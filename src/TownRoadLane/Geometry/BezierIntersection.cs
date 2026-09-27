using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace TownRoadLane.Geometry
{
    /// <summary>
    /// Finds the parameter pairs (tA, tB) where two cubic Bezier curves cross in the XZ plane
    /// (markings are flat, so Y is ignored). Uses recursive subdivision: a curve lies inside
    /// the convex hull of its control points, so curves whose control-point bounding boxes do
    /// not overlap cannot intersect. Once both pieces are smaller than the tolerance, the
    /// midpoints of their parameter ranges are reported as a hit.
    ///
    /// Neighbouring recursion branches often find the same crossing, so hits closer than
    /// <see cref="kDedupeDistance"/> are merged. A 10 m curve needs about 8 levels to reach
    /// 5 cm, which is cheap enough to recompute every line pair of a node (up to ~20 lines).
    /// </summary>
    public static class BezierIntersection
    {
        // Stop subdividing once a piece's bounding-box diagonal (XZ) is below this. 5 cm is well
        // under the visible width of a marking line.
        private const float kSubdivideTolerance = 0.05f;

        // Hits closer than this are one crossing found by two adjacent recursion branches.
        private const float kDedupeDistance = 0.15f;

        // Near-coincident curves would otherwise subdivide forever. Real crossings converge long
        // before this depth, so reaching it means degenerate input.
        private const int kMaxDepth = 16;

        public readonly struct Hit
        {
            public readonly float tA;     // parameter on curve A, [0, 1]
            public readonly float tB;     // parameter on curve B, [0, 1]
            public readonly float3 point;  // world-space point, Y averaged between the two curves

            public Hit(float tA, float tB, float3 point) { this.tA = tA; this.tB = tB; this.point = point; }
        }

        public static List<Hit> Intersect(Bezier4x3 a, Bezier4x3 b)
        {
            var hits = new List<Hit>(2);
            Recurse(a, b, 0f, 1f, 0f, 1f, 0, hits);
            DedupeInPlace(hits);
            return hits;
        }

        private static void Recurse(
            Bezier4x3 a, Bezier4x3 b,
            float aLo, float aHi, float bLo, float bHi,
            int depth, List<Hit> hits)
        {
            if (!AabbOverlapXZ(a, b)) return;

            float aDiag = AabbDiagonalXZ(a);
            float bDiag = AabbDiagonalXZ(b);
            bool aSmall = aDiag < kSubdivideTolerance;
            bool bSmall = bDiag < kSubdivideTolerance;

            if ((aSmall && bSmall) || depth >= kMaxDepth)
            {
                float tA = (aLo + aHi) * 0.5f;
                float tB = (bLo + bHi) * 0.5f;
                float3 pA = MathUtils.Position(a, 0.5f);
                float3 pB = MathUtils.Position(b, 0.5f);
                hits.Add(new Hit(tA, tB, (pA + pB) * 0.5f));
                return;
            }

            // Split only the larger piece: converges faster than always splitting both.
            if (aDiag >= bDiag)
            {
                float aMid = (aLo + aHi) * 0.5f;
                var a1 = MathUtils.Cut(a, new float2(0f, 0.5f));
                var a2 = MathUtils.Cut(a, new float2(0.5f, 1f));
                Recurse(a1, b, aLo, aMid, bLo, bHi, depth + 1, hits);
                Recurse(a2, b, aMid, aHi, bLo, bHi, depth + 1, hits);
            }
            else
            {
                float bMid = (bLo + bHi) * 0.5f;
                var b1 = MathUtils.Cut(b, new float2(0f, 0.5f));
                var b2 = MathUtils.Cut(b, new float2(0.5f, 1f));
                Recurse(a, b1, aLo, aHi, bLo, bMid, depth + 1, hits);
                Recurse(a, b2, aLo, aHi, bMid, bHi, depth + 1, hits);
            }
        }

        private static bool AabbOverlapXZ(Bezier4x3 a, Bezier4x3 b)
        {
            float aMinX = math.min(math.min(a.a.x, a.b.x), math.min(a.c.x, a.d.x));
            float aMaxX = math.max(math.max(a.a.x, a.b.x), math.max(a.c.x, a.d.x));
            float aMinZ = math.min(math.min(a.a.z, a.b.z), math.min(a.c.z, a.d.z));
            float aMaxZ = math.max(math.max(a.a.z, a.b.z), math.max(a.c.z, a.d.z));
            float bMinX = math.min(math.min(b.a.x, b.b.x), math.min(b.c.x, b.d.x));
            float bMaxX = math.max(math.max(b.a.x, b.b.x), math.max(b.c.x, b.d.x));
            float bMinZ = math.min(math.min(b.a.z, b.b.z), math.min(b.c.z, b.d.z));
            float bMaxZ = math.max(math.max(b.a.z, b.b.z), math.max(b.c.z, b.d.z));
            return aMaxX >= bMinX && aMinX <= bMaxX && aMaxZ >= bMinZ && aMinZ <= bMaxZ;
        }

        private static float AabbDiagonalXZ(Bezier4x3 c)
        {
            float minX = math.min(math.min(c.a.x, c.b.x), math.min(c.c.x, c.d.x));
            float maxX = math.max(math.max(c.a.x, c.b.x), math.max(c.c.x, c.d.x));
            float minZ = math.min(math.min(c.a.z, c.b.z), math.min(c.c.z, c.d.z));
            float maxZ = math.max(math.max(c.a.z, c.b.z), math.max(c.c.z, c.d.z));
            float dx = maxX - minX;
            float dz = maxZ - minZ;
            return math.sqrt(dx * dx + dz * dz);
        }

        private static void DedupeInPlace(List<Hit> hits)
        {
            if (hits.Count < 2) return;
            float dSq = kDedupeDistance * kDedupeDistance;
            // Quadratic, but a line pair yields only a handful of hits.
            for (int i = hits.Count - 1; i >= 1; i--)
            {
                for (int j = 0; j < i; j++)
                {
                    float3 diff = hits[i].point - hits[j].point;
                    if (diff.x * diff.x + diff.z * diff.z < dSq)
                    {
                        hits.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}
