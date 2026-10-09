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
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.Emission;
using TownRoadLane.Utilities;
using static TownRoadLane.Geometry.PolygonUtils;

namespace TownRoadLane.Systems.Topology
{
    /// <summary>
    /// Resolves each <see cref="MarkingArea"/> into a world-space <see cref="MarkingAreaPiece"/>
    /// whenever a node's areas or lines change (tracked by a hash of both). The ring is the
    /// contour exactly as drawn: straight chords plus sampled line curves, see
    /// <see cref="ResolveOuterRing"/>. Thin shapes are fine because
    /// <see cref="MarkingAreaTriangulationSystem"/> triangulates the fills itself. Lines drawn
    /// across an area are only an overlay and do not cut it.
    ///
    /// Runs after <see cref="MarkingTopologySystem"/> for the current line buffer and before
    /// <see cref="MarkingAreaEmissionSystem"/>, which spawns fills from the pieces. Like the line
    /// topology, the per-node scan only runs when an input buffer or a state changed, a node has
    /// no state yet, or a node is waiting for its roads.
    /// Order against line topology and area emission is the UpdateSystem registration in Mod.OnLoad.
    /// </summary>
    public partial class MarkingAreaTopologySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithAreas;
        private EntityQuery _spawnedAreas;
        private EntityQuery _nodesWithoutState;
        // One query per watched type: a change filter on several types is not needed and each
        // query stays trivially cheap when nothing changed.
        private EntityQuery[] _changeWatchers;

        // Ticks each node has been waiting for its edges to become ready (see RecomputeIfChanged).
        private readonly Dictionary<Entity, int> _deferredTicks = new Dictionary<Entity, int>();
        private const int kDeferredWarnTicks = 600;

