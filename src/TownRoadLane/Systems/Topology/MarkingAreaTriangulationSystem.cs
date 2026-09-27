using System.Collections.Generic;
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
using static TownRoadLane.PolygonUtils;

namespace TownRoadLane
{
    /// <summary>
    /// Replaces the vanilla triangles of the mod's area fills with a triangulation of the exact
    /// node ring.
    ///
    /// Vanilla <see cref="Game.Areas.GeometrySystem"/> shrinks every polygon by 0.1 m per side
    /// (<c>AreaUtils.GetExpandedNode(-0.1f)</c>), which pulls sharp vertices metres inward, and
    /// ear-clips with a budget of 2·N attempts. When that fails it clears the triangle buffer
    /// and the fill disappears without a trace. Thin islands between marking lines hit this
    /// constantly.
    ///
    /// GeometrySystem handles areas tagged <c>Updated</c> (and all areas on the first tick
    /// after a load). This system runs right after it in Modification2B on the same set,
    /// limited to entities with <see cref="TRLAreaLink"/>. The Triangle buffer is
    /// IEmptySerializable and never saved, so overwriting it is safe. Per entity it writes:
    ///  - triangle indices: ear-clip of the exact ring, then vanilla's public
    ///    <c>GeometrySystem.EqualizeTriangles</c> for mesh quality;
    ///  - Triangle.m_HeightRange: terrain range over each triangle's bounding box
    ///    (<c>TerrainUtils.GetHeightRange</c>), slightly wider than vanilla's exact raster;
    ///  - Triangle.m_MinLod: vanilla's formula, brute force over the (small) ring;
    ///  - Area.m_Flags (clears NoTriangles) and the Geometry component.
    ///
    /// If the ear-clip fails (self-intersecting ring) the vanilla triangles are left as they are.
    /// </summary>
    [UpdateAfter(typeof(Game.Areas.GeometrySystem))]
    public partial class MarkingAreaTriangulationSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _updatedOurs;
        private EntityQuery _allOurs;
        // Our fills whose Triangle buffer someone else wrote since this system last ran.
        private EntityQuery _retriangulatedOurs;
        private TerrainSystem _terrainSystem;
        private bool _loaded;

        // Fills whose ring the ear-clip could not solve. Without this set the reclaim scan would
        // retry and warn every tick. An entry is cleared when the entity passes through the
        // Updated path again, meaning its ring changed.
        private readonly HashSet<Entity> _healFailed =
            new HashSet<Entity>();

        // Hash of the triangle indices this system last wrote, per fill. A mismatch means the fill
        // was re-triangulated without an Updated tag (see ReclaimClobberedFills).
        private readonly Dictionary<Entity, int> _ownedFingerprint =
            new Dictionary<Entity, int>();

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
            _retriangulatedOurs = GetEntityQuery(
                ComponentType.ReadOnly<TRLAreaLink>(),
                ComponentType.ReadOnly<Area>(),
                ComponentType.ReadOnly<Game.Areas.Node>(),
                ComponentType.ReadWrite<Triangle>(),
                ComponentType.Exclude<Deleted>());
            _retriangulatedOurs.SetChangedVersionFilter(ComponentType.ReadWrite<Triangle>());
            RequireForUpdate(_allOurs);
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            // GeometrySystem re-triangulates every area on its first tick after a load, so this
            // system handles all fills on that tick as well.
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
            TerrainHeightData heightData = _terrainSystem.GetHeightData();
            if (!query.IsEmptyIgnoreFilter)
            {
                using var entities = query.ToEntityArray(Allocator.Temp);
                int done = 0, failed = 0;
                for (int i = 0; i < entities.Length; i++)
                {
                    try
                    {
                        if (RewriteTriangles(entities[i], ref heightData))
                        {
                            done++;
                            _healFailed.Remove(entities[i]);
                            _ownedFingerprint[entities[i]] = Fingerprint(EntityManager.GetBuffer<Triangle>(entities[i], isReadOnly: true));
                        }
                        else { failed++; _healFailed.Add(entities[i]); }
                    }
                    catch (System.Exception e)
                    {
                        // Keep going with the other fills; this one keeps its vanilla triangles.
                        failed++;
                        _healFailed.Add(entities[i]);
                        log.Warn($"area-triangulation ent#{entities[i].Index}: {e.GetType().Name}: {e.Message} — keeping vanilla triangles");
                    }
                }
                if (done > 0 || failed > 0)
                    log.Debug($"MarkingAreaTriangulationSystem: rewrote {done} fill(s){(failed > 0 ? $", {failed} left vanilla" : "")}");
            }

