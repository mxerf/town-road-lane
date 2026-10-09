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
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.Prefabs;
using TownRoadLane.Systems.Topology;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane.Systems.Emission
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
    ///
    /// The diff only runs on frames where something could have changed it: a segment or line
    /// buffer was written, the number of our sublanes changed behind our back (the case above),
    /// or the last pass had to wait for a prefab or for road geometry. Right after a load the
    /// edges have no geometry for a few frames, so no line curve can be built yet; nothing in
    /// the marking buffers changes when it arrives, so the pass retries on its own while any
    /// such edge exists. A line whose road is gone for good does not keep it retrying.
    /// It runs after MarkingTopologySystem via the UpdateSystem registration in Mod.OnLoad.
    /// </summary>
    public partial class MarkingSegmentEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithLines;
        private EntityQuery _ourSubLanes;
        private EntityQuery _changedSegments;
        private EntityQuery _changedLines;
        // Our live sublanes right after the last pass. A different count on a later frame means
        // someone else created or deleted some.
        private int _subLaneCountAfterPass = -1;
        private bool _passPending = true;
        private int _lastWaitingForCurve;
        // Right after a load any unbuildable line is retried for a while, whatever the reason;
        // after that only lines whose roads exist but aren't ready yet.
        private float _retryAnyUntil;
        private const float kPostLoadRetrySeconds = 30f;
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
            // GetEntityQuery returns the cached query for an identical component set, so a
            // change-filtered query must differ from the unfiltered ones or the filter lands on both.
            _changedSegments = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MarkingSegment>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            _changedSegments.SetChangedVersionFilter(ComponentType.ReadOnly<MarkingSegment>());
            _changedLines = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MarkingLine>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            _changedLines.SetChangedVersionFilter(ComponentType.ReadOnly<MarkingLine>());
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            _passPending = true;
            _retryAnyUntil = UnityEngine.Time.realtimeSinceStartup + kPostLoadRetrySeconds;
        }

        private bool PassNeeded()
        {
            return _passPending
                || !_changedSegments.IsEmpty
                || !_changedLines.IsEmpty
                || _ourSubLanes.CalculateEntityCount() != _subLaneCountAfterPass;
        }

        protected override void OnUpdate()
        {
            if (!PassNeeded()) return;
            _passPending = false;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // Wanted sublanes, keyed like TRLSegmentLink, mapped to the segment's buffer index.
            var wanted = new Dictionary<(Entity, int, int, int), int>();
            using (var nodes = _nodesWithLines.ToEntityArray(Allocator.Temp))
            {
                for (int n = 0; n < nodes.Length; n++)
                    CollectWanted(nodes[n], wanted);
            }

            // Keep one sublane per wanted key; delete the rest, duplicates included.
            int deleted = 0;
            using (var existing = _ourSubLanes.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < existing.Length; i++)
                {
                    var link = EntityManager.GetComponentData<TRLSegmentLink>(existing[i]);
                    if (wanted.Remove((link.node, link.lineIndex, link.segmentIndex, link.passIndex))) continue;
                    ecb.AddComponent<Deleted>(existing[i]);
                    deleted++;
                }
            }

            // Spawn the rest.
            int created = 0;
            if (wanted.Count > 0)
            {
                // Solid is required: it is the fallback for any style whose clone is not ready.
                bool isNA = IsNATheme();
                if (!TryResolveStylePrefab(MarkingStyle.Solid, isNA, out var solidPair))
                {
                    log.Warn("segment-emission: solid prefab not resolved yet — deferring entire tick");
                    _passPending = true;
                }
                else
                {
                    var prefabByStyle = new Dictionary<MarkingStyle, (Entity prefab, EntityArchetype arch)>
                    {
                        [MarkingStyle.Solid] = solidPair,
                    };
                    var curvesByNode = new Dictionary<Entity, Bezier4x3?[]>();
                    int waitingForCurve = 0;
                    foreach (var entry in wanted)
                    {
                        var (node, lineIndex, segIdx, pass) = entry.Key;
                        if (!curvesByNode.TryGetValue(node, out var curves))
                            curvesByNode[node] = curves = BuildLineCurves(node);
                        if (!(curves[lineIndex] is Bezier4x3 curve))
                        {
                            if (UnityEngine.Time.realtimeSinceStartup < _retryAnyUntil || IsWaitingForRoad(node, lineIndex))
                                waitingForCurve++;
                            continue;
                        }

                        var seg = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true)[entry.Value];
                        // Style belongs to the segment, so pieces of one line can differ.
                        var style = (MarkingStyle)seg.style;
                        if (!prefabByStyle.TryGetValue(style, out var pair))
                        {
                            if (!TryResolveStylePrefab(style, isNA, out pair))
                                pair = solidPair;
                            prefabByStyle[style] = pair;
                        }

                        var spawned = SpawnSegmentSublane(ecb, node, lineIndex, segIdx, pass,
                            curve, seg.tStart, seg.tEnd, pair.prefab, pair.arch);
                        if (spawned == Entity.Null) continue;
                        created++;
                        // Churn diagnostics: a small steady trickle of re-creations means
                        // something keeps deleting these exact sublanes.
                        if (created <= 12)
                            _churnDetail.Append(created > 1 ? ", " : "").Append($"node#{node.Index} L{lineIndex} S{segIdx} P{pass} {style}");
                    }
                    if (waitingForCurve > 0) _passPending = true;
                    if (waitingForCurve != _lastWaitingForCurve)
                        log.Debug($"segment-emission: {waitingForCurve} sublane(s) waiting for a buildable curve");
                    _lastWaitingForCurve = waitingForCurve;
                }
            }

            if (created > 0 || deleted > 0)
            {
                log.Debug($"segment-emission: +{created} created, -{deleted} deleted (wanted={wanted.Count - created} unmet, existing={_ourSubLanes.CalculateEntityCount()})");
                if (_churnDetail.Length > 0 && created <= 12)
                    log.Debug($"segment-emission detail: {_churnDetail}");
            }
            _churnDetail.Clear();

            ecb.Playback(EntityManager);
            ecb.Dispose();
            _subLaneCountAfterPass = _ourSubLanes.CalculateEntityCount();
        }

        /// <summary>Adds a key per visible segment and draw pass. A sublane's segment index counts
        /// only the visible segments of its line.</summary>
        private void CollectWanted(Entity node, Dictionary<(Entity, int, int, int), int> wanted)
        {
            if (!EntityManager.HasBuffer<MarkingSegment>(node) || !EntityManager.HasBuffer<MarkingLine>(node)) return;
            var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
            int lineCount = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true).Length;
            var visibleInLine = new int[lineCount];
            for (int s = 0; s < segs.Length; s++)
            {
                var seg = segs[s];
                if (!seg.visible) continue;
                if (seg.lineIndex < 0 || seg.lineIndex >= lineCount) continue;
                int segIdx = visibleInLine[seg.lineIndex]++;
                // Multi-pass styles stack copies on the same curve to boost alpha.
                int passes = ((MarkingStyle)seg.style).DrawPasses();
                for (int p = 0; p < passes; p++)
                    wanted[(node, seg.lineIndex, segIdx, p)] = s;
            }
        }

        /// <summary>The line's roads exist but have no geometry yet (the first frames after a
        /// load), so its curve will become buildable without any marking change.</summary>
        private bool IsWaitingForRoad(Entity node, int lineIndex)
        {
            var line = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true)[lineIndex];
            return MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, line.sourceEdge)
                || MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, line.targetEdge);
        }

        /// <summary>Full curve of every line on the node, null where the line can't be built.</summary>
        private Bezier4x3?[] BuildLineCurves(Entity node)
        {
            var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            var curves = new Bezier4x3?[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                if (MarkingCurveBuilder.TryBuild(endpoints, lines[i], out var bez)) curves[i] = bez;
            return curves;
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
                m_StartNode = new PathNode(new PathNode(node, idxBase), secondaryNode: true),
                m_MiddleNode = new PathNode(new PathNode(node, (ushort)(idxBase + 1)), secondaryNode: true),
                m_EndNode = new PathNode(new PathNode(node, (ushort)(idxBase + 2)), secondaryNode: true),
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
