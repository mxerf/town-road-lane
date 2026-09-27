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
    /// Keeps one vanilla SecondaryLane sublane (tagged <see cref="TRLSegmentLink"/>) per visible
    /// <see cref="MarkingSegment"/> and draw pass: creates the missing ones and deletes those whose
    /// segment is gone or hidden. Each segment's curve is the full line from
    /// <see cref="MarkingCurveBuilder"/> cut to the segment's range, so it matches the topology
    /// math exactly.
    ///
    /// The diff compares keys only. A line whose style or road changed gets new sublanes because
    /// <see cref="CustomSecondaryLaneSystem"/> deletes all lanes of an updated node that has user
    /// lines, including ours, and this system respawns them on the next frame.
    /// </summary>
    [UpdateAfter(typeof(MarkingTopologySystem))]
    public partial class MarkingSegmentEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithLines;
        private EntityQuery _ourSubLanes;
        private readonly System.Text.StringBuilder _churnDetail = new System.Text.StringBuilder();
        // One warning per (node, line) over the PathNode slot capacity: the check fails every
        // tick for such a line, so unthrottled logging would flood.
        private readonly HashSet<(Entity, int)> _slotOverflowWarned = new HashSet<(Entity, int)>();
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
        }

        protected override void OnUpdate()
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // Wanted set: (node, lineIndex, segmentIndex, passIndex) for every visible segment.
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
                    var style = (MarkingStyle)seg.style;
                    int passes = style.DrawPasses();
                    for (int p = 0; p < passes; p++)
                        wanted.Add((node, seg.lineIndex, segIdx, p));
                }
            }
            nodes.Dispose();

            // Delete unwanted and duplicate sublanes.
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

            // Spawn the rest.
            int created = 0;
            if (wanted.Count > 0)
            {
                // Solid is required: it is the fallback for any style whose clone is not ready.
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

                            // Style belongs to the segment, so pieces of one line can differ.
                            var style = (MarkingStyle)seg.style;
                            if (!prefabByStyle.TryGetValue(style, out var pair))
                            {
                                if (!TryResolveStylePrefab(style, isNA, out pair))
                                    pair = solidPair;
                                prefabByStyle[style] = pair;
                            }

                            // Multi-pass styles stack copies on the same curve to boost alpha.
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
                log.Debug($"segment-emission: +{created} created, -{deleted} deleted (wanted={wanted.Count} unmet, existing={_ourSubLanes.CalculateEntityCount()})");
                if (_churnDetail.Length > 0 && created <= 12)
                    log.Debug($"segment-emission detail: {_churnDetail}");
            }
            _churnDetail.Clear();

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        private Entity SpawnSegmentSublane(EntityCommandBuffer ecb, Entity node, int lineIndex, int segmentIndex,
            int passIndex, Bezier4x3 fullBezier, float tStart, float tEnd, Entity prefab, EntityArchetype archetype)
        {
            Bezier4x3 segBez = MathUtils.Cut(fullBezier, new float2(tStart, tEnd));

            // PathNode slots start at 32768, clear of the vanilla lanes' 0..N-1: 512 per line,
            // 16 per segment, 4 per pass. Past 32 segments a line runs into the next line's range,
            // and past 64 lines the ushort wraps into vanilla slots, where a collision crashes the
            // pathfinder. Such a sublane is skipped, so that piece is simply not drawn.
            int slotBase = 32768 + lineIndex * 512 + segmentIndex * 16 + passIndex * 4;
            if (segmentIndex >= 32 || lineIndex >= 64 || slotBase + 2 > ushort.MaxValue)
            {
                if (_slotOverflowWarned.Add((node, lineIndex)))
                    log.Warn($"segment-emission node#{node.Index}: line {lineIndex} segment {segmentIndex} exceeds PathNode slot capacity (max 64 lines × 32 segments per node) — sublane skipped");
                return Entity.Null;
            }
            ushort idxBase = (ushort)slotBase;
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
            // The owner node is deliberately not marked Updated: that cascades through
            // LaneReferencesSystem into a runaway spawn loop.

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

        /// <summary>Returns false while the clone for (style, theme) is not registered or its
        /// NetLaneArchetypeData is not baked yet.</summary>
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
