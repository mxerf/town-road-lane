using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>Polygon and point helpers in the XZ plane. Y is ignored.</summary>
    public static class PolygonUtils
    {
        public static float DistSqXZ(float3 p, float3 q)
        {
            float dx = p.x - q.x;
            float dz = p.z - q.z;
            return dx * dx + dz * dz;
        }

        /// <summary>Shoelace area of the ring. Positive = counter-clockwise.</summary>
        public static float SignedAreaXZ(List<float3> ring)
        {
            float sum = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                float3 a = ring[i];
                float3 b = ring[(i + 1) % ring.Count];
                sum += a.x * b.z - b.x * a.z;
            }
            return sum * 0.5f;
        }

        /// <inheritdoc cref="SignedAreaXZ(List{float3})"/>
        public static float SignedAreaXZ(NativeArray<float3> ring)
        {
            float sum = 0f;
            for (int i = 0; i < ring.Length; i++)
            {
                float3 a = ring[i];
                float3 b = ring[(i + 1) % ring.Length];
                sum += a.x * b.z - b.x * a.z;
            }
            return sum * 0.5f;
        }

        /// <summary>Average of the ring's vertices. Used to match a recomputed fill piece to the
        /// old piece that contains this point.</summary>
        public static float3 CentroidXZ(List<float3> ring)
        {
            float3 sum = float3.zero;
            for (int i = 0; i < ring.Count; i++) sum += ring[i];
            return sum / ring.Count;
        }

        /// <summary>Point-in-polygon test (ray casting).</summary>
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