        protected override void OnCreate()
        {
            base.OnCreate();
            // Temp nodes are the road tool's preview copies, buffers included; writing to them
            // while the tool applies crashes the game (same as in MarkingTopologySystem).
            _nodesWithAreas = GetEntityQuery(
                ComponentType.ReadOnly<MarkingArea>(),
                ComponentType.ReadOnly<Node>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            _spawnedAreas = GetEntityQuery(
                ComponentType.ReadOnly<TRLAreaLink>(),
                ComponentType.Exclude<Deleted>());
            _nodesWithoutState = GetEntityQuery(
                ComponentType.ReadOnly<MarkingArea>(),
                ComponentType.ReadOnly<Node>(),
                ComponentType.Exclude<MarkingAreaTopologyState>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            _changeWatchers = new[]
            {
                ChangedQuery(ComponentType.ReadOnly<MarkingArea>()),
                ChangedQuery(ComponentType.ReadOnly<MarkingAreaVertex>()),
                ChangedQuery(ComponentType.ReadOnly<MarkingLine>()),
                ChangedQuery(ComponentType.ReadOnly<MarkingAreaTopologyState>()),
            };
            RequireForUpdate(_nodesWithAreas);
        }

        /// <summary>Nodes with areas whose <paramref name="watched"/> component changed. Unlike
        /// _nodesWithAreas these queries leave out Node: GetEntityQuery returns the cached query
        /// for an identical component set, and the change filter would land on both.</summary>
        private EntityQuery ChangedQuery(ComponentType watched)
        {
            var area = ComponentType.ReadOnly<MarkingArea>();
            var query = GetEntityQuery(new EntityQueryDesc
            {
                All = watched == area ? new[] { area } : new[] { area, watched },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            query.SetChangedVersionFilter(watched);
            return query;
        }

        private bool AnythingToRebuild()
        {
            if (_deferredTicks.Count > 0 || !_nodesWithoutState.IsEmpty) return true;
            for (int i = 0; i < _changeWatchers.Length; i++)
                if (!_changeWatchers[i].IsEmpty) return true;
            return false;
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            // Entity indices are reused between saves, so counters left from the previous save
            // would belong to unrelated nodes.
            _deferredTicks.Clear();
        }

        protected override void OnUpdate()
        {
            if (!AnythingToRebuild()) return;

            using var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp);
            int rewritten = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (RecomputeIfChanged(nodes[i])) rewritten++;
            }
            // A node deleted while waiting would otherwise keep the scan running every frame.
            if (_deferredTicks.Count > 0)
            {
                var alive = new HashSet<Entity>(nodes);
                var gone = new List<Entity>();
                foreach (var node in _deferredTicks.Keys)
                    if (!alive.Contains(node)) gone.Add(node);
                for (int i = 0; i < gone.Count; i++) _deferredTicks.Remove(gone[i]);
            }
            if (rewritten > 0) log.Debug($"MarkingAreaTopologySystem: recomputed pieces on {rewritten} node(s)");
        }

        private bool RecomputeIfChanged(Entity node)
        {
            if (!EntityManager.HasBuffer<MarkingArea>(node)) return false;
            var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
            if (areas.Length == 0 && !EntityManager.HasBuffer<MarkingAreaPiece>(node))
                return false;
            if (!EntityManager.HasBuffer<MarkingAreaVertex>(node)) return false;
            var areaVerts = EntityManager.GetBuffer<MarkingAreaVertex>(node, isReadOnly: true);

            DynamicBuffer<MarkingLine> lines = default;
            bool hasLines = EntityManager.HasBuffer<MarkingLine>(node);
            if (hasLines) lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);

            int newHash = HashAreaAndLines(areas, areaVerts, hasLines ? (DynamicBuffer<MarkingLine>?)lines : null);
            int oldHash = EntityManager.HasComponent<MarkingAreaTopologyState>(node)
                ? EntityManager.GetComponentData<MarkingAreaTopologyState>(node).combinedHash
                : 0;
            if (newHash == oldHash && EntityManager.HasBuffer<MarkingAreaPiece>(node))
                return false;

            // Right after a load, Composition and EdgeGeometry of the connected edges are still
            // zeroed (refilled in Modification3/4, after this system), so anchor extraction finds
            // nothing and every ring would fail, replacing the saved pieces. Wait while any
            // connected edge exists but isn't ready.
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                var connected = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
                for (int i = 0; i < connected.Length; i++)
                {
                    if (MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, connected[i].m_Edge))
                    {
                        // Some edges never become ready (certain Road Builder or custom nets).
                        // The area then never gets a fill, so warn once instead of staying silent.
                        _deferredTicks.TryGetValue(node, out var ticks);
                        _deferredTicks[node] = ticks + 1;
                        if (ticks + 1 == kDeferredWarnTicks)
                            log.Warn($"area-topology node#{node.Index}: deferred {kDeferredWarnTicks} ticks — connected edge #{connected[i].m_Edge.Index} never extraction-ready, area pieces are not being built");
                        return false;
                    }
                }
            }
            _deferredTicks.Remove(node);

