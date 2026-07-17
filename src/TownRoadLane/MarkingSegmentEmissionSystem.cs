using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane
{
    /// <summary>
    /// Stage 5b successor of <see cref="MarkingPairEmissionSystem"/>. Diffs the
    /// (<see cref="MarkingLine"/>, <see cref="MarkingSegment"/>) buffers on a node against
    /// already-spawned sublanes (tagged with <see cref="TRLSegmentLink"/>) and reconciles:
    ///
    ///   - visible segment in buffer, no matching sublane     → create sublane
    ///   - segment gone or hidden, sublane still has TRLSegmentLink → delete sublane
    ///   - visible segment exists + sublane exists            → no-op
    ///
    /// Also one-shot cleans up legacy <see cref="TRLPairLink"/> sublanes left over from the
    /// pre-5b emission system — those entities reference MarkingPair which the migration
    /// removes, so they have nothing to anchor to and must go.
    ///
    /// Per-segment Bezier comes from <see cref="MarkingCurveBuilder"/> for the full line,
    /// then cut to the segment's parameter range with <see cref="MathUtils.Cut(Bezier4x3, float2)"/>.
    /// This guarantees the rendered geometry matches the topology-recompute math exactly.
    /// </summary>
    [UpdateAfter(typeof(MarkingTopologySystem))]
    public partial class MarkingSegmentEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithLines;
        private EntityQuery _ourSubLanes;
        private EntityQuery _legacyPairSubLanes;
        private readonly System.Text.StringBuilder _churnDetail = new System.Text.StringBuilder();
        private PrefabSystem _prefabSystem;
        private EdgeLineCloneSystem _edgeLineSys;
        private CityConfigurationSystem _cityConfig;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _edgeLineSys = World.GetOrCreateSystemManaged<EdgeLineCloneSystem>();
            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            _nodesWithLines = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MarkingLine>(), ComponentType.ReadOnly<MarkingSegment>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            _ourSubLanes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TRLSegmentLink>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _legacyPairSubLanes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TRLPairLink>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 0. One-shot cleanup of pre-5b sublanes. Their MarkingPair source is gone after
            //    migration, so they'll never be re-anchored — just delete and let
            //    MarkingSegmentEmissionSystem re-spawn from the new segment buffer.
            if (_legacyPairSubLanes.CalculateEntityCount() > 0)
            {
                var legacy = _legacyPairSubLanes.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < legacy.Length; i++) ecb.AddComponent<Deleted>(legacy[i]);
                log.Info($"segment-emission: GC'd {legacy.Length} legacy TRLPairLink sublane(s)");
                legacy.Dispose();
            }

            // 1. Build the "wanted" set: (node, lineIndex, segmentIndex, passIndex) for every
            // visible segment. Some styles need multiple draw passes — see MarkingStyle.DrawPasses.
            var wanted = new HashSet<(Entity, int, int, int)>();
            var nodes = _nodesWithLines.ToEntityArray(Allocator.Temp);
            for (int n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                if (!EntityManager.HasBuffer<MarkingSegment>(node)) continue;
                if (!EntityManager.HasBuffer<MarkingLine>(node)) continue;
                var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var perLineCounter = new Dictionary<int, int>();
                for (int s = 0; s < segs.Length; s++)
                {
                    var seg = segs[s];
                    if (!seg.visible) continue;
                    if (seg.lineIndex < 0 || seg.lineIndex >= lines.Length) continue;
                    int segIdx = perLineCounter.TryGetValue(seg.lineIndex, out var c) ? c : 0;
                    perLineCounter[seg.lineIndex] = segIdx + 1;
                    // Style now lives ON the segment (Stage 5d) — let each piece of a line
                    // pick its own visual independent of the line-level default.
                    var style = (MarkingStyle)seg.style;
                    int passes = style.DrawPasses();
                    for (int p = 0; p < passes; p++)
                        wanted.Add((node, seg.lineIndex, segIdx, p));
                }
            }
            nodes.Dispose();

            // 2. Diff existing sublanes against wanted set.
            var existing = _ourSubLanes.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<(Entity, int, int, int)>();
            int deleted = 0;
            for (int i = 0; i < existing.Length; i++)
            {
                var sub = existing[i];
                var link = EntityManager.GetComponentData<TRLSegmentLink>(sub);
                var key = (link.node, link.lineIndex, link.segmentIndex, link.passIndex);
                if (!wanted.Contains(key) || seen.Contains(key))
                {
                    ecb.AddComponent<Deleted>(sub);
                    deleted++;
                    continue;
                }
                seen.Add(key);
                wanted.Remove(key);
            }
            existing.Dispose();

            // 3. Spawn remaining wanted.
            int created = 0;
            if (wanted.Count > 0)
            {
                // Resolve every style we might need this tick. Cached per (style, theme) so we
                // don't re-query NetLaneArchetypeData per segment. Solid always required as the
                // fallback when a line's style isn't loaded yet.
                bool isNA = IsNATheme();
                var prefabByStyle = new Dictionary<MarkingStyle, (Entity prefab, EntityArchetype arch)>();
                if (!TryResolveStylePrefab(MarkingStyle.Solid, isNA, out var solidPair))
                {
                    log.Warn("segment-emission: solid prefab not resolved yet — deferring entire tick");
                }
                else
                {
                    prefabByStyle[MarkingStyle.Solid] = solidPair;
                    nodes = _nodesWithLines.ToEntityArray(Allocator.Temp);
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        var node = nodes[n];
                        if (!EntityManager.HasBuffer<MarkingLine>(node)) continue;
                        if (!EntityManager.HasBuffer<MarkingSegment>(node)) continue;
                        var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                        var segs  = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);

                        var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);

                        var lineCount = lines.Length;
                        var fullBeziers = new Bezier4x3[lineCount];
                        var bezValid = new bool[lineCount];
                        for (int i = 0; i < lineCount; i++)
                        {
                            if (MarkingCurveBuilder.TryBuild(endpoints, lines[i], out var bez))
                            {
                                fullBeziers[i] = bez;
                                bezValid[i] = true;
                            }
                        }

                        var perLineCounter = new Dictionary<int, int>();
                        for (int s = 0; s < segs.Length; s++)
                        {
                            var seg = segs[s];
                            if (!seg.visible) continue;
                            int segIdx = perLineCounter.TryGetValue(seg.lineIndex, out var c) ? c : 0;
                            perLineCounter[seg.lineIndex] = segIdx + 1;

                            if (seg.lineIndex < 0 || seg.lineIndex >= lineCount) continue;
                            if (!bezValid[seg.lineIndex]) continue;

                            // Per-segment prefab lookup. Style is owned by the segment itself
                            // (Stage 5d) — different pieces of one line may render differently.
                            // Lazy-resolve into the cache: FIRST segment of each style pays for
                            // the archetype lookup, every later one is a dictionary hit.
                            var style = (MarkingStyle)seg.style;
                            if (!prefabByStyle.TryGetValue(style, out var pair))
                            {
                                if (!TryResolveStylePrefab(style, isNA, out pair))
                                    pair = solidPair;
                                prefabByStyle[style] = pair;
                            }

                            // Spawn one sublane per draw pass. Multi-pass styles overlap copies
                            // on the same geometry to boost alpha — see MarkingStyle.DrawPasses.
                            int passes = style.DrawPasses();
                            for (int p = 0; p < passes; p++)
                            {
                                var key = (node, seg.lineIndex, segIdx, p);
                                if (!wanted.Contains(key)) continue;
                                wanted.Remove(key);
                                var spawned = SpawnSegmentSublane(ecb, node, seg.lineIndex, segIdx, p,
                                    fullBeziers[seg.lineIndex], seg.tStart, seg.tEnd, pair.prefab, pair.arch);
                                if (spawned != Entity.Null)
                                {
                                    created++;
                                    // Churn diagnostics: a small steady trickle of re-creations
                                    // means something keeps deleting these exact sublanes.
                                    if (created <= 12)
                                        _churnDetail.Append(created > 1 ? ", " : "").Append($"node#{node.Index} L{seg.lineIndex} S{segIdx} P{p} {style}");
                                }
                            }
                        }
                    }
                    nodes.Dispose();
                }
            }

            if (created > 0 || deleted > 0)
            {
                log.Info($"segment-emission: +{created} created, -{deleted} deleted (wanted={wanted.Count} unmet, existing={_ourSubLanes.CalculateEntityCount()})");
                if (_churnDetail.Length > 0 && created <= 12)
                    log.Info($"segment-emission detail: {_churnDetail}");
            }
            _churnDetail.Clear();

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        private Entity SpawnSegmentSublane(EntityCommandBuffer ecb, Entity node, int lineIndex, int segmentIndex,
            int passIndex, Bezier4x3 fullBezier, float tStart, float tEnd, Entity prefab, EntityArchetype archetype)
        {
            // Cut the full-line Bezier to the segment's parameter range. MathUtils.Cut takes
            // float2(start, end) — vanilla uses this exact API for navigation curve trimming.
            Bezier4x3 segBez = MathUtils.Cut(fullBezier, new float2(tStart, tEnd));

            // PathNode slots: base = 32768 + lineIndex*512 + segmentIndex*16 + passIndex*4.
            // Each segment reserves 16 slots → up to 4 passes of 4 PathNode slots each. 512 slots
            // per line → up to 32 segments per line before colliding with the next line.
            // Vanilla primary lanes occupy 0..N-1; 32768+ keeps us clear.
            ushort idxBase = (ushort)(32768 + lineIndex * 512 + segmentIndex * 16 + passIndex * 4);
            var lane = new Lane
            {
                m_StartNode  = new PathNode(new PathNode(node, idxBase),               secondaryNode: true),
                m_MiddleNode = new PathNode(new PathNode(node, (ushort)(idxBase + 1)), secondaryNode: true),
                m_EndNode    = new PathNode(new PathNode(node, (ushort)(idxBase + 2)), secondaryNode: true),
            };

            Entity e = ecb.CreateEntity(archetype);
            ecb.SetComponent(e, new PrefabRef(prefab));
            ecb.SetComponent(e, lane);
            ecb.SetComponent(e, new Curve { m_Bezier = segBez, m_Length = MathUtils.Length(segBez) });
            ecb.AddComponent(e, new Owner { m_Owner = node });
            ecb.AddComponent(e, default(Elevation));
            ecb.AddComponent(e, new TRLSegmentLink { node = node, lineIndex = lineIndex, segmentIndex = segmentIndex, passIndex = passIndex });
            ecb.AddComponent(e, default(Created));
            ecb.AddComponent(e, default(Updated));
            // NOT marking owner Updated — same reason as the old PairEmissionSystem (cascade
            // through LaneReferencesSystem caused runaway spawn loops; see commit cdc96a5).

            return e;
        }

        /// <summary>Whether the city's default theme is North American. Both EU and NA edge-line
        /// prefab clones exist; this picks which one to use for every emission this tick.</summary>
        private bool IsNATheme()
        {
            var theme = _cityConfig.defaultTheme;
            if (theme == Entity.Null) return false;
            if (_prefabSystem.TryGetPrefab<PrefabBase>(theme, out var themePrefab) && themePrefab != null)
            {
                var n = themePrefab.name;
                if (!string.IsNullOrEmpty(n) && (n.Contains("North American") || n.StartsWith("NA "))) return true;
            }
            return false;
        }

        /// <summary>Look up the (prefab, archetype) pair for a given (style, theme). Returns false
        /// when the clone isn't loaded yet or the prefab hasn't been baked through
        /// NetLaneArchetypeData by PrefabSystem. Caller falls back to Solid in that case.</summary>
        private bool TryResolveStylePrefab(MarkingStyle style, bool isNA, out (Entity prefab, EntityArchetype arch) pair)
        {
            pair = default;
            if (_edgeLineSys == null) return false;
            Entity prefab = _edgeLineSys.GetCloneEntity(style, isNA);
            if (prefab == Entity.Null) return false;
            if (!EntityManager.HasComponent<NetLaneArchetypeData>(prefab)) return false;
            pair = (prefab, EntityManager.GetComponentData<NetLaneArchetypeData>(prefab).m_LaneArchetype);
            return true;
        }
    }
}
