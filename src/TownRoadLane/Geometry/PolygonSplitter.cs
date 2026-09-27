using System.Collections.Generic;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Splits a simple polygon (no holes, no self-intersections) by polylines into smaller
    /// polygons that together cover the input, each lying on one side of every cut. Works in
    /// the XZ plane; Y is interpolated.
    ///
    /// This is a reduced Weiler-Atherton chord cut: a cut that crosses the boundary an even
    /// number of times is paired into chords, the boundary plus one chord gives two
    /// sub-polygons, and each half is split again with the same cut.
    ///
    /// Edge cases:
    ///   - Odd hit count (the cut ends inside or grazes a vertex): no split.
    ///   - Hits near a vertex snap to it, so no sliver edges appear.
    ///   - Parallel edges count as no intersection.
    ///   - Rings with fewer than 3 points or below the minimum area are discarded.
    /// </summary>
    public static class PolygonSplitter
    {
        // Edge hits this close (in t) to a vertex snap onto it, so a cut grazing a vertex leaves
        // no zero-length sliver edge.
        private const float kVertexSnapT = 1e-3f;
        // Hits closer than this are one point: a cut through a polygon vertex is reported by
        // both edges that share it.
        private const float kDuplicateHitDistSq = 1e-4f * 1e-4f;
        // Result rings smaller than this (m²) are discarded as slivers.
        private const float kMinRingArea = 0.01f;
        // Every real split shrinks the ring area, so genuine splits never get near this depth.
        // It is a backstop for unforeseen degenerate geometry: a stack overflow takes the whole
        // game down with a native Mono crash, an unsplit area does not.
        private const int kMaxSplitDepth = 16;

        /// <summary>One intersection between a polygon edge and a cut segment.</summary>
        private struct Hit
        {
            public int edgeIndex;   // index into the polygon ring (edge from ring[edgeIndex] to ring[edgeIndex+1])
            public float tEdge;     // 0..1 along the polygon edge
            public int cutIndex;    // which segment of the cut polyline produced this hit
            public float tCut;      // 0..1 along that cut segment
            public float3 point;    // world-space intersection (Y interpolated along the edge)
        }

        /// <summary>
        /// Splits <paramref name="polygon"/> by the polyline <paramref name="cut"/>. If the cut
        /// does not fully cross the polygon, the result holds only the original polygon.
        /// </summary>
        public static List<List<float3>> SplitByPolyline(List<float3> polygon, List<float3> cut)
            => SplitByPolyline(polygon, cut, 0);

        private static List<List<float3>> SplitByPolyline(List<float3> polygon, List<float3> cut, int depth)
        {
            var result = new List<List<float3>>();
            if (polygon == null || polygon.Count < 3 || cut == null || cut.Count < 2
                || depth >= kMaxSplitDepth)
            {
                result.Add(polygon ?? new List<float3>());
                return result;
            }

            var hits = new List<Hit>(8);
            int n = polygon.Count;
            for (int c = 0; c < cut.Count - 1; c++)
            {
                float3 cA = cut[c], cB = cut[c + 1];
                for (int e = 0; e < n; e++)
                {
                    float3 eA = polygon[e], eB = polygon[(e + 1) % n];
                    if (!Segment2DIntersect(eA.xz, eB.xz, cA.xz, cB.xz, out float tEdge, out float tCut)) continue;
                    if (tEdge < 0f || tEdge > 1f || tCut < 0f || tCut > 1f) continue;
                    if (tEdge < kVertexSnapT) tEdge = 0f;
                    else if (tEdge > 1f - kVertexSnapT) tEdge = 1f;
                    float y = math.lerp(eA.y, eB.y, tEdge);
                    float3 p = new float3(math.lerp(eA.x, eB.x, tEdge), y, math.lerp(eA.z, eB.z, tEdge));
                    hits.Add(new Hit { edgeIndex = e, tEdge = tEdge, cutIndex = c, tCut = tCut, point = p });
                }
            }

            if (hits.Count < 2)
            {
                result.Add(polygon);
                return result;
            }

            // Order along the cut polyline.
            hits.Sort((a, b) => a.cutIndex != b.cutIndex
                ? a.cutIndex.CompareTo(b.cutIndex)
                : a.tCut.CompareTo(b.tCut));

            for (int i = hits.Count - 1; i > 0; i--)
            {
                float dx = hits[i].point.x - hits[i - 1].point.x;
                float dz = hits[i].point.z - hits[i - 1].point.z;
                if (dx * dx + dz * dz < kDuplicateHitDistSq) hits.RemoveAt(i);
            }

            if ((hits.Count & 1) != 0)
            {
                // The cut entered but did not fully cross: it ends inside, or touches a vertex.
                result.Add(polygon);
                return result;
            }

            // Only a pair where both rings are smaller than the input by more than the sliver
            // threshold is a real split. A cut that runs along the boundary (typically a line
            // that is itself one of the polygon's edges, when the area is closed over the
            // endpoints of the lines that cut it) yields the whole polygon plus a sliver.
            // Recursing on that ring finds the same hits again forever and overflows the stack.
            float polyArea = math.abs(SignedAreaXZ(polygon));
            for (int h = 0; h + 1 < hits.Count; h += 2)
            {
                var left = BuildRing(polygon, cut, hits[h], hits[h + 1], walkForward: true);
                var right = BuildRing(polygon, cut, hits[h], hits[h + 1], walkForward: false);
                float leftArea = left.Count >= 3 ? math.abs(SignedAreaXZ(left)) : 0f;
                float rightArea = right.Count >= 3 ? math.abs(SignedAreaXZ(right)) : 0f;
                bool realSplit = leftArea >= kMinRingArea && rightArea >= kMinRingArea
                              && leftArea <= polyArea - kMinRingArea
                              && rightArea <= polyArea - kMinRingArea;
                if (!realSplit) continue;

                // Recurse with the same cut: any further chords of it are found again inside
                // whichever half they fall in.
                foreach (var r in SplitByPolyline(left, cut, depth + 1)) if (IsValidRing(r)) result.Add(r);
                foreach (var r in SplitByPolyline(right, cut, depth + 1)) if (IsValidRing(r)) result.Add(r);
                return result;
            }

            // Every candidate chord ran along the boundary.
            result.Add(polygon);
            return result;
        }

        /// <summary>Splits <paramref name="polygon"/> by every polyline in
        /// <paramref name="cuts"/> in turn.</summary>
        public static List<List<float3>> SplitByPolylines(List<float3> polygon, List<List<float3>> cuts)
        {
            var current = new List<List<float3>> { polygon };
            if (cuts == null) return current;
            for (int c = 0; c < cuts.Count; c++)
            {
                var next = new List<List<float3>>();
                for (int p = 0; p < current.Count; p++)
                {
                    var split = SplitByPolyline(current[p], cuts[c]);
                    next.AddRange(split);
                }
                current = next;
            }
            return current;
        }

        /// <summary>
        /// Builds one side of the chord cut: walks the polygon boundary from
        /// <paramref name="hEnter"/> to <paramref name="hExit"/> (forward = increasing edge
        /// index), then closes back to hEnter along the cut polyline.
        /// </summary>
        private static List<float3> BuildRing(List<float3> polygon, List<float3> cut, Hit hEnter, Hit hExit, bool walkForward)
        {
            var ring = new List<float3>(polygon.Count + 4);
            ring.Add(hEnter.point);

            // Forward takes vertices enter.edgeIndex+1 … exit.edgeIndex; reverse takes
            // enter.edgeIndex … exit.edgeIndex+1.
            int n = polygon.Count;
            if (walkForward)
            {
                int v = (hEnter.edgeIndex + 1) % n;
                // Both hits on the same edge, in walking order: no polygon vertex in between.
                if (hEnter.edgeIndex != hExit.edgeIndex || hEnter.tEdge > hExit.tEdge)
                {
                    int safety = n + 1;
                    while (safety-- > 0)
                    {
                        ring.Add(polygon[v]);
                        if (v == hExit.edgeIndex) break;
                        v = (v + 1) % n;
                    }
                }
            }
            else
            {
                int v = hEnter.edgeIndex;
                if (hEnter.edgeIndex != hExit.edgeIndex || hEnter.tEdge < hExit.tEdge)
                {
                    int safety = n + 1;
                    while (safety-- > 0)
                    {
                        ring.Add(polygon[v]);
                        int prev = (v - 1 + n) % n;
                        if (prev == hExit.edgeIndex) break;
                        v = prev;
                    }
                }
            }

            ring.Add(hExit.point);

            // Back along the cut: hEnter.cutIndex ≤ hExit.cutIndex by sort order, so the cut
            // vertices strictly between the two hits are cut[hExit.cutIndex] down to
            // cut[hEnter.cutIndex + 1].
            for (int c = hExit.cutIndex; c > hEnter.cutIndex; c--)
            {
                ring.Add(cut[c]);
            }

            // Drop zero-length edges, including the closing one.
            for (int i = ring.Count - 1; i > 0; i--)
            {
                float dx = ring[i].x - ring[i - 1].x;
                float dz = ring[i].z - ring[i - 1].z;
                if (dx * dx + dz * dz < kDuplicateHitDistSq) ring.RemoveAt(i);
            }
            if (ring.Count >= 2)
            {
                float dx = ring[ring.Count - 1].x - ring[0].x;
                float dz = ring[ring.Count - 1].z - ring[0].z;
                if (dx * dx + dz * dz < kDuplicateHitDistSq) ring.RemoveAt(ring.Count - 1);
            }
            return ring;
        }

        private static bool IsValidRing(List<float3> ring)
        {
            if (ring == null || ring.Count < 3) return false;
            return math.abs(SignedAreaXZ(ring)) >= kMinRingArea;
        }

        /// <summary>Shoelace area of the ring in the XZ plane. Positive = CCW.</summary>
        public static float SignedAreaXZ(List<float3> ring)
        {
            float sum = 0f;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                sum += (b.x - a.x) * (b.z + a.z);
            }
            return -sum * 0.5f;
        }

        /// <summary>Average of the ring's vertices. Used to match a recomputed fill piece to the
        /// old piece that contains this point.</summary>
        public static float3 CentroidXZ(List<float3> ring)
        {
            float3 sum = float3.zero;
            for (int i = 0; i < ring.Count; i++) sum += ring[i];
            return sum / ring.Count;
        }

        /// <summary>Intersection of the lines through segments AB and CD, as parameters along
        /// each. False only for parallel or collinear segments; the caller checks that both
        /// parameters fall within [0, 1].</summary>
        private static bool Segment2DIntersect(float2 a, float2 b, float2 c, float2 d, out float tA, out float tB)
        {
            tA = 0f; tB = 0f;
            float2 r = b - a;
            float2 s = d - c;
            float denom = r.x * s.y - r.y * s.x;
            if (math.abs(denom) < 1e-9f) return false;  // parallel or collinear
            float2 ca = c - a;
            tA = (ca.x * s.y - ca.y * s.x) / denom;
            tB = (ca.x * r.y - ca.y * r.x) / denom;
            return true;
        }

        /// <summary>Point-in-polygon test in the XZ plane (ray casting).</summary>
        public static bool ContainsXZ(List<float3> ring, float3 p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = ring[i].x, zi = ring[i].z;
                float xj = ring[j].x, zj = ring[j].z;
                bool intersect = ((zi > p.z) != (zj > p.z)) &&
                                 (p.x < (xj - xi) * (p.z - zi) / (zj - zi + 1e-9f) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }
}
