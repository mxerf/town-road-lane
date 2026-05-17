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
        private PrefabSystem _prefabSystem;
        private EdgeLineCloneSystem _edgeLineSys;
        private CityConfigurationSystem _cityConfig;

        private int _heartbeatTicks;

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
            _heartbeatTicks++;
            if (_heartbeatTicks % 120 == 1)
                log.Info($"[segment-emission] heartbeat tick={_heartbeatTicks} nodesWithLines={_nodesWithLines.CalculateEntityCount()} ourSubLanes={_ourSubLanes.CalculateEntityCount()} legacyPair={_legacyPairSubLanes.CalculateEntityCount()}");

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

            // 1. Build the "wanted" set: (node, lineIndex, segmentIndex) for every visible segment.
            var wanted = new HashSet<(Entity, int, int)>();
            var nodes = _nodesWithLines.ToEntityArray(Allocator.Temp);
            for (int n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                if (!EntityManager.HasBuffer<MarkingSegment>(node)) continue;
                var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                // Per-line counter so segmentIndex is dense per line (0..K-1 inside one line).
                // Without this, segmentIndex would just be the flat buffer position, which is
                // also unique but harder to read in logs.
                var perLineCounter = new Dictionary<int, int>();
                for (int s = 0; s < segs.Length; s++)
                {
                    var seg = segs[s];
                    if (!seg.visible) continue;
                    int segIdx = perLineCounter.TryGetValue(seg.lineIndex, out var c) ? c : 0;
                    perLineCounter[seg.lineIndex] = segIdx + 1;
                    wanted.Add((node, seg.lineIndex, segIdx));
                }
            }
            nodes.Dispose();

            // 2. Diff existing sublanes against wanted set.
            var existing = _ourSubLanes.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<(Entity, int, int)>();
            int deleted = 0;
            for (int i = 0; i < existing.Length; i++)
            {
                var sub = existing[i];
                var link = EntityManager.GetComponentData<TRLSegmentLink>(sub);
                var key = (link.node, link.lineIndex, link.segmentIndex);
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

                            var key = (node, seg.lineIndex, segIdx);
                            if (!wanted.Contains(key)) continue;
                            wanted.Remove(key);

                            if (seg.lineIndex < 0 || seg.lineIndex >= lineCount) continue;
                            if (!bezValid[seg.lineIndex]) continue;

                            // Per-segment prefab lookup via the line's style. Lazy-resolve into
                            // the cache so the FIRST line of a given style pays for the archetype
                            // lookup and every later segment in the same style is a dictionary hit.
                            var style = (MarkingStyle)lines[seg.lineIndex].style;
                            if (!prefabByStyle.TryGetValue(style, out var pair))
                            {
                                if (!TryResolveStylePrefab(style, isNA, out pair))
                                {
                                    // Unknown / not-loaded style → degrade to solid. Old saves
                                    // with future-style numbers and missed clones both land here.
                                    pair = solidPair;
                                }
                                prefabByStyle[style] = pair;
                            }

                            var spawned = SpawnSegmentSublane(ecb, node, seg.lineIndex, segIdx,
                                fullBeziers[seg.lineIndex], seg.tStart, seg.tEnd, pair.prefab, pair.arch);
                            if (spawned != Entity.Null) created++;
                        }
                    }
                    nodes.Dispose();
                }
            }

            if (created > 0 || deleted > 0)
                log.Info($"segment-emission: +{created} created, -{deleted} deleted (wanted={wanted.Count} unmet, existing={_ourSubLanes.CalculateEntityCount()})");

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        private Entity SpawnSegmentSublane(EntityCommandBuffer ecb, Entity node, int lineIndex, int segmentIndex,
            Bezier4x3 fullBezier, float tStart, float tEnd, Entity prefab, EntityArchetype archetype)
        {
            // Cut the full-line Bezier to the segment's parameter range. MathUtils.Cut takes
            // float2(start, end) — vanilla uses this exact API for navigation curve trimming.
            Bezier4x3 segBez = MathUtils.Cut(fullBezier, new float2(tStart, tEnd));

            // PathNode slots per the v2 plan: base = 32768 + lineIndex*256 + segmentIndex*4.
            // Allows up to 256 segments per line and ~14 lines per node before colliding with
            // ushort wrap — far above anything realistic (typical 4-way: 4-6 lines, 8 segments).
            // Vanilla primary lanes occupy 0..N-1, so 32768+ is well clear.
            ushort idxBase = (ushort)(32768 + lineIndex * 256 + segmentIndex * 4);
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
            ecb.AddComponent(e, new TRLSegmentLink { node = node, lineIndex = lineIndex, segmentIndex = segmentIndex });
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
