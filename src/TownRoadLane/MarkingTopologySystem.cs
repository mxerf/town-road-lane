using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Stage 5b owner of the (MarkingLine → MarkingSegment) relationship. When any
    /// <see cref="MarkingLine"/> buffer changes on a node, recomputes the
    /// <see cref="MarkingSegment"/> buffer:
    ///
    ///   1. Build the full Bezier of every line on the node (via MarkingCurveBuilder).
    ///   2. For each pair (i &lt; j), find all intersection parameters (tI, tJ) using
    ///      BezierIntersection — adds tI as a boundary on line i, tJ on line j.
    ///   3. Sort + dedupe boundaries per line. Sandwich endpoints {0, 1} on the outside.
    ///   4. Build N-1 new segments per line from N sorted boundaries. Each new segment
    ///      inherits visibility from the OLD segment that contained its midpoint, if any —
    ///      otherwise defaults to visible=true. This keeps user edits stable across
    ///      recomputes (adding a new line doesn't un-hide segments the user explicitly hid).
    ///   5. Rewrite the MarkingSegment buffer with the new flat list (lineIndex + tRange).
    ///
    /// Trigger: marks the node Updated when it rewrites the buffer so MarkingPairEmissionSystem
    /// (or its successor MarkingSegmentEmissionSystem) picks up the change.
    ///
    /// Detection of "needs recompute" uses a content-hash of the MarkingLine buffer stored on
    /// a separate component <see cref="MarkingTopologyState"/>. Comparing the hash on every tick
    /// is much cheaper than re-running the intersection math when nothing changed. The hash
    /// includes the gap-based endpoint identity of every line; it does NOT include style
    /// (style change can't move a line, so doesn't affect topology).
    /// </summary>
    [UpdateAfter(typeof(MarkingPairMigrationSystem))]
    [UpdateBefore(typeof(MarkingSegmentEmissionSystem))]
    public partial class MarkingTopologySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithLines;

        // Intersections within this world-space radius of any line endpoint are dropped.
        // 2.0m = typical 3m drive lane width / 1.5 — covers the "two lines from one dot" overlap
        // without eating real splits between independent lines. Stable across line length.
        private const float kEndpointMarginM = 2.0f;

        // Segments shorter than this are merged with their neighbour by removing the internal
        // boundary. Catches tangential grazes that survive the endpoint filter — typical case is
        // two slightly-different curves that touch in the middle for ~50cm.
        private const float kMinSegmentLengthM = 1.0f;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nodesWithLines = GetEntityQuery(
                ComponentType.ReadOnly<MarkingLine>(),
                ComponentType.ReadOnly<Node>());
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

            // Quick exit: empty MarkingLine + no MarkingSegment buffer → nothing to do.
            if (lines.Length == 0 && !EntityManager.HasBuffer<MarkingSegment>(node))
                return false;

            int newHash = HashLines(lines);
            int oldHash = EntityManager.HasComponent<MarkingTopologyState>(node)
                ? EntityManager.GetComponentData<MarkingTopologyState>(node).linesHash
                : 0;
            if (newHash == oldHash && EntityManager.HasBuffer<MarkingSegment>(node))
                return false;

            // Snapshot lines before any structural change — buffer becomes invalid the moment
            // we call AddBuffer/RemoveComponent below.
            int lineCount = lines.Length;
            var linesSnapshot = new NativeArray<MarkingLine>(lineCount, Allocator.Temp);
            for (int i = 0; i < lineCount; i++) linesSnapshot[i] = lines[i];

            // Capture old segments so we can inherit visibility for unchanged boundaries.
            // List<(lineIndex, tStart, tEnd, visible)> per line.
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

            // Build per-line Bezier curves once. nullable-equivalent: use a parallel bool array
            // since Bezier4x3 is a struct.
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

            // Save/load guard: on the first tick after loading a save, Composition and
            // EdgeGeometry are still zeroed (IEmptySerializable; refilled at Modification3/4,
            // AFTER this Modification1 system runs). Endpoint extraction then yields nothing,
            // every TryBuild fails, and rewriting the buffer would collapse each line to a
            // single [0,1] segment — destroying the per-segment style/visibility the user saved.
            // If any failed line still references a live-but-unready edge, defer the whole node:
            // no writes, no hash update, retry next tick.
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

            // Boundaries[i] = sorted unique t-values on line i where it splits.
            var boundaries = new List<List<float>>(lineCount);
            for (int i = 0; i < lineCount; i++)
            {
                var b = new List<float>(4);
                b.Add(0f); b.Add(1f);
                boundaries.Add(b);
            }
            // Pairwise intersection. n is tiny in practice (≤ 20).
            //
            // Filter B (endpoint margin): two lines that share a dot inevitably "overlap" for the
            // first few metres as they leave the dot together (their tangents and start points
            // match). BezierIntersection sees dozens of micro-hits along that overlap and reports
            // each as a separate intersection. Drop any hit whose world-space position is within
            // kEndpointMarginM of either curve's endpoint — that's not a logical split, it's the
            // endpoint cluster the user clicked through.
            for (int i = 0; i < lineCount; i++)
            {
                if (!bezierValid[i]) continue;
                for (int j = i + 1; j < lineCount; j++)
                {
                    if (!bezierValid[j]) continue;
                    var hits = BezierIntersection.Intersect(beziers[i], beziers[j]);
                    for (int h = 0; h < hits.Count; h++)
                    {
                        if (IsNearAnyEndpoint(hits[h].point, beziers[i], beziers[j])) continue;
                        if (hits[h].tA > 0.01f && hits[h].tA < 0.99f) boundaries[i].Add(hits[h].tA);
                        if (hits[h].tB > 0.01f && hits[h].tB < 0.99f) boundaries[j].Add(hits[h].tB);
                    }
                }
            }

            // Sort + dedupe each boundary list (in case multiple lines cross at the same t).
            for (int i = 0; i < lineCount; i++)
            {
                boundaries[i].Sort();
                DedupeSortedInPlace(boundaries[i], epsilon: 0.005f);
            }

            // Filter C (minimum segment length): even after endpoint filtering, two lines that
            // graze each other along an arc can leave sub-metre slivers. Walk the boundary list
            // and drop any internal boundary that would create a segment shorter than
            // kMinSegmentLengthM in world space. Always keep the outer 0 and 1.
            for (int i = 0; i < lineCount; i++)
            {
                if (!bezierValid[i]) continue;
                EnforceMinSegmentLength(boundaries[i], beziers[i], kMinSegmentLengthM);
            }

            // Build the new flat segments list. Each new segment inherits visibility AND style
            // from the OLD segment whose [tStart, tEnd] contains the new midpoint — that way
            // a per-segment override survives a re-split when a fresh intersecting line is added.
            // When no old segment matches (first build for this line) fall back to the parent
            // MarkingLine's style.
            var newSegments = new List<MarkingSegment>(lineCount * 2);
            for (int i = 0; i < lineCount; i++)
            {
                // Line permanently unresolvable (edge demolished, or gapIndex gone after a road
                // upgrade changed the lane layout). Its curve can't be built so nothing renders,
                // but carry its old segments over verbatim instead of collapsing them — if the
                // situation is somehow restored later, the user's per-segment edits are intact.
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

            // Write back. AddBuffer overwrites if present.
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

        /// <summary>Find the old segment whose [tStart, tEnd] contains <paramref name="t"/>;
        /// return its visibility. Falls back to <paramref name="defaultVisible"/> when no
        /// match (= a newly created segment).</summary>
        private static bool LookupInheritedVisibility(List<MarkingSegment> oldSegs, float t, bool defaultVisible)
        {
            for (int i = 0; i < oldSegs.Count; i++)
            {
                if (t >= oldSegs[i].tStart && t <= oldSegs[i].tEnd) return oldSegs[i].visible;
            }
            return defaultVisible;
        }

        /// <summary>Same pattern as <see cref="LookupInheritedVisibility"/> for the per-segment
        /// style override. New segments inherit from whichever old segment contained their
        /// midpoint; truly-new segments take the parent line's default style.</summary>
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

        /// <summary>True if the world-space point sits within <see cref="kEndpointMarginM"/> of
        /// either endpoint of either curve. Distance compared in XZ — Y is irrelevant for road
        /// markings (everything is on-or-near the road surface). Squared compare for speed.</summary>
        private static bool IsNearAnyEndpoint(float3 p, Bezier4x3 a, Bezier4x3 b)
        {
            float rSq = kEndpointMarginM * kEndpointMarginM;
            return DistSqXZ(p, a.a) < rSq || DistSqXZ(p, a.d) < rSq
                || DistSqXZ(p, b.a) < rSq || DistSqXZ(p, b.d) < rSq;
        }

        private static float DistSqXZ(float3 p, float3 q)
        {
            float dx = p.x - q.x;
            float dz = p.z - q.z;
            return dx * dx + dz * dz;
        }

        /// <summary>Remove internal boundaries that would create segments shorter than
        /// <paramref name="minLengthM"/> in world space. Walks left-to-right; when a too-short
        /// segment is found, the LATER boundary is dropped (which extends the next segment
        /// instead of the previous one — arbitrary but consistent). Outer 0 and 1 are kept.
        ///
        /// Curve length used is the chord between sampled positions, not arc length — close
        /// enough for the scales we hit (sub-metre slivers on ~5-20m curves) and avoids per-
        /// merge calls to MathUtils.Length.</summary>
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
                    // Don't advance — the new boundary at index i needs re-check against pPrev.
                    continue;
                }
                i++;
            }
            // Final segment: if last internal boundary leaves a sub-min tail, drop it too.
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
            // FNV-1a 32-bit on (sourceEdge.Index, sourceGap, targetEdge.Index, targetGap) per line.
            // Order matters: swapping two lines changes lineIndex assignments and thus segment
            // lineIndex references, so we want a recompute in that case.
            const uint kPrime = 16777619u;
            uint h = 2166136261u;
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                h = (h ^ (uint)l.sourceEdge.Index) * kPrime;
                h = (h ^ (uint)l.sourceGapIndex) * kPrime;
                h = (h ^ (uint)l.targetEdge.Index) * kPrime;
                h = (h ^ (uint)l.targetGapIndex) * kPrime;
            }
            return (int)h;
        }
    }

    /// <summary>Per-node companion of <see cref="MarkingLine"/>: caches the hash of the line
    /// buffer at last successful topology recompute. Lets <see cref="MarkingTopologySystem"/>
    /// skip the O(n²) Bezier intersection work when the buffer hasn't changed since last tick.
    /// Not serialised — recomputed on first OnUpdate after load (hash = 0 → mismatch → recompute,
    /// which is exactly what we want).</summary>
    public struct MarkingTopologyState : IComponentData
    {
        public int linesHash;
    }
}
