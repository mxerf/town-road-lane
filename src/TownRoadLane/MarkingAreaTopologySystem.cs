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
    /// Phase 6e: owns the (<see cref="MarkingArea"/> + <see cref="MarkingLine"/>) →
    /// <see cref="MarkingAreaPiece"/> relationship. Mirrors <see cref="MarkingTopologySystem"/>
    /// which does the same job for lines (splitting them at intersections with other lines).
    ///
    /// On every change to either MarkingArea or MarkingLine on a node, rebuilds each area's
    /// outer ring as the TRUE drawn contour (straight chords + sampled line curves — see
    /// <see cref="ResolveOuterRing"/>) and stores it as ONE piece per area; arbitrary thin
    /// shapes are fine because <see cref="MarkingAreaTriangulationSystem"/> (phase 8) owns
    /// the triangulation of our fills.
    /// Auto-splitting areas along crossing lines is gone (phase 7e) — a line over an area is
    /// purely cosmetic. Visibility per piece is inherited from whichever old piece contained
    /// the new piece's centroid — keeps user edits stable across topology changes.
    ///
    /// Trigger: combined hash of MarkingArea + MarkingLine buffer. Skips work when both
    /// unchanged.
    ///
    /// Ordering: runs after <see cref="MarkingTopologySystem"/> so it sees the latest line
    /// buffer, and before <see cref="MarkingAreaEmissionSystem"/> so the piece buffer is fresh
    /// when emission diffs against spawned entities.
    /// </summary>
    [UpdateAfter(typeof(MarkingTopologySystem))]
    [UpdateBefore(typeof(MarkingAreaEmissionSystem))]
    public partial class MarkingAreaTopologySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithAreas;
        private EntityQuery _spawnedAreas;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nodesWithAreas = GetEntityQuery(
                ComponentType.ReadOnly<MarkingArea>(),
                ComponentType.ReadOnly<Node>());
            _spawnedAreas = GetEntityQuery(
                ComponentType.ReadOnly<TRLAreaLink>(),
                ComponentType.Exclude<Deleted>());
            RequireForUpdate(_nodesWithAreas);
        }

        protected override void OnUpdate()
        {
            using var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp);
            int rewritten = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (RecomputeIfChanged(nodes[i])) rewritten++;
            }
            if (rewritten > 0) log.Info($"MarkingAreaTopologySystem: recomputed pieces on {rewritten} node(s)");
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

            // Save/load guard: right after loading a save, Composition/EdgeGeometry on the
            // connected edges are still zeroed (refilled at Modification3/4, after us), so
            // endpoint/corner extraction returns nothing and every ring would fail to resolve —
            // wiping the saved pieces and their per-piece visibility. Defer the whole node while
            // any connected edge is alive but not yet extraction-ready; retry next tick.
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                var connected = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
                for (int i = 0; i < connected.Length; i++)
                {
                    if (MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, connected[i].m_Edge))
                        return false;
                }
            }

            // Snapshot before any structural change — buffers become invalid the moment we
            // touch AddBuffer/RemoveComponent below.
            int areaCount = areas.Length;
            var areasSnap = new NativeArray<MarkingArea>(areaCount, Allocator.Temp);
            for (int i = 0; i < areaCount; i++) areasSnap[i] = areas[i];
            int areaVertCount = areaVerts.Length;
            var areaVertsSnap = new NativeArray<MarkingAreaVertex>(areaVertCount, Allocator.Temp);
            for (int i = 0; i < areaVertCount; i++) areaVertsSnap[i] = areaVerts[i];

            // Resolve current lane endpoints / corner anchors for vertex position lookup. Same
            // call MarkingAreaEmissionSystem makes — cheap (only on hash mismatch).
            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            var corners = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);

            // Snapshot the lines for intersection-vertex resolve (kind 2) — same rationale as
            // the area/vertex snapshots above: buffer handles die at the structural changes below.
            var linesSnap = new MarkingLine[hasLines ? lines.Length : 0];
            for (int i = 0; i < linesSnap.Length; i++) linesSnap[i] = lines[i];

            // Capture old pieces so visibility can be inherited.
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

            // v1→v2 vertex migration (phase 8b): legacy vertices identify kind-0/1 anchors by
            // raw list index, but extraction order is NOT deterministic across save loads (the
            // game rebuilds lanes) — the index may point at a different dot today. The saved
            // piece rings ARE trustworthy world-space geometry, so: resolve legacy vertices by
            // index, accept only when the result lies ON a saved ring of that area, and stamp
            // the stable v2 identity (edge/gap keys). Areas with any unconfirmed vertex keep
            // their carried pieces this pass — correct visuals from the save, retried on the
            // next recompute.
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
                            if ((t.kind == 0 || t.kind == 1) && t.refEdgeA == Entity.Null) { hasLegacy = true; break; }
                        }
                    }
                    if (!hasLegacy) continue;
                    if (oldPiecesByArea[a].Count == 0) continue; // no saved ring — legacy resolve as before
                    var oldRing = oldPiecesByArea[a][0].ring;    // one piece per area since 7e
                    if (oldRing.Count < 3) continue;

                    // Phase 1: resolve every vertex of the area, mapping each onto the saved
                    // ring. Legacy vertices must land ON the ring (anchors are always emitted
                    // as ring points); the whole sequence must then walk the ring in cyclic
                    // order — a legacy index that grabbed ANOTHER anchor of the same area also
                    // lies on the ring, but breaks the order (bowtie), so order is the guard.
                    pending.Clear();
                    ringIdxSeq.Clear();
                    bool confirmed = true;
                    for (int v = 0; v < ad.vertexCount && confirmed; v++)
                    {
                        int idx = ad.firstVertex + v;
                        if (idx < 0 || idx >= vertsRW.Length) { confirmed = false; break; }
                        var av = vertsRW[idx];
                        bool isLegacy = (av.kind == 0 || av.kind == 1) && av.refEdgeA == Entity.Null;
                        float3 pos;
                        if (isLegacy)
                        {
                            Entity edgeA, edgeB = Entity.Null;
                            int gap = 0;
                            if (av.kind == 0 && av.refIndex >= 0 && av.refIndex < endpoints.Count)
                            {
                                var ep = endpoints[av.refIndex];
                                pos = ep.position; edgeA = ep.edge; gap = ep.gapIndex;
                            }
                            else if (av.kind == 1 && av.refIndex >= 0 && av.refIndex < corners.Count)
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
                    // Phase 2: cyclic-order check, then stamp all-or-nothing.
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

            // Build the new flat piece list — ONE piece per area. Auto-cutting areas by
            // crossing lines is gone (phase 7e): the split rings went through an index-paired
            // tip fattener that mangled them into fold-over spikes (invisible fills), the
            // recomputes made existing areas "jump" whenever a line was drawn, and the user
            // explicitly prefers areas to be immutable once drawn — a line through an area is
            // now simply cosmetic overlap. The piece layer itself stays (saves compatibility,
            // per-piece visibility maps 1:1 onto the area).
            var newPieces = new List<MarkingAreaPiece>(areaCount);
            var newVerts = new List<MarkingAreaPieceVertex>(areaCount * 8);

            for (int a = 0; a < areaCount; a++)
            {
                var ad = areasSnap[a];
                // Note: hidden areas (ad.visible == false) still get their pieces computed —
                // emission filters on area visibility, and keeping the pieces means per-piece
                // visibility survives an area hide→show cycle instead of resetting to default.
                if (ad.vertexCount < 3) continue;

                // The ring builder must never take the whole system down: an exception escaping
                // OnUpdate would re-fire every tick (log flood, all nodes after this one frozen).
                // Treat a throwing ring like an unresolvable one — the carried-pieces path below
                // keeps the cached geometry and the hash write stops the retry loop.
                // Unconfirmed legacy areas (v1 vertices whose index resolve contradicts the
                // saved ring) take the same path deliberately.
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
                    // Ring permanently unresolvable (a referenced anchor disappeared after a
                    // road change — the transient load case is deferred above). Carry the old
                    // pieces over verbatim instead of dropping them: cached geometry is the best
                    // truth we have and the user's per-piece visibility must survive.
                    var carried = oldPiecesByArea[a];
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

                if (math.abs(SignedAreaXZ(outerRing)) < kMinPieceAreaM2) continue;

                float3 c = PolygonSplitter.CentroidXZ(outerRing);
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

            // Write back. AddBuffer overwrites if present.
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

            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);

            // Invalidate every spawned fill of this node. The emission diff matches purely by
            // (node, areaIndex, pieceIndex) + prefab — a surviving key match keeps its STALE
            // geometry (7e bug: deleting area #1 left its dead fill alive as the new "area #1"
            // and the LAST area's fill vanished instead). Pieces just changed, so mark them all
            // Deleted here; emission respawns everything wanted next tick.
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

            log.Info($"area-topology node#{node.Index}: {areaCount} area(s) → {newPieces.Count} piece(s)");
            return true;
        }

        /// <summary>Build the outer ring of one area: the TRUE contour, exactly as drawn.
        ///
        /// Each edge is either a straight chord between its two anchors or the sampled
        /// sub-Bezier of the marking line both anchors lie on — the same sampling the tool's
        /// draft preview uses (<see cref="SampleCurvedEdge"/>), so the committed fill matches
        /// the preview point for point.
        ///
        /// History (phase 7d → 8): this used to be a min-width ENVELOPE builder — corner
        /// strips, flat caps, tiny-edge merging — because vanilla triangulation folded any
        /// ring thinner than ~0.3 m and silently cleared its triangles. Since phase 8,
        /// <see cref="MarkingAreaTriangulationSystem"/> re-triangulates our fills after the
        /// vanilla pass (no shrink, no attempt budget), so knife-tip corners and sub-metre
        /// islands triangulate fine and the envelope machinery is gone.
        ///
        /// Returns null when a vertex fails to resolve (line removed, road demolished — the
        /// area gets cleaned up via the carried-pieces path).</summary>
        private static List<float3> ResolveOuterRing(MarkingArea ad, NativeArray<MarkingAreaVertex> verts,
                                                     List<MarkingEndpoint> endpoints, List<MarkingCornerAnchor> corners,
                                                     MarkingLine[] lines)
        {
            int n = ad.vertexCount;
            if (n < 3) return null;

            // 1. Anchor positions.
            var anchors = new float3[n];
            var avs = new MarkingAreaVertex[n];
            for (int v = 0; v < n; v++)
            {
                int idx = ad.firstVertex + v;
                if (idx < 0 || idx >= verts.Length) return null;
                avs[v] = verts[idx];
                if (!ResolveVertexPos(avs[v], endpoints, corners, lines, out anchors[v])) return null;
            }

            // 2. Emit anchor, then the edge's interior curve points (DP-simplified — straight
            // stretches contribute nothing).
            var ring = new List<float3>(n * 4);
            for (int v = 0; v < n; v++)
            {
                ring.Add(anchors[v]);
                if (avs[v].edgeToNext == 1 // AreaEdgeKind.LineBezier
                    && TryFindSharedLine(avs[v], avs[(v + 1) % n], endpoints, lines, out _, out var bez, out float tFrom, out float tTo))
                {
                    SampleCurvedEdge(bez, tFrom, tTo, ring);
                }
            }

            // Collapse near-duplicate consecutive points (coincident anchors, sub-5cm edges).
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
                if (math.abs(tTo - tFrom) < 1e-4f) continue; // degenerate span
                if (!MarkingCurveBuilder.TryBuild(endpoints, lines[i], out bez)) continue;
                lineIndex = i;
                return true;
            }
            return false;
        }

        /// <summary>Index of the ring point closest to <paramref name="pos"/> (XZ); returns the
        /// squared distance. Used by the v1→v2 migration to anchor its on-ring and
        /// cyclic-order validation.</summary>
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

        private static float DistSqXZ(float3 p, float3 q)
        {
            float dx = p.x - q.x;
            float dz = p.z - q.z;
            return dx * dx + dz * dz;
        }

        // Pieces smaller than this are dropped outright (see the cut loop).
        private const float kMinPieceAreaM2 = 0.5f;

        private static float SignedAreaXZ(List<float3> ring)
        {
            float sum = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                sum += p.x * q.z - q.x * p.z;
            }
            return sum * 0.5f;
        }

        private static bool ResolveVertexPos(MarkingAreaVertex av, List<MarkingEndpoint> endpoints,
                                             List<MarkingCornerAnchor> corners, MarkingLine[] lines, out float3 pos)
        {
            pos = default;
            if (av.kind == 0)
            {
                int idx = MarkingEndpointExtractor.ResolveEndpointIndex(endpoints, av);
                if (idx < 0) return false;
                pos = endpoints[idx].position;
                return true;
            }
            if (av.kind == 1)
            {
                int idx = MarkingEndpointExtractor.ResolveCornerIndex(corners, av);
                if (idx < 0) return false;
                pos = corners[idx].position;
                return true;
            }
            if (av.kind == 2) // line crossing — refIndex is the packed (lineA, lineB, hit)
                return MarkingIntersectionExtractor.TryResolve(endpoints, lines, av.refIndex, out pos);
            return false;
        }

        // Free-zone / preview sampling: fine-sample the curve, then Douglas-Peucker so a node
        // exists only where the geometry actually bends — a node on a straight stretch is pure
        // liability for the vanilla triangulation (see ResolveOuterRing).
        private const float kFineSampleSpacingM = 0.75f;
        private const int kFineSampleMax = 48;
        private const float kSimplifyTolM = 0.06f;

        /// <summary>Interior polyline points of a curved edge (t=tFrom → t=tTo along the
        /// Bezier; the endpoints themselves are NOT appended). Shared by the tool's draft
        /// preview and the committed ring (<see cref="ResolveOuterRing"/>) — what the preview
        /// shows is exactly what gets filled.</summary>
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

        /// <summary>Douglas-Peucker over pts[first..last] in XZ: mark interior points deviating
        /// from the chord by more than tol as kept. Endpoints are the callers' anchors.</summary>
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
            if (av.kind == 0)
            {
                int epIdx = MarkingEndpointExtractor.ResolveEndpointIndex(endpoints, av);
                if (epIdx < 0) return false;
                var ep = endpoints[epIdx];
                var ln = lines[lineIndex];
                if (ln.sourceEdge == ep.edge && ln.sourceGapIndex == ep.gapIndex) { t = 0f; return true; }
                if (ln.targetEdge == ep.edge && ln.targetGapIndex == ep.gapIndex) { t = 1f; return true; }
                return false;
            }
            if (av.kind == 2)
            {
                MarkingIntersectionExtractor.Unpack(av.refIndex, out int a, out int b, out _);
                if (lineIndex != a && lineIndex != b) return false;
                if (!MarkingIntersectionExtractor.TryResolveAnchor(endpoints, lines, av.refIndex, out var anchor)) return false;
                t = lineIndex == a ? anchor.tA : anchor.tB;
                return true;
            }
            return false;
        }

        /// <summary>Inheritance: new piece is "the same as" old piece P if P contains the new
        /// piece's centroid. Returns the visibility of the first such P, or default if none
        /// match (= truly new piece, e.g. after a new line was added).</summary>
        private static bool LookupInheritedVisibility(List<(MarkingAreaPiece header, List<float3> ring)> oldPieces,
                                                     float3 newCentroid, bool defaultVisible)
        {
            for (int i = 0; i < oldPieces.Count; i++)
            {
                if (PolygonSplitter.ContainsXZ(oldPieces[i].ring, newCentroid))
                    return oldPieces[i].header.visible;
            }
            return defaultVisible;
        }

        private static int HashAreaAndLines(DynamicBuffer<MarkingArea> areas, DynamicBuffer<MarkingAreaVertex> verts, DynamicBuffer<MarkingLine>? lines)
        {
            // FNV-1a 32-bit over: area styleId/visible/vertex-slice for every area, then each
            // vertex's (kind, refIndex, edgeToNext), then each line's geometry identity. Same
            // shape as MarkingTopologySystem.HashLines.
            //
            // kAlgoVersion folds the ring-building algorithm into the hash: bump it whenever
            // the SHAPE produced from identical inputs changes (7b: curved-edge sampling), so
            // areas loaded from older saves rebuild once instead of keeping stale chord pieces.
            const uint kAlgoVersion = 11;
            const uint kPrime = 16777619u;
            uint h = 2166136261u ^ kAlgoVersion;
            for (int i = 0; i < areas.Length; i++)
            {
                var a = areas[i];
                h = (h ^ (uint)a.styleId) * kPrime;
                h = (h ^ (uint)(a.visible ? 1 : 0)) * kPrime;
                h = (h ^ (uint)a.firstVertex) * kPrime;
                h = (h ^ (uint)a.vertexCount) * kPrime;
            }
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                h = (h ^ (uint)v.kind) * kPrime;
                h = (h ^ (uint)v.refIndex) * kPrime;
                h = (h ^ (uint)v.edgeToNext) * kPrime;
            }
            if (lines.HasValue)
            {
                var lb = lines.Value;
                for (int i = 0; i < lb.Length; i++)
                {
                    var l = lb[i];
                    h = (h ^ (uint)l.sourceEdge.Index) * kPrime;
                    h = (h ^ (uint)l.sourceGapIndex) * kPrime;
                    h = (h ^ (uint)l.targetEdge.Index) * kPrime;
                    h = (h ^ (uint)l.targetGapIndex) * kPrime;
                    // Curvature moves the cut polyline — pieces must be recomputed.
                    h = (h ^ math.asuint(l.curvature)) * kPrime;
                }
            }
            return (int)h;
        }
    }
}