            ReclaimClobberedFills(ref heightData);
        }

        /// <summary>Order-sensitive hash of the triangle indices. Indices into the same ring fully
        /// determine the mesh, so positions are not needed.</summary>
        private static int Fingerprint(DynamicBuffer<Triangle> tris)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < tris.Length; i++)
                {
                    int3 idx = tris[i].m_Indices;
                    h = h * 31 + idx.x;
                    h = h * 31 + idx.y;
                    h = h * 31 + idx.z;
                }
                return h * 31 + tris.Length;
            }
        }

        /// <summary>
        /// When the terrain heightmap finishes streaming after a load
        /// (<c>TerrainHeightsReadyAfterLoading</c>), vanilla
        /// <see cref="Game.Areas.GeometrySystem"/> re-triangulates every area without tagging
        /// anything <c>Updated</c>. Fills it can't solve lose all their triangles and turn
        /// invisible. Fills it can solve get ears chosen on the shrunk polygon but drawn on the
        /// real ring, so the mesh spills past the contour near sharp tips.
        ///
        /// Nothing downstream is notified, so this scan compares the triangle indices of every
        /// fill whose Triangle buffer was written by someone else (a per-chunk change filter)
        /// with the fingerprint of what this system last wrote. On a mismatch it rewrites the
        /// triangles and tags the fill Updated, so the search tree and AreaBatchSystem refresh
        /// in the same frame.
        /// </summary>
        private void ReclaimClobberedFills(ref TerrainHeightData heightData)
        {
            if (_retriangulatedOurs.IsEmpty) return;
            using var candidates = _retriangulatedOurs.ToEntityArray(Allocator.Temp);
            var toHeal = new NativeList<Entity>(Allocator.Temp);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (_healFailed.Contains(candidates[i])) continue;
                int current = Fingerprint(EntityManager.GetBuffer<Triangle>(candidates[i], isReadOnly: true));
                if (!_ownedFingerprint.TryGetValue(candidates[i], out int owned) || current != owned)
                    toHeal.Add(candidates[i]);
            }

            int healed = 0;
            for (int i = 0; i < toHeal.Length; i++)
            {
                Entity e = toHeal[i];
                try
                {
                    if (RewriteTriangles(e, ref heightData))
                    {
                        healed++;
                        _ownedFingerprint[e] = Fingerprint(EntityManager.GetBuffer<Triangle>(e, isReadOnly: true));
                    }
                    else { _healFailed.Add(e); toHeal[i] = Entity.Null; }
                }
                catch (System.Exception ex)
                {
                    _healFailed.Add(e);
                    toHeal[i] = Entity.Null;
                    log.Warn($"area-triangulation ent#{e.Index}: {ex.GetType().Name}: {ex.Message} — reclaim failed");
                }
            }

            if (healed > 0)
            {
                // Structural changes last: AddComponent invalidates buffer handles.
                for (int i = 0; i < toHeal.Length; i++)
                    if (toHeal[i] != Entity.Null)
                        EntityManager.AddComponent<Updated>(toHeal[i]);
                log.Info($"MarkingAreaTriangulationSystem: reclaimed {healed} fill(s) re-triangulated behind our back (vanilla post-load terrain pass)");
            }
            toHeal.Dispose();

            // Entity keys include the version, so recycled indices never collide, but keys of
            // deleted fills pile up as fills are respawned. Prune them now and then.
            if (_ownedFingerprint.Count > _allOurs.CalculateEntityCount() * 2 + 32)
            {
                using var all = _allOurs.ToEntityArray(Allocator.Temp);
                var alive = new HashSet<Entity>();
                for (int i = 0; i < all.Length; i++) alive.Add(all[i]);
                var dead = new List<Entity>();
                foreach (var kv in _ownedFingerprint)
                    if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
                for (int i = 0; i < dead.Count; i++) _ownedFingerprint.Remove(dead[i]);
                _healFailed.RemoveWhere(e => !alive.Contains(e));
            }
        }

        /// <summary>Replaces the entity's triangles with a triangulation of its exact node ring.
        /// Returns false, leaving the vanilla data as is, when the ring can't be
        /// triangulated.</summary>
        private bool RewriteTriangles(Entity entity, ref TerrainHeightData heightData)
        {
            var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, isReadOnly: true);
            int n = nodes.Length;
            if (n < 3) return false;

            var positions = new NativeArray<float3>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) positions[i] = nodes[i].m_Position;

            // The ear-clip needs the winding to tell convex corners from reflex ones, whichever
            // direction the area was drawn in.
            bool ccw = SignedAreaXZ(positions) > 0f;

            var tris = new NativeList<Triangle>(2 * n, Allocator.Temp);
            bool ok = EarClip(positions, ccw, tris);
            if (!ok)
            {
                // Self-intersecting ring, e.g. a straight edge crossing a curved one. The caller
                // records the failure, so this warns once per ring change.
                log.Warn($"area-triangulation ent#{entity.Index}: ear-clip failed on {n}-node ring — keeping vanilla triangles");
                positions.Dispose();
                tris.Dispose();
                return false;
            }

            var triangles = EntityManager.GetBuffer<Triangle>(entity);
            triangles.Clear();
            for (int i = 0; i < tris.Length; i++) triangles.Add(tris[i]);
            tris.Dispose();

            // Vanilla edge-flip pass for better-shaped triangles.
            Game.Areas.GeometrySystem.EqualizeTriangles(positions, triangles);

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
            // A fill on a bridge deck has nodes with an explicit elevation instead of the
            // terrain-following float.MinValue. It changes the height range and center below.
            bool elevated = false;
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i].m_Elevation != float.MinValue) { elevated = true; break; }
            for (int i = 0; i < triangles.Length; i++)
            {
                Triangle tri = triangles[i];
                Triangle3 tri3 = AreaUtils.GetTriangle3(nodes, tri);

                Bounds3 triBounds = MathUtils.Bounds(tri3);
                Bounds1 offsetBounds = new Bounds1(math.min(0f, heightOffset), math.max(0f, heightOffset));
                if (elevated)
                {
                    // The height range is also the decal projection volume (AreaBatchSystem feeds
                    // it into m_YMinMax), not only a culling bound. Reaching down to the terrain
                    // would paint the fill on the ground under the bridge as well, so keep it
                    // tight around the deck.
                    tri.m_HeightRange = new Bounds1(-0.5f, 0.5f) | offsetBounds;
                }
                else
                {
                    // Terrain min/max over the triangle's bounding box, relative to the triangle's
                    // own height. Vanilla rasterizes the exact triangle; the box can only widen
                    // the range, which is safe.
                    Bounds1 terrain = TerrainUtils.GetHeightRange(ref heightData, triBounds);
                    if (terrain.min <= terrain.max)
                    {
                        tri.m_HeightRange = new Bounds1(terrain.min - triBounds.max.y, terrain.max - triBounds.min.y) | offsetBounds;
                    }
                    else
                    {
                        tri.m_HeightRange = offsetBounds;
                    }
                }

                // Vanilla MinLod: the clearance to the polygon boundary at the best candidate
                // point, times 4, approximates the fill's local rendering size.
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
            // The candidate's height comes from the nodes. Ground fills snap it to the terrain
            // like vanilla; elevated fills keep the deck height, or the area popover and culling
            // center would end up on the ground under the bridge.
            if (!elevated)
                geometry.m_CenterPosition.y = TerrainUtils.SampleHeight(ref heightData, geometry.m_CenterPosition);

            if (EntityManager.HasComponent<Geometry>(entity))
                EntityManager.SetComponentData(entity, geometry);

            var area = EntityManager.GetComponentData<Area>(entity);
            area.m_Flags &= ~AreaFlags.NoTriangles;
            EntityManager.SetComponentData(entity, area);

            positions.Dispose();
            return true;
        }

        /// <summary>Port of vanilla's CheckCenterPositionCandidate scheme, brute force over the
        /// ring's edges. Candidates are the midpoints of triangle edges that are not polygon
        /// edges, plus a weighted center for triangles with one polygon edge and the centroid
        /// as a fallback. Each candidate scores the two smallest squared distances to the
        /// polygon edges (min1, min2); the largest min1 wins.</summary>
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

        /// <summary>The two smallest squared XZ distances from <paramref name="point"/> to the
        /// polygon's edges, as (min1, min2).</summary>
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

        /// <summary>O(n²) ear-clipping in the XZ plane, without vanilla's shrink step or attempt
        /// limit, so any simple polygon yields n - 2 triangles. Zero-area ears are clipped as
        /// a fallback, as vanilla tolerates collinear points. Returns false only when no ear
        /// exists (self-intersecting ring).</summary>
        private static bool EarClip(NativeArray<float3> positions, bool ccw, NativeList<Triangle> outTris)
        {
            int n = positions.Length;
            var index = new NativeList<int>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) index.Add(i);

            while (index.Length > 3)
            {
                int m = index.Length;
                int earAt = -1;
                // First pass: strictly convex ears. Second pass: also zero-area (collinear) ears,
                // which unblocks rings with duplicate or collinear points. Both passes require
                // the ear to contain no other vertex: such an ear puts triangles outside the
                // polygon, and falling back to vanilla is better than a fill spilling past
                // its contour.
                for (int pass = 0; pass < 2 && earAt < 0; pass++)
                {
                    float minCross = pass == 0 ? 1e-6f : -1e-6f;
                    for (int i = 0; i < m; i++)
                    {
                        int i0 = index[(i + m - 1) % m], i1 = index[i], i2 = index[(i + 1) % m];
                        float2 a = positions[i0].xz, b = positions[i1].xz, c = positions[i2].xz;
                        float cross = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                        if (!ccw) cross = -cross;
                        if (cross < minCross) continue; // reflex corner
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
                // Zero-area ears are emitted too, to keep vanilla's invariant of exactly n - 2
                // triangles per ring. They draw nothing and never hit-test.
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