            // Copy the buffers first: structural changes below invalidate the handles.
            int areaCount = areas.Length;
            var areasSnap = new NativeArray<MarkingArea>(areaCount, Allocator.Temp);
            for (int i = 0; i < areaCount; i++) areasSnap[i] = areas[i];
            int areaVertCount = areaVerts.Length;
            var areaVertsSnap = new NativeArray<MarkingAreaVertex>(areaVertCount, Allocator.Temp);
            for (int i = 0; i < areaVertCount; i++) areaVertsSnap[i] = areaVerts[i];

            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            var corners = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);

            // Lines are needed to resolve crossing vertices (kind 2).
            var linesSnap = new MarkingLine[hasLines ? lines.Length : 0];
            for (int i = 0; i < linesSnap.Length; i++) linesSnap[i] = lines[i];

            // Old pieces: visibility to inherit, and fallback geometry.
            var oldPiecesByArea = new List<List<(MarkingAreaPiece header, List<float3> ring)>>(areaCount);
            for (int i = 0; i < areaCount; i++) oldPiecesByArea.Add(new List<(MarkingAreaPiece, List<float3>)>());
            if (EntityManager.HasBuffer<MarkingAreaPiece>(node) && EntityManager.HasBuffer<MarkingAreaPieceVertex>(node))
            {
                var oldPieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
                var oldVerts = EntityManager.GetBuffer<MarkingAreaPieceVertex>(node, isReadOnly: true);
                for (int i = 0; i < oldPieces.Length; i++)
                {
                    var op = oldPieces[i];
                    if (op.areaIndex < 0 || op.areaIndex >= areaCount) continue;
                    var ring = new List<float3>(op.vertexCount);
                    for (int v = 0; v < op.vertexCount; v++)
                    {
                        int idx = op.firstVertex + v;
                        if (idx >= 0 && idx < oldVerts.Length) ring.Add(oldVerts[idx].position);
                    }
                    oldPiecesByArea[op.areaIndex].Add((op, ring));
                }
            }

            // Migrate version 1 area vertices. They name kind 0/1 anchors by raw list index, and
            // that order changes between loads, so the index may now point at a different dot.
            // The saved piece ring is reliable world-space geometry: resolve each legacy vertex
            // by index, accept it only if it lands on the area's saved ring, and then store the
            // stable edge/gap identity. An area with any unconfirmed vertex keeps its saved
            // pieces for now and is retried on the next rebuild.
            var unconfirmedAreas = new HashSet<int>();
            {
                var vertsRW = EntityManager.GetBuffer<MarkingAreaVertex>(node);
                bool migratedAny = false;
                var pending = new List<(int bufIdx, MarkingAreaVertex stamped)>();
                var ringIdxSeq = new List<int>();
                for (int a = 0; a < areaCount; a++)
                {
                    var ad = areasSnap[a];
                    bool hasLegacy = false;
                    for (int v = 0; v < ad.vertexCount; v++)
                    {
                        int idx = ad.firstVertex + v;
                        if (idx >= 0 && idx < vertsRW.Length)
                        {
                            var t = vertsRW[idx];
                            if (t.IsLegacyIndexRef) { hasLegacy = true; break; }
                        }
                    }
                    if (!hasLegacy) continue;
                    if (oldPiecesByArea[a].Count == 0) continue; // no saved ring: resolve by index
                    var oldRing = oldPiecesByArea[a][0].ring;    // one piece per area
                    if (oldRing.Count < 3) continue;

                    // Map every vertex of the area onto the saved ring. Legacy vertices must lie
                    // on it (anchors are always ring points), and the sequence must walk the ring
                    // in cyclic order: an index that picked up another anchor of the same area
                    // also lies on the ring but breaks the order (a bowtie).
                    pending.Clear();
                    ringIdxSeq.Clear();
                    bool confirmed = true;
                    for (int v = 0; v < ad.vertexCount && confirmed; v++)
                    {
                        int idx = ad.firstVertex + v;
                        if (idx < 0 || idx >= vertsRW.Length) { confirmed = false; break; }
                        var av = vertsRW[idx];
                        float3 pos;
                        if (av.IsLegacyIndexRef)
                        {
                            Entity edgeA, edgeB = Entity.Null;
                            int gap = 0;
                            if (av.kind == AreaAnchorKind.LaneEndpoint && av.refIndex >= 0 && av.refIndex < endpoints.Count)
                            {
                                var ep = endpoints[av.refIndex];
                                pos = ep.position; edgeA = ep.edge; gap = ep.gapIndex;
                            }
                            else if (av.kind == AreaAnchorKind.NodeCorner && av.refIndex >= 0 && av.refIndex < corners.Count)
                            {
                                var ca = corners[av.refIndex];
                                pos = ca.position; edgeA = ca.edgeA; edgeB = ca.edgeB;
                            }
                            else { confirmed = false; break; }
                            if (NearestRingIndex(oldRing, pos, out int rIdx) > 0.25f) { confirmed = false; break; }
                            ringIdxSeq.Add(rIdx);
                            av.refEdgeA = edgeA; av.refEdgeB = edgeB; av.refGap = gap; av.refPos = pos;
                            pending.Add((idx, av));
                        }
                        else
                        {
                            if (!ResolveVertexPos(av, endpoints, corners, linesSnap, out pos)) { confirmed = false; break; }
                            NearestRingIndex(oldRing, pos, out int rIdx);
                            ringIdxSeq.Add(rIdx);
                        }
                    }
                    // Check the cyclic order, then write all of the area's vertices or none.
                    if (confirmed && ringIdxSeq.Count >= 3)
                    {
                        int L = oldRing.Count;
                        int prev = 0;
                        for (int v = 1; v < ringIdxSeq.Count; v++)
                        {
                            int rel = (ringIdxSeq[v] - ringIdxSeq[0] + L) % L;
                            if (rel < prev) { confirmed = false; break; }
                            prev = rel;
                        }
                    }
                    if (!confirmed)
                    {
                        unconfirmedAreas.Add(a);
                        continue;
                    }
                    for (int p = 0; p < pending.Count; p++)
                        vertsRW[pending[p].bufIdx] = pending[p].stamped;
                    if (pending.Count > 0) migratedAny = true;
                }
                if (migratedAny)
                {
                    for (int i = 0; i < areaVertCount && i < vertsRW.Length; i++) areaVertsSnap[i] = vertsRW[i];
                    log.Info($"area-topology node#{node.Index}: stamped stable v2 identity on legacy area vertices");
                }
            }

            // One piece per area. Areas are not cut by lines that cross them: an area keeps the
            // shape it was drawn with. The piece layer stays for save compatibility.
            var newPieces = new List<MarkingAreaPiece>(areaCount);
            var newVerts = new List<MarkingAreaPieceVertex>(areaCount * 8);

            for (int a = 0; a < areaCount; a++)
            {
                var ad = areasSnap[a];
                // Hidden areas still get pieces: emission filters on area visibility, and the
                // per-piece visibility then survives hiding and showing the area.
                if (ad.vertexCount < 3) continue;

                // An exception escaping OnUpdate would repeat every tick, flooding the log and
                // skipping every node after this one. A throwing ring is treated as unresolvable:
                // the cached pieces are kept and the hash write stops the retries. Unconfirmed
                // legacy areas take the same path.
                List<float3> outerRing = null;
                if (!unconfirmedAreas.Contains(a))
                {
                    try
                    {
                        outerRing = ResolveOuterRing(areasSnap[a], areaVertsSnap, endpoints, corners, linesSnap);
                    }
                    catch (System.Exception e)
                    {
                        log.Warn($"area-topology node#{node.Index} area#{a}: ring builder threw ({e.GetType().Name}: {e.Message}), keeping cached pieces");
                    }
                }
                if (outerRing == null || outerRing.Count < 3)
                {
                    // An anchor is gone for good (road changed; the post-load case waits above).
                    // Keep the old pieces as they are: the cached geometry is the best available,
                    // and the per-piece visibility survives.
                    var carried = oldPiecesByArea[a];
                    if (carried.Count == 0)
                        log.Warn($"area-topology node#{node.Index} area#{a}: outer ring unresolvable and no cached pieces — this area will have no fill");
                    for (int p = 0; p < carried.Count; p++)
                    {
                        var (header, ring) = carried[p];
                        if (ring.Count < 3) continue;
                        header.areaIndex = a;
                        header.firstVertex = newVerts.Count;
                        header.vertexCount = ring.Count;
                        newPieces.Add(header);
                        for (int v = 0; v < ring.Count; v++)
                            newVerts.Add(new MarkingAreaPieceVertex { position = ring[v] });
                    }
                    continue;
                }

                if (math.abs(SignedAreaXZ(outerRing)) < kMinPieceAreaM2)
                {
                    log.Warn($"area-topology node#{node.Index} area#{a}: ring area {math.abs(SignedAreaXZ(outerRing)):F2} m² below {kMinPieceAreaM2} m² minimum — piece dropped");
                    continue;
                }

                float3 c = PolygonUtils.CentroidXZ(outerRing);
                bool visible = LookupInheritedVisibility(oldPiecesByArea[a], c, defaultVisible: true);
                int firstVertexIdx = newVerts.Count;
                for (int v = 0; v < outerRing.Count; v++)
                    newVerts.Add(new MarkingAreaPieceVertex { position = outerRing[v] });
                newPieces.Add(new MarkingAreaPiece
                {
                    areaIndex = a,
                    pieceIndex = 0,
                    visible = visible,
                    firstVertex = firstVertexIdx,
                    vertexCount = outerRing.Count,
                    centroid = c,
                });
            }

            var pieceBuf = EntityManager.HasBuffer<MarkingAreaPiece>(node)
                ? EntityManager.GetBuffer<MarkingAreaPiece>(node)
                : EntityManager.AddBuffer<MarkingAreaPiece>(node);
            pieceBuf.Clear();
            for (int i = 0; i < newPieces.Count; i++) pieceBuf.Add(newPieces[i]);

            var vertBuf = EntityManager.HasBuffer<MarkingAreaPieceVertex>(node)
                ? EntityManager.GetBuffer<MarkingAreaPieceVertex>(node)
                : EntityManager.AddBuffer<MarkingAreaPieceVertex>(node);
            vertBuf.Clear();
            for (int i = 0; i < newVerts.Count; i++) vertBuf.Add(newVerts[i]);

            if (EntityManager.HasComponent<MarkingAreaTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = newHash });
            else
                EntityManager.AddComponentData(node, new MarkingAreaTopologyState { combinedHash = newHash });

            EntityManager.MarkUpdated(node);

            // Delete every fill spawned for this node; emission respawns them next tick. Its diff
            // matches only by key and prefab, so a fill whose key still matches would keep stale
            // geometry: deleting area #1 would leave its old fill standing in as the new #1 and
            // remove the last area's fill instead.
            using (var spawned = _spawnedAreas.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < spawned.Length; i++)
                {
                    if (EntityManager.GetComponentData<TRLAreaLink>(spawned[i]).node != node) continue;
                    EntityManager.AddComponent<Deleted>(spawned[i]);
                }
            }

            areasSnap.Dispose();
            areaVertsSnap.Dispose();

            log.Debug($"area-topology node#{node.Index}: {areaCount} area(s) → {newPieces.Count} piece(s)");
            return true;
        }

        /// <summary>Builds the outer ring of one area exactly as drawn. Each edge is either a
        /// straight chord between its anchors or the sampled part of the marking line both
        /// anchors lie on, using the same sampling as the tool's preview
        /// (<see cref="SampleCurvedEdge"/>), so the fill matches the preview point for point.
        /// No minimum width is enforced: <see cref="MarkingAreaTriangulationSystem"/> handles
        /// knife-edge corners and sub-metre islands that vanilla triangulation would drop.
        ///
        /// Returns null when a vertex can't be resolved (line removed, road demolished); the
        /// caller then keeps the cached pieces.</summary>
        private static List<float3> ResolveOuterRing(MarkingArea ad, NativeArray<MarkingAreaVertex> verts,
                                                     List<MarkingEndpoint> endpoints, List<MarkingCornerAnchor> corners,
                                                     MarkingLine[] lines)
        {
            int n = ad.vertexCount;
            if (n < 3) return null;

            var anchors = new float3[n];
            var avs = new MarkingAreaVertex[n];
            for (int v = 0; v < n; v++)
            {
                int idx = ad.firstVertex + v;
                if (idx < 0 || idx >= verts.Length) return null;
                avs[v] = verts[idx];
                if (!ResolveVertexPos(avs[v], endpoints, corners, lines, out anchors[v])) return null;
            }

            // Each anchor, followed by the simplified interior points of a curved edge.
            var ring = new List<float3>(n * 4);
            for (int v = 0; v < n; v++)
            {
                ring.Add(anchors[v]);
                if (avs[v].edgeToNext == AreaEdgeKind.LineBezier
                    && TryFindSharedLine(avs[v], avs[(v + 1) % n], endpoints, lines, out _, out var bez, out float tFrom, out float tTo))
                {
                    SampleCurvedEdge(bez, tFrom, tTo, ring);
                }
            }

            // Merge consecutive points closer than 5 cm (coincident anchors, tiny edges).
            for (int i = ring.Count - 1; i > 0; i--)
                if (DistSqXZ(ring[i], ring[i - 1]) < 0.0025f) ring.RemoveAt(i);
            if (ring.Count > 1 && DistSqXZ(ring[0], ring[ring.Count - 1]) < 0.0025f)
                ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        private static bool TryFindSharedLine(MarkingAreaVertex from, MarkingAreaVertex to,
                                              List<MarkingEndpoint> endpoints, MarkingLine[] lines,
                                              out int lineIndex, out Bezier4x3 bez, out float tFrom, out float tTo)
        {
            lineIndex = -1;
            bez = default;
            tFrom = 0f;
            tTo = 0f;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryAnchorParamOnLine(from, i, lines, endpoints, out tFrom)) continue;
                if (!TryAnchorParamOnLine(to, i, lines, endpoints, out tTo)) continue;
                if (math.abs(tTo - tFrom) < 1e-4f) continue; // zero-length span
                if (!MarkingCurveBuilder.TryBuild(endpoints, lines[i], out bez)) continue;
                lineIndex = i;
                return true;
            }
            return false;
        }

        /// <summary>Index of the ring point closest to <paramref name="pos"/> in XZ; returns the
        /// squared distance.</summary>
        private static float NearestRingIndex(List<float3> ring, float3 pos, out int index)
        {
            index = 0;
            float best = float.MaxValue;
            for (int i = 0; i < ring.Count; i++)
            {
                float sq = DistSqXZ(ring[i], pos);
                if (sq < best) { best = sq; index = i; }
            }
            return best;
        }

        // Rings with a smaller area are dropped.
        private const float kMinPieceAreaM2 = 0.5f;

        private static bool ResolveVertexPos(MarkingAreaVertex av, List<MarkingEndpoint> endpoints,
                                             List<MarkingCornerAnchor> corners, MarkingLine[] lines, out float3 pos)
        {
            pos = default;
            if (av.kind == AreaAnchorKind.LaneEndpoint)
            {
                int idx = MarkingEndpointExtractor.ResolveEndpointIndex(endpoints, av);
                if (idx < 0) return false;
                pos = endpoints[idx].position;
                return true;
            }
            if (av.kind == AreaAnchorKind.NodeCorner)
            {
                int idx = MarkingEndpointExtractor.ResolveCornerIndex(corners, av);
                if (idx < 0) return false;
                pos = corners[idx].position;
                return true;
            }
            if (av.kind == AreaAnchorKind.LineIntersection) // refIndex is the packed (lineA, lineB, hit)
                return MarkingIntersectionExtractor.TryResolve(endpoints, lines, av.refIndex, out pos);
            return false;
        }

        // Curved edges are sampled finely, then simplified with Douglas-Peucker so points remain
        // only where the curve actually bends. Extra points on straight stretches only add
        // near-degenerate triangles.
        private const float kFineSampleSpacingM = 0.75f;
        private const int kFineSampleMax = 48;
        private const float kSimplifyTolM = 0.06f;

        /// <summary>Appends the interior points of a curved edge from tFrom to tTo, without the
        /// endpoints. Shared by the tool's preview and <see cref="ResolveOuterRing"/>, so the
        /// preview shows exactly what gets filled.</summary>
        public static void SampleCurvedEdge(Bezier4x3 bez, float tFrom, float tTo, List<float3> into)
        {
            var pFrom = MathUtils.Position(bez, tFrom);
            var pTo = MathUtils.Position(bez, tTo);
            float chord = math.sqrt(DistSqXZ(pFrom, pTo));
            int fine = math.clamp((int)math.ceil(chord / kFineSampleSpacingM), 1, kFineSampleMax);
            if (fine < 2) return;

            var pts = new List<float3>(fine + 1) { pFrom };
            for (int s = 1; s < fine; s++)
                pts.Add(MathUtils.Position(bez, math.lerp(tFrom, tTo, s / (float)fine)));
            pts.Add(pTo);

            var keep = new bool[pts.Count];
            SimplifyDP(pts, 0, pts.Count - 1, kSimplifyTolM, keep);
            for (int i = 1; i < pts.Count - 1; i++)
                if (keep[i]) into.Add(pts[i]);
        }

        /// <summary>Douglas-Peucker over pts[first..last] in XZ: marks interior points that
        /// deviate from the chord by more than tol. The endpoints are the caller's.</summary>
        private static void SimplifyDP(List<float3> pts, int first, int last, float tol, bool[] keep)
        {
            if (last - first < 2) return;
            float ax = pts[first].x, az = pts[first].z;
            float dx = pts[last].x - ax, dz = pts[last].z - az;
            float len = math.max(math.sqrt(dx * dx + dz * dz), 1e-6f);
            int worst = -1;
            float worstDist = tol;
            for (int i = first + 1; i < last; i++)
            {
                float dev = math.abs((pts[i].x - ax) * dz - (pts[i].z - az) * dx) / len;
                if (dev > worstDist) { worstDist = dev; worst = i; }
            }
            if (worst < 0) return;
            keep[worst] = true;
            SimplifyDP(pts, first, worst, tol, keep);
            SimplifyDP(pts, worst, last, tol, keep);
        }

        /// <summary>t parameter of an anchor on the given line's Bezier: a lane endpoint is the
        /// line's source (0) or target (1); a crossing contributes the parameter of whichever
        /// pair member matches. Corners never lie on a line.</summary>
        private static bool TryAnchorParamOnLine(MarkingAreaVertex av, int lineIndex, MarkingLine[] lines,
                                                 List<MarkingEndpoint> endpoints, out float t)
        {
            t = 0f;
            if (av.kind == AreaAnchorKind.LaneEndpoint)
            {
                int epIdx = MarkingEndpointExtractor.ResolveEndpointIndex(endpoints, av);
                if (epIdx < 0) return false;
                var ep = endpoints[epIdx];
                var ln = lines[lineIndex];
                if (ln.sourceEdge == ep.edge && ln.sourceGapIndex == ep.gapIndex) { t = 0f; return true; }
                if (ln.targetEdge == ep.edge && ln.targetGapIndex == ep.gapIndex) { t = 1f; return true; }
                return false;
            }
            if (av.kind == AreaAnchorKind.LineIntersection)
            {
                MarkingIntersectionExtractor.Unpack(av.refIndex, out int a, out int b, out _);
                if (lineIndex != a && lineIndex != b) return false;
                if (!MarkingIntersectionExtractor.TryResolveAnchor(endpoints, lines, av.refIndex, out var anchor)) return false;
                t = lineIndex == a ? anchor.tA : anchor.tB;
                return true;
            }
            return false;
        }

        /// <summary>Visibility of the first old piece that contains the new piece's centroid, or
        /// <paramref name="defaultVisible"/> if none does.</summary>
        private static bool LookupInheritedVisibility(List<(MarkingAreaPiece header, List<float3> ring)> oldPieces,
                                                     float3 newCentroid, bool defaultVisible)
        {
            for (int i = 0; i < oldPieces.Count; i++)
            {
                if (PolygonUtils.ContainsXZ(oldPieces[i].ring, newCentroid))
                    return oldPieces[i].header.visible;
            }
            return defaultVisible;
        }

        private static int HashAreaAndLines(DynamicBuffer<MarkingArea> areas, DynamicBuffer<MarkingAreaVertex> verts, DynamicBuffer<MarkingLine>? lines)
        {
            // Bump kAlgoVersion whenever the ring built from the same input changes, so existing
            // areas are rebuilt.
            const uint kAlgoVersion = 11;
            var h = Fnv1a.Create();
            h.Add(kAlgoVersion);
            for (int i = 0; i < areas.Length; i++)
            {
                var a = areas[i];
                h.Add(a.styleId);
                h.Add(a.visible);
                h.Add(a.firstVertex);
                h.Add(a.vertexCount);
            }
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                h.Add((uint)v.kind);
                h.Add(v.refIndex);
                h.Add((uint)v.edgeToNext);
            }
            if (lines.HasValue) MarkingTopologySystem.AddLines(ref h, lines.Value);
            return h.Value32;
        }
    }
}
