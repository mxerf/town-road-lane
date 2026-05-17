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
                var prefab = PickPrefab();
                if (prefab == Entity.Null)
                {
                    log.Warn("segment-emission: no edge-line clone prefab resolved yet — deferring");
                }
                else if (!EntityManager.HasComponent<NetLaneArchetypeData>(prefab))
                {
                    log.Warn($"segment-emission: prefab #{prefab.Index} has no NetLaneArchetypeData yet — deferring");
                }
                else
                {
                    var arch = EntityManager.GetComponentData<NetLaneArchetypeData>(prefab).m_LaneArchetype;
                    nodes = _nodesWithLines.ToEntityArray(Allocator.Temp);
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        var node = nodes[n];
                        if (!EntityManager.HasBuffer<MarkingLine>(node)) continue;
                        if (!EntityManager.HasBuffer<MarkingSegment>(node)) continue;
                        var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                        var segs  = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);

                        // Pre-extract endpoints once per node (extractor walks every ConnectedEdge).
                        var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);

                        // Build full Beziers per line once — cheaper than once per segment.
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

                        // Same per-line counter the wanted set used.
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
                            var spawned = SpawnSegmentSublane(ecb, node, seg.lineIndex, segIdx,
                                fullBeziers[seg.lineIndex], seg.tStart, seg.tEnd, prefab, arch);
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

        private Entity PickPrefab()
        {
            if (_edgeLineSys == null) return Entity.Null;
            var theme = _cityConfig.defaultTheme;
            if (theme == Entity.Null) return _edgeLineSys.CloneEntityEU;
            if (_prefabSystem.TryGetPrefab<PrefabBase>(theme, out var themePrefab) && themePrefab != null)
            {
                var n = themePrefab.name;
                if (!string.IsNullOrEmpty(n) && (n.Contains("North American") || n.StartsWith("NA "))) return _edgeLineSys.CloneEntityNA;
            }
            return _edgeLineSys.CloneEntityEU;
        }
    }
}
