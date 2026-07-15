using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Serialization;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 8: re-triangulates OUR spawned area fills after the vanilla pipeline, replacing
    /// its triangles with a faithful triangulation of the true node ring.
    ///
    /// Why: vanilla <see cref="Game.Areas.GeometrySystem"/> shrinks every polygon inward by
    /// 0.1 m per side (<c>AreaUtils.GetExpandedNode(-0.1f)</c> — sharp vertices fly metres
    /// inward) and ear-clips with a hard 2·N attempt budget; on failure it CLEARS the triangle
    /// buffer and the fill silently vanishes. That is why islands between marking lines need
    /// the min-width envelope in <see cref="MarkingAreaTopologySystem"/>. Owning the triangles
    /// removes the whole failure class: fills reach exactly to the ring, knife-tip corners
    /// render honestly.
    ///
    /// How: GeometrySystem only processes areas tagged <c>Updated</c> (ALL areas on the first
    /// tick after a save loads), writing <c>DynamicBuffer&lt;Triangle&gt;</c> — which is
    /// IEmptySerializable, i.e. never saved, so overwriting it is save-safe. This system runs
    /// right after it in the same phase (Modification2B) over the same trigger set, restricted
    /// to entities tagged <see cref="TRLAreaLink"/>, and rewrites per entity:
    ///  - Triangle indices: robust ear-clip of the TRUE ring (no shrink, no budget), then
    ///    vanilla's public <c>GeometrySystem.EqualizeTriangles</c> for mesh quality;
    ///  - Triangle.m_HeightRange: conservative terrain range over each triangle's AABB
    ///    (<c>TerrainUtils.GetHeightRange</c>) instead of vanilla's exact rasterisation —
    ///    slightly wider bounds, safe for culling;
    ///  - Triangle.m_MinLod: vanilla's formula (inner-edge midpoint candidates → distance to
    ///    polygon boundary → CalculateLodLimit), brute-force over edges — our rings are small;
    ///  - Area.m_Flags NoTriangles + the Geometry component (bounds / surface area / centre).
    ///
    /// If OUR ear-clip fails (degenerate ring) the vanilla triangles are left untouched —
    /// same graceful degradation as everywhere else in the mod.
    /// </summary>
    [UpdateAfter(typeof(Game.Areas.GeometrySystem))]
    public partial class MarkingAreaTriangulationSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _updatedOurs;
        private EntityQuery _allOurs;
        private TerrainSystem _terrainSystem;
        private bool _loaded;

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            _updatedOurs = GetEntityQuery(
                ComponentType.ReadOnly<TRLAreaLink>(),
                ComponentType.ReadOnly<Area>(),
                ComponentType.ReadOnly<Game.Areas.Node>(),
                ComponentType.ReadWrite<Triangle>(),
                ComponentType.ReadOnly<Updated>(),
                ComponentType.Exclude<Deleted>());
            _allOurs = GetEntityQuery(
                ComponentType.ReadOnly<TRLAreaLink>(),
                ComponentType.ReadOnly<Area>(),
                ComponentType.ReadOnly<Game.Areas.Node>(),
                ComponentType.ReadWrite<Triangle>(),
                ComponentType.Exclude<Deleted>());
            RequireForUpdate(_allOurs);
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            // Vanilla GeometrySystem re-triangulates EVERY area on its first post-load tick,
            // clobbering whatever we wrote in the previous session — mirror its behaviour.
            _loaded = true;
        }

        protected override void OnUpdate()
        {
            EntityQuery query;
            if (_loaded)
            {
                _loaded = false;
                query = _allOurs;
            }
            else
            {
                query = _updatedOurs;
            }
            if (query.IsEmptyIgnoreFilter) return;

            TerrainHeightData heightData = _terrainSystem.GetHeightData();
            using var entities = query.ToEntityArray(Allocator.Temp);
            int done = 0, failed = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                try
                {
                    if (RewriteTriangles(entities[i], ref heightData)) done++;
                    else failed++;
                }
                catch (System.Exception e)
                {
                    // Never break the tick loop: vanilla triangles (possibly empty) stay.
                    failed++;
                    log.Warn($"area-triangulation ent#{entities[i].Index}: {e.GetType().Name}: {e.Message} — keeping vanilla triangles");
                }
            }
            if (done > 0 || failed > 0)
                log.Info($"MarkingAreaTriangulationSystem: rewrote {done} fill(s){(failed > 0 ? $", {failed} left vanilla" : "")}");
        }

        /// <summary>Replace the entity's triangles with our own triangulation of the true node
        /// ring. Returns false (leaving vanilla data intact) when the ring can't be
        /// triangulated.</summary>
        private bool RewriteTriangles(Entity entity, ref TerrainHeightData heightData)
        {
            var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, isReadOnly: true);
            int n = nodes.Length;
            if (n < 3) return false;

            var positions = new NativeArray<float3>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) positions[i] = nodes[i].m_Position;

            // Vanilla marks rings CCW-positive via signed area; our ear-clip needs the winding
            // to classify convex corners the same way regardless of draw direction.
            bool ccw = SignedAreaXZ(positions) > 0f;

            var tris = new NativeList<Triangle>(2 * n, Allocator.Temp);
            bool ok = EarClip(positions, ccw, tris);
            if (!ok)
            {
                // Self-intersecting ring (e.g. a drawn chord crossing a curved edge) — vanilla
                // triangles stay; one warn per recompute, no retry loop (hash is written).
                log.Warn($"area-triangulation ent#{entity.Index}: ear-clip failed on {n}-node ring — keeping vanilla triangles");
                positions.Dispose();
                tris.Dispose();
                return false;
            }

            var triangles = EntityManager.GetBuffer<Triangle>(entity);
            triangles.Clear();
            for (int i = 0; i < tris.Length; i++) triangles.Add(tris[i]);
            tris.Dispose();

            // Vanilla mesh-quality pass (public static): Delaunay-style edge flips.
            Game.Areas.GeometrySystem.EqualizeTriangles(positions, triangles);

            // Per-triangle terrain height range + LOD, then the aggregate Geometry component.
            var prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
            float heightOffset = 0f;
            float nodeDistance = 0f;
            float lodBias = 0f;
            if (EntityManager.HasComponent<TerrainAreaData>(prefabRef.m_Prefab))
                heightOffset = EntityManager.GetComponentData<TerrainAreaData>(prefabRef.m_Prefab).m_HeightOffset;
            if (EntityManager.HasComponent<AreaGeometryData>(prefabRef.m_Prefab))
            {
                var geoData = EntityManager.GetComponentData<AreaGeometryData>(prefabRef.m_Prefab);
                nodeDistance = AreaUtils.GetMinNodeDistance(geoData.m_Type);
                lodBias = geoData.m_LodBias;
            }

            var geometry = new Geometry { m_Bounds = new Bounds3(float.MaxValue, float.MinValue) };
            float bestCentreScore = -1f;
            for (int i = 0; i < triangles.Length; i++)
            {
                Triangle tri = triangles[i];
                Triangle3 tri3 = AreaUtils.GetTriangle3(nodes, tri);

                // Conservative height range: terrain min/max over the triangle's world AABB,
                // relative to the triangle's own vertical extent (vanilla rasterises the exact
                // triangle; the AABB superset only ever widens the range — culling-safe).
                Bounds3 triBounds = MathUtils.Bounds(tri3);
                Bounds1 offsetBounds = new Bounds1(math.min(0f, heightOffset), math.max(0f, heightOffset));
                Bounds1 terrain = TerrainUtils.GetHeightRange(ref heightData, triBounds);
                if (terrain.min <= terrain.max)
                {
                    tri.m_HeightRange = new Bounds1(terrain.min - triBounds.max.y, terrain.max - triBounds.min.y) | offsetBounds;
                }
                else
                {
                    tri.m_HeightRange = offsetBounds;
                }

                // Vanilla MinLod: candidate points near the triangle's interior (midpoints of
                // its non-boundary edges), scored by distance to the polygon boundary; the
                // resulting clearance × 4 approximates the fill's local rendering size.
                float2 bestMinDistSq = ScoreTriangle(tri, tri3, positions, out float3 centreCandidate);
                float2 size = math.sqrt(bestMinDistSq) * 4f;
                tri.m_MinLod = RenderingUtils.CalculateLodLimit(
                    RenderingUtils.GetRenderingSize(new float3(size.x, nodeDistance, size.y)), lodBias);
                triangles[i] = tri;

                geometry.m_Bounds |= triBounds;
                geometry.m_SurfaceArea += MathUtils.Area(tri3.xz);
                if (bestMinDistSq.x > bestCentreScore)
                {
                    bestCentreScore = bestMinDistSq.x;
                    geometry.m_CenterPosition = centreCandidate;
                }
            }
            geometry.m_CenterPosition.y = TerrainUtils.SampleHeight(ref heightData, geometry.m_CenterPosition);

            if (EntityManager.HasComponent<Geometry>(entity))
                EntityManager.SetComponentData(entity, geometry);

            var area = EntityManager.GetComponentData<Area>(entity);
            area.m_Flags &= ~AreaFlags.NoTriangles;
            EntityManager.SetComponentData(entity, area);

            positions.Dispose();
            return true;
        }

        // ── MinLod scoring (port of vanilla's candidate walk, brute-force edges) ────

        /// <summary>Vanilla candidate scheme (port of CheckCenterPositionCandidate + its call
        /// sites, brute-force over edges — our rings are small): for each triangle edge that is
        /// NOT a boundary edge of the polygon (index gap > 1), take its midpoint; special-case
        /// triangles with exactly one boundary edge (weighted centre) or none (centroid).
        /// Score per candidate = the TWO smallest squared distances to polygon edges as
        /// (min1, min2) — vanilla's local-width proxy; candidates compete on min1.</summary>
        private static float2 ScoreTriangle(Triangle tri, Triangle3 tri3, NativeArray<float3> positions, out float3 bestPos)
        {
            int n = positions.Length;
            int3 gap = math.abs(tri.m_Indices.zxy - tri.m_Indices.yzx);
            bool3 isBoundary = (gap == 1) | (gap == n - 1);
            bool3 inner = !isBoundary;

            float2 best = -1f;
            bestPos = (tri3.a + tri3.b + tri3.c) / 3f;

            void Check(float3 candidate, ref float2 bestRef, ref float3 bestPosRef)
            {
                float2 twoMin = TwoMinDistSqToBoundary(candidate, positions);
                if (twoMin.x > bestRef.x)
                {
                    bestRef = twoMin;
                    bestPosRef = candidate;
                }
            }

            if (inner.x) Check(math.lerp(tri3.b, tri3.c, 0.5f), ref best, ref bestPos);
            if (inner.y) Check(math.lerp(tri3.c, tri3.a, 0.5f), ref best, ref bestPos);
            if (inner.z) Check(math.lerp(tri3.a, tri3.b, 0.5f), ref best, ref bestPos);
            if (math.all(inner.xy) & isBoundary.z)
                Check(tri3.c * 0.5f + (tri3.a + tri3.b) * 0.25f, ref best, ref bestPos);
            else if (math.all(inner.yz) & isBoundary.x)
                Check(tri3.a * 0.5f + (tri3.b + tri3.c) * 0.25f, ref best, ref bestPos);
            else if (math.all(inner.zx) & isBoundary.y)
                Check(tri3.b * 0.5f + (tri3.c + tri3.a) * 0.25f, ref best, ref bestPos);
            else if (best.x < 0f)
                Check((tri3.a + tri3.b + tri3.c) / 3f, ref best, ref bestPos);
            return math.max(best, 0f);
        }

        /// <summary>(min1, min2) of squared distances from <paramref name="point"/> to every
        /// polygon boundary segment — same accumulation as vanilla's candidate loop.</summary>
        private static float2 TwoMinDistSqToBoundary(float3 point, NativeArray<float3> positions)
        {
            int n = positions.Length;
            float2 best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float2 a = positions[i].xz;
                float2 b = positions[(i + 1) % n].xz;
                float2 ab = b - a;
                float t = math.saturate(math.dot(point.xz - a, ab) / math.max(math.dot(ab, ab), 1e-9f));
                float d = math.distancesq(point.xz, math.lerp(a, b, t));
                best.y = math.select(best.y, d, d < best.y);
                best = math.select(best, new float2(d, best.x), d < best.x);
            }
            return best;
        }

        // ── Robust ear-clipping (true ring, no shrink, no attempt budget) ────────────

        private static float SignedAreaXZ(NativeArray<float3> pts)
        {
            float sum = 0f;
            for (int i = 0; i < pts.Length; i++)
            {
                float3 a = pts[i];
                float3 b = pts[(i + 1) % pts.Length];
                sum += a.x * b.z - b.x * a.z;
            }
            return sum * 0.5f;
        }

        /// <summary>Classic O(n²) ear-clipping over the XZ plane. Unlike vanilla's version
        /// there is no shrink pre-pass and no 2·N attempt cap: a valid simple polygon always
        /// triangulates fully (n − 2 triangles). Degenerate corners (zero-area ears) are
        /// clipped eagerly, matching how vanilla's snip tolerates collinear points. Returns
        /// false only when no ear can be found (self-intersecting ring).</summary>
        private static bool EarClip(NativeArray<float3> positions, bool ccw, NativeList<Triangle> outTris)
        {
            int n = positions.Length;
            var index = new NativeList<int>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) index.Add(i);

            while (index.Length > 3)
            {
                int m = index.Length;
                int earAt = -1;
                // Pass 1: strictly convex, empty ears. Pass 2 (fallback): also allow zero-area
                // (collinear) ears — clipping them is a no-op geometrically but unblocks rings
                // with duplicate/collinear points. The emptiness check stays MANDATORY in both
                // passes: an ear containing another vertex emits triangles outside the polygon
                // (fills visually "spilling" past their contour) — a clean failure into the
                // vanilla fallback is strictly better than garbage geometry.
                for (int pass = 0; pass < 2 && earAt < 0; pass++)
                {
                    float minCross = pass == 0 ? 1e-6f : -1e-6f;
                    for (int i = 0; i < m; i++)
                    {
                        int i0 = index[(i + m - 1) % m], i1 = index[i], i2 = index[(i + 1) % m];
                        float2 a = positions[i0].xz, b = positions[i1].xz, c = positions[i2].xz;
                        float cross = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                        if (!ccw) cross = -cross;
                        if (cross < minCross) continue; // reflex corner — not an ear
                        if (ContainsOtherVertex(positions, index, i, a, b, c)) continue;
                        earAt = i;
                        break;
                    }
                }
                if (earAt < 0)
                {
                    index.Dispose();
                    return false;
                }
                int p0 = index[(earAt + index.Length - 1) % index.Length];
                int p1 = index[earAt];
                int p2 = index[(earAt + 1) % index.Length];
                // Emit even zero-area ears: consumers (incremental search-tree diff, LOD
                // sort) tolerate any count but vanilla's invariant is EXACTLY n − 2 triangles
                // per ring — keep it. Degenerate triangles draw nothing and hit-test nothing.
                outTris.Add(ccw ? new Triangle(p0, p1, p2) : new Triangle(p2, p1, p0));
                index.RemoveAt(earAt);
            }
            outTris.Add(ccw
                ? new Triangle(index[0], index[1], index[2])
                : new Triangle(index[2], index[1], index[0]));
            index.Dispose();
            return true;
        }

        private static bool ContainsOtherVertex(NativeArray<float3> positions, NativeList<int> index, int earIdx,
                                                float2 a, float2 b, float2 c)
        {
            int m = index.Length;
            int i0 = index[(earIdx + m - 1) % m], i1 = index[earIdx], i2 = index[(earIdx + 1) % m];
            for (int j = 0; j < m; j++)
            {
                int pj = index[j];
                if (pj == i0 || pj == i1 || pj == i2) continue;
                if (PointInTriangle(positions[pj].xz, a, b, c)) return true;
            }
            return false;
        }

        private static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool hasNeg = d1 < -1e-9f || d2 < -1e-9f || d3 < -1e-9f;
            bool hasPos = d1 > 1e-9f || d2 > 1e-9f || d3 > 1e-9f;
            return !(hasNeg && hasPos);
        }
    }
}
