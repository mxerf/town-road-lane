using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Splits each node's <see cref="MarkingLine"/>s into <see cref="MarkingSegment"/>s at
    /// their mutual crossings. When a node's lines change, it builds every line's Bezier,
    /// intersects all pairs, filters out false crossings, and rewrites the segment buffer. A new
    /// segment takes visibility and style from the old segment that contained its midpoint, so
    /// adding a line does not undo the user's per-segment edits. The node is then tagged
    /// Updated for <see cref="MarkingSegmentEmissionSystem"/>.
    ///
    /// Change detection compares a hash of the line geometry (endpoints and curvature, not
    /// style) against <see cref="MarkingTopologyState"/>.
    /// </summary>
    [UpdateAfter(typeof(MarkingPairMigrationSystem))]
    [UpdateBefore(typeof(MarkingSegmentEmissionSystem))]
    public partial class MarkingTopologySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithLines;

        // Defaults of the four split-filter thresholds, which are user settings. They are read on
        // every node rebuild, so a change reaches a junction when its lines are next edited, and
        // every junction after a load (MarkingTopologyState is not saved).

        // Crossings within this distance of any line endpoint are ignored. Two lines leaving the
        // same dot overlap for the first metres and report a string of false hits there. 2 m is
        // about two thirds of a 3 m lane, small enough to keep real crossings.
        public const float kDefaultEndpointMarginM = 2.0f;

        // Minimum crossing angle for a split. Shallower contacts are near-tangent grazes (merge
        // lanes) that only produce slivers. MarkingIntersectionExtractor deliberately does not
        // apply this filter: island tips are shallow crossings and must stay clickable as area
        // anchors.
        public const int kDefaultMinCrossingAngleDeg = 8;
        // Hits of one line pair closer than this collapse to the first; a graze reports a cluster.
        public const float kDefaultHitClusterM = 1.5f;

        // Shorter segments are merged into a neighbour, catching grazes that pass the other
        // filters (two similar curves touching over half a metre, for example).
        public const float kDefaultMinSegmentLengthM = 1.0f;

        protected override void OnCreate()
        {
            base.OnCreate();
            // Temp nodes are the road tool's preview copies, buffers included. Writing to them or
            // tagging them Updated while the tool applies crashes the game in native code.
            _nodesWithLines = GetEntityQuery(
                ComponentType.ReadOnly<MarkingLine>(),
                ComponentType.ReadOnly<Node>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            RequireForUpdate(_nodesWithLines);
        }

        protected override void OnUpdate()
        {
            using var nodes = _nodesWithLines.ToEntityArray(Allocator.Temp);
            int rewritten = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (RecomputeIfChanged(nodes[i])) rewritten++;
            }
            if (rewritten > 0) log.Info($"MarkingTopologySystem: recomputed segments on {rewritten} node(s)");
        }

        private bool RecomputeIfChanged(Entity node)
        {
            if (!EntityManager.HasBuffer<MarkingLine>(node)) return false;
            var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);

            if (lines.Length == 0 && !EntityManager.HasBuffer<MarkingSegment>(node))
                return false;

            int newHash = HashLines(lines);
            int oldHash = EntityManager.HasComponent<MarkingTopologyState>(node)
                ? EntityManager.GetComponentData<MarkingTopologyState>(node).linesHash
                : 0;
            if (newHash == oldHash && EntityManager.HasBuffer<MarkingSegment>(node))
                return false;

            // Copy the lines first: structural changes below (AddBuffer, AddComponent) invalidate
            // the buffer handle.
            int lineCount = lines.Length;
            var linesSnapshot = new NativeArray<MarkingLine>(lineCount, Allocator.Temp);
            for (int i = 0; i < lineCount; i++) linesSnapshot[i] = lines[i];

            // Old segments per line, for inheriting visibility and style.
            var oldSegmentsByLine = new List<List<MarkingSegment>>(lineCount);
            for (int i = 0; i < lineCount; i++) oldSegmentsByLine.Add(new List<MarkingSegment>());
            if (EntityManager.HasBuffer<MarkingSegment>(node))
            {
                var oldSegs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                for (int i = 0; i < oldSegs.Length; i++)
                {
                    var s = oldSegs[i];
                    if (s.lineIndex >= 0 && s.lineIndex < lineCount)
                        oldSegmentsByLine[s.lineIndex].Add(s);
                }
            }

            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            var beziers = new NativeArray<Bezier4x3>(lineCount, Allocator.Temp);
            var bezierValid = new NativeArray<bool>(lineCount, Allocator.Temp);
            for (int i = 0; i < lineCount; i++)
            {
                if (MarkingCurveBuilder.TryBuild(endpoints, linesSnapshot[i], out var bez))
                {
                    beziers[i] = bez;
                    bezierValid[i] = true;
                }
            }

            // On the first tick after a load, Composition and EdgeGeometry are still zeroed (they
            // are IEmptySerializable and get refilled in Modification3/4, after this
            // Modification1 system). Every TryBuild then fails, and rewriting now would collapse
            // each line to one [0, 1] segment and lose the saved per-segment style and
            // visibility. If a failed line references an edge that exists but isn't ready, skip
            // the node without writing anything and retry next tick.
            for (int i = 0; i < lineCount; i++)
            {
                if (bezierValid[i]) continue;
                if (MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, linesSnapshot[i].sourceEdge)
                    || MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, linesSnapshot[i].targetEdge))
                {
                    linesSnapshot.Dispose();
                    beziers.Dispose();
                    bezierValid.Dispose();
                    return false;
                }
            }

            // Split parameters per line, starting with the ends 0 and 1.
            var boundaries = new List<List<float>>(lineCount);
            for (int i = 0; i < lineCount; i++)
            {
                var b = new List<float>(4);
                b.Add(0f); b.Add(1f);
                boundaries.Add(b);
            }
            // Settings is null only during early startup, before any node can have lines.
            var st = Mod.Settings;
            float endpointMarginM = st?.SegmentAnchorDeadZoneM ?? kDefaultEndpointMarginM;
            float minCrossingSin = math.sin(math.radians((float)(st?.SegmentMinCrossingAngleDeg ?? kDefaultMinCrossingAngleDeg)));
            float hitClusterM = st?.SegmentHitClusterM ?? kDefaultHitClusterM;
            float minSegmentLengthM = st?.SegmentMinLengthM ?? kDefaultMinSegmentLengthM;

            // All pairs; a node rarely has more than about 20 lines.
            for (int i = 0; i < lineCount; i++)
            {
                if (!bezierValid[i]) continue;
                for (int j = i + 1; j < lineCount; j++)
                {
                    if (!bezierValid[j]) continue;
                    var hits = BezierIntersection.Intersect(beziers[i], beziers[j]);

                    // Drop hits near an endpoint and shallow grazes, then keep only the first hit
                    // of each cluster along line i.
                    var kept = new List<BezierIntersection.Hit>(hits.Count);
                    for (int h = 0; h < hits.Count; h++)
                    {
                        var hit = hits[h];
                        if (IsNearAnyEndpoint(hit.point, beziers[i], beziers[j], endpointMarginM)) continue;
                        var ta = MathUtils.Tangent(beziers[i], hit.tA).xz;
                        var tb = MathUtils.Tangent(beziers[j], hit.tB).xz;
                        float denom = math.max(math.length(ta) * math.length(tb), 1e-6f);
                        float sinAngle = math.abs(ta.x * tb.y - ta.y * tb.x) / denom;
                        if (sinAngle < minCrossingSin) continue;
                        kept.Add(hit);
                    }
                    kept.Sort((x, y) => x.tA.CompareTo(y.tA));
                    float3 lastPoint = default;
                    bool haveLast = false;
                    for (int h = 0; h < kept.Count; h++)
                    {
                        var hit = kept[h];
                        if (haveLast && math.distancesq(hit.point.xz, lastPoint.xz) < hitClusterM * hitClusterM)
                            continue;
                        lastPoint = hit.point;
                        haveLast = true;
                        if (hit.tA > 0.01f && hit.tA < 0.99f) boundaries[i].Add(hit.tA);
                        if (hit.tB > 0.01f && hit.tB < 0.99f) boundaries[j].Add(hit.tB);
                    }
                }
            }

            // Several lines can cross one line at the same t.
            for (int i = 0; i < lineCount; i++)
            {
                boundaries[i].Sort();
                DedupeSortedInPlace(boundaries[i], epsilon: 0.005f);
            }

            for (int i = 0; i < lineCount; i++)
            {
                if (!bezierValid[i]) continue;
                EnforceMinSegmentLength(boundaries[i], beziers[i], minSegmentLengthM);
            }

            var newSegments = new List<MarkingSegment>(lineCount * 2);
            for (int i = 0; i < lineCount; i++)
            {
                // The line can't be resolved (edge demolished, or its gap gone after a road
                // upgrade), so nothing renders. Keep its old segments as they are, so the user's
                // edits survive if the line becomes valid again.
                if (!bezierValid[i])
                {
                    var carried = oldSegmentsByLine[i];
                    for (int c = 0; c < carried.Count; c++) newSegments.Add(carried[c]);
                    continue;
                }
                var bs = boundaries[i];
                var oldSegs = oldSegmentsByLine[i];
                int lineDefaultStyle = linesSnapshot[i].style;
                for (int s = 0; s < bs.Count - 1; s++)
                {
                    float tStart = bs[s];
                    float tEnd = bs[s + 1];
                    float tMid = (tStart + tEnd) * 0.5f;
                    bool visible = LookupInheritedVisibility(oldSegs, tMid, defaultVisible: true);
                    int style = LookupInheritedStyle(oldSegs, tMid, defaultStyle: lineDefaultStyle);
                    newSegments.Add(new MarkingSegment
                    {
                        lineIndex = i,
                        tStart = tStart,
                        tEnd = tEnd,
                        visible = visible,
                        style = style,
                    });
                }
            }

            var segBuf = EntityManager.HasBuffer<MarkingSegment>(node)
                ? EntityManager.GetBuffer<MarkingSegment>(node)
                : EntityManager.AddBuffer<MarkingSegment>(node);
            segBuf.Clear();
            for (int i = 0; i < newSegments.Count; i++) segBuf.Add(newSegments[i]);

            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = newHash });
            else
                EntityManager.AddComponentData(node, new MarkingTopologyState { linesHash = newHash });

            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);

            linesSnapshot.Dispose();
            beziers.Dispose();
            bezierValid.Dispose();

            log.Info($"topology node#{node.Index}: {lineCount} line(s) → {newSegments.Count} segment(s)");
            return true;
        }

        /// <summary>Visibility of the old segment containing <paramref name="t"/>, or
        /// <paramref name="defaultVisible"/> if none does.</summary>
        private static bool LookupInheritedVisibility(List<MarkingSegment> oldSegs, float t, bool defaultVisible)
        {
            for (int i = 0; i < oldSegs.Count; i++)
            {
                if (t >= oldSegs[i].tStart && t <= oldSegs[i].tEnd) return oldSegs[i].visible;
            }
            return defaultVisible;
        }

        /// <summary>Style of the old segment containing <paramref name="t"/>, or
        /// <paramref name="defaultStyle"/> (the line's style) if none does.</summary>
        private static int LookupInheritedStyle(List<MarkingSegment> oldSegs, float t, int defaultStyle)
        {
            for (int i = 0; i < oldSegs.Count; i++)
            {
                if (t >= oldSegs[i].tStart && t <= oldSegs[i].tEnd) return oldSegs[i].style;
            }
            return defaultStyle;
        }

        private static void DedupeSortedInPlace(List<float> values, float epsilon)
        {
            for (int i = values.Count - 1; i >= 1; i--)
            {
                if (math.abs(values[i] - values[i - 1]) < epsilon) values.RemoveAt(i);
            }
        }

        /// <summary>Call after removing entry <paramref name="lineIndex"/> from a node's
        /// MarkingLine buffer. Drops that line's segments and shifts the lineIndex of the rest,
        /// so the other lines keep their per-segment edits. Also reindexes area vertices anchored
        /// on line crossings (they store line indices) and resets both topology hashes so the
        /// next tick rebuilds. Vertices on the removed line are left as they are: they stop
        /// resolving and the area keeps its cached pieces, which is better than snapping to the
        /// wrong crossing.</summary>
        public static void OnLineRemoved(EntityManager em, Entity node, int lineIndex)
        {
            if (em.HasBuffer<MarkingSegment>(node))
            {
                var segs = em.GetBuffer<MarkingSegment>(node);
                for (int s = segs.Length - 1; s >= 0; s--)
                {
                    var seg = segs[s];
                    if (seg.lineIndex == lineIndex) { segs.RemoveAt(s); continue; }
                    if (seg.lineIndex > lineIndex)
                    {
                        seg.lineIndex--;
                        segs[s] = seg;
                    }
                }
            }

            if (em.HasBuffer<MarkingAreaVertex>(node))
            {
                var averts = em.GetBuffer<MarkingAreaVertex>(node);
                for (int v = 0; v < averts.Length; v++)
                {
                    var av = averts[v];
                    if (av.kind != 2) continue;
                    MarkingIntersectionExtractor.Unpack(av.refIndex, out int a, out int b, out int k);
                    if (a == lineIndex || b == lineIndex) continue;
                    if (a > lineIndex) a--;
                    if (b > lineIndex) b--;
                    av.refIndex = MarkingIntersectionExtractor.Pack(a, b, k);
                    averts[v] = av;
                }
                if (em.HasComponent<MarkingAreaTopologyState>(node))
                    em.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = 0 });
            }

            if (em.HasComponent<MarkingTopologyState>(node))
                em.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
        }

        /// <summary>True if <paramref name="p"/> is within <paramref name="marginM"/> (in XZ) of
        /// an endpoint of either curve.</summary>
        private static bool IsNearAnyEndpoint(float3 p, Bezier4x3 a, Bezier4x3 b, float marginM)
        {
            float rSq = marginM * marginM;
            return DistSqXZ(p, a.a) < rSq || DistSqXZ(p, a.d) < rSq
                || DistSqXZ(p, b.a) < rSq || DistSqXZ(p, b.d) < rSq;
        }

        private static float DistSqXZ(float3 p, float3 q)
        {
            float dx = p.x - q.x;
            float dz = p.z - q.z;
            return dx * dx + dz * dz;
        }

        /// <summary>Removes inner boundaries that would leave a segment shorter than
        /// <paramref name="minLengthM"/>. Walking from the start, the later boundary of a short
        /// segment is dropped; the outer 0 and 1 always stay. Length is the chord between the
        /// boundary points, which is close enough to arc length at this scale.</summary>
        private static void EnforceMinSegmentLength(List<float> boundaries, Bezier4x3 curve, float minLengthM)
        {
            if (boundaries.Count <= 2) return;
            float minSq = minLengthM * minLengthM;
            int i = 1;
            while (i < boundaries.Count - 1)
            {
                float3 pPrev = MathUtils.Position(curve, boundaries[i - 1]);
                float3 pCur  = MathUtils.Position(curve, boundaries[i]);
                if (DistSqXZ(pPrev, pCur) < minSq)
                {
                    boundaries.RemoveAt(i);
                    // Recheck the boundary that moved into slot i.
                    continue;
                }
                i++;
            }
            // If the last segment is too short, drop the last inner boundary.
            if (boundaries.Count >= 3)
            {
                int lastIdx = boundaries.Count - 1;
                float3 pPrev = MathUtils.Position(curve, boundaries[lastIdx - 1]);
                float3 pEnd  = MathUtils.Position(curve, boundaries[lastIdx]);
                if (DistSqXZ(pPrev, pEnd) < minSq) boundaries.RemoveAt(lastIdx - 1);
            }
        }

        private static int HashLines(DynamicBuffer<MarkingLine> lines)
        {
            // FNV-1a over each line's endpoints and curvature. Order-sensitive on purpose:
            // reordering lines changes the lineIndex that segments refer to.
            const uint kPrime = 16777619u;
            uint h = 2166136261u;
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                h = (h ^ (uint)l.sourceEdge.Index) * kPrime;
                h = (h ^ (uint)l.sourceGapIndex) * kPrime;
                h = (h ^ (uint)l.targetEdge.Index) * kPrime;
                h = (h ^ (uint)l.targetGapIndex) * kPrime;
                h = (h ^ math.asuint(l.curvature)) * kPrime;
            }
            return (int)h;
        }
    }

    /// <summary>Hash of the node's MarkingLine buffer at the last segment rebuild, so
    /// <see cref="MarkingTopologySystem"/> can skip the intersection work when nothing changed.
    /// Deliberately not saved: every node rebuilds once after a load.</summary>
    public struct MarkingTopologyState : IComponentData
    {
        public int linesHash;
    }
}
