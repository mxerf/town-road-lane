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
    /// outer ring with the corner-aware builder (curved edges, min-width envelope at sharp
    /// corners — see <see cref="ResolveOuterRing"/>) and stores it as ONE piece per area.
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
                List<float3> outerRing = null;
                try
                {
                    outerRing = ResolveOuterRing(areasSnap[a], areaVertsSnap, endpoints, corners, linesSnap);
                }
                catch (System.Exception e)
                {
                    log.Warn($"area-topology node#{node.Index} area#{a}: ring builder threw ({e.GetType().Name}: {e.Message}), keeping cached pieces");
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

        /// <summary>Build the outer ring of one area — corner-aware (phase 7d).
        ///
        /// The vanilla area pipeline needs a local width of ≥ kMinTipWidthM everywhere (its
        /// −0.1 m/side pre-triangulation offset folds anything thinner — see kMinTipWidthM).
        /// Islands between marking lines violate that exactly at the corners: two boundary
        /// chains converge at a few degrees and stay too thin for METRES (the thin zone is
        /// w_min / (2·sin(θ/2)) long — angle-dependent, so no fixed clearance can be right).
        ///
        /// So the ring is built as the max-width ENVELOPE of the true region: walking outward
        /// from each anchor along BOTH adjacent chains in matched arc-distance steps, the
        /// boundary point at distance d is m(d) ± max(w(d), w_min)/2 — the exact curve wherever
        /// the region is wide enough, a smooth centreline-offset strip where it pinches, with
        /// a flat w_min cap at the anchor itself. Measuring w(d) directly (instead of an angle
        /// formula) makes tangential "hyperbola" approaches and curvature work for free.
        /// Corners that are wide by the first step keep their true anchor point, uncapped.
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

            // 2. Dense boundary chain per edge v → v+1 (curve samples or plain chord).
            var chainList = new List<EdgeChain>(n);
            var anchorList = new List<float3>(anchors);
            for (int v = 0; v < n; v++)
                chainList.Add(BuildEdgeChain(anchors[v], anchors[(v + 1) % n], avs[v], avs[(v + 1) % n], endpoints, lines));

            // 2b. Merge tiny edges (median-tip case: two anchors on opposite kerb corners sit
            // < 1 m apart). Such an edge is shorter than the corner-walk step — its two corners
            // can't be measured and would emit garbage micro-strips (the 7d hairpin that folds
            // under the game's −0.1 m offset). Collapse it: one corner at the edge midpoint,
            // bridged by the cap; the neighbouring chains stay untouched.
            // Invariant kept throughout: chainList[v] runs anchorList[v] → anchorList[v+1 mod n].
            int guard = n;
            while (chainList.Count > 3 && guard-- > 0)
            {
                int tiny = -1;
                for (int v = 0; v < chainList.Count; v++)
                    if (chainList[v].Length < kTinyEdgeM) { tiny = v; break; }
                if (tiny < 0) break;
                int last = chainList.Count - 1;
                if (tiny < last)
                {
                    var mid = (anchorList[tiny] + anchorList[tiny + 1]) * 0.5f;
                    anchorList[tiny] = mid;
                    anchorList.RemoveAt(tiny + 1);
                    chainList.RemoveAt(tiny);
                }
                else // wrap: the tiny edge runs anchor[last] → anchor[0]
                {
                    var mid = (anchorList[last] + anchorList[0]) * 0.5f;
                    anchorList[0] = mid;
                    anchorList.RemoveAt(last);
                    chainList.RemoveAt(last);
                }
            }
            n = chainList.Count;
            var chains = chainList.ToArray();
            anchors = anchorList.ToArray();

            // 3. Thin-zone extent + cap normal per corner (corner v joins chains[v-1] → chains[v]).
            var zone = new float[n];
            var capDir = new float3[n];
            for (int v = 0; v < n; v++)
                zone[v] = CornerZone(chains[(v - 1 + n) % n], chains[v], out capDir[v]);

            // 4. Emit ring edge by edge: start-corner strip, free true-curve zone, end-corner strip.
            var ring = new List<float3>(n * 6);
            for (int e = 0; e < n; e++)
            {
                var cur = chains[e];
                int c0 = e;
                int c1 = (e + 1) % n;
                float freeStart = zone[c0];
                float freeEnd = cur.Length - zone[c1];

                EmitCornerStrip(ring, chains[(e - 1 + n) % n], cur, anchors[c0], capDir[c0], zone[c0], side: -1, rising: true);
                if (freeEnd - freeStart > kCornerWalkStepM)
                    EmitFreeZone(ring, cur, freeStart, freeEnd);
                EmitCornerStrip(ring, cur, chains[c1], anchors[c1], capDir[c1], zone[c1], side: +1, rising: false);
            }

            // Collapse near-duplicate consecutive points (strip/free-zone seams).
            for (int i = ring.Count - 1; i > 0; i--)
                if (DistSqXZ(ring[i], ring[i - 1]) < 0.0025f) ring.RemoveAt(i);
            if (ring.Count > 1 && DistSqXZ(ring[0], ring[ring.Count - 1]) < 0.0025f)
                ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        // ── Corner-aware ring machinery (7d) ────────────────────────────────

        private const float kCornerWalkStepM = 0.5f;
        private const float kCornerZoneMaxM = 12f;
        private const float kStripSpacingM = 1.5f;
        // Edges shorter than this merge into a single corner (see step 2b) — too short for
        // the corner walk to measure, and the flat cap spans them anyway.
        private const float kTinyEdgeM = 1.0f;

        private struct EdgeChain
        {
            public List<float3> pts;  // dense boundary polyline anchor→anchor, endpoints included
            public List<float> cum;   // cumulative XZ arc length; cum[0] = 0
            public int boundaryLine;  // lineIndex this edge runs along, -1 = straight chord
            public float Length;
        }

        private static EdgeChain BuildEdgeChain(float3 from, float3 to, MarkingAreaVertex fromVert, MarkingAreaVertex toVert,
                                                List<MarkingEndpoint> endpoints, MarkingLine[] lines)
        {
            var pts = new List<float3> { from };
            int boundaryLine = -1;
            if (fromVert.edgeToNext == 1 // AreaEdgeKind.LineBezier
                && TryFindSharedLine(fromVert, toVert, endpoints, lines, out boundaryLine, out var bez, out float tFrom, out float tTo))
            {
                float chord = math.sqrt(DistSqXZ(from, to));
                int fine = math.clamp((int)math.ceil(chord / kCornerWalkStepM), 1, 96);
                for (int s = 1; s < fine; s++)
                    pts.Add(MathUtils.Position(bez, math.lerp(tFrom, tTo, s / (float)fine)));
            }
            pts.Add(to);
            var cum = new List<float>(pts.Count) { 0f };
            for (int i = 1; i < pts.Count; i++)
                cum.Add(cum[i - 1] + math.sqrt(DistSqXZ(pts[i - 1], pts[i])));
            return new EdgeChain { pts = pts, cum = cum, boundaryLine = boundaryLine, Length = cum[cum.Count - 1] };
        }

        /// <summary>Point at arc distance <paramref name="dist"/> from the chain's start (or
        /// end), linearly interpolated between the dense samples.</summary>
        private static float3 ChainPoint(EdgeChain c, float dist, bool fromEnd)
        {
            float target = math.clamp(fromEnd ? c.Length - dist : dist, 0f, c.Length);
            for (int i = 1; i < c.pts.Count; i++)
            {
                if (c.cum[i] >= target)
                {
                    float seg = math.max(c.cum[i] - c.cum[i - 1], 1e-6f);
                    return math.lerp(c.pts[i - 1], c.pts[i], (target - c.cum[i - 1]) / seg);
                }
            }
            return c.pts[c.pts.Count - 1];
        }

        /// <summary>How far the corner's thin zone reaches (0 = wide corner, keep the true
        /// anchor). Walks both adjacent chains outward in matched steps measuring the TRUE
        /// width. Also derives the cap normal from the first measurable separation. Edges
        /// shorter than the walk step still get one probe at dMax — a corner walk that never
        /// measures anything must not emit a strip with a made-up normal (7d hairpin bug).</summary>
        private static float CornerZone(EdgeChain prev, EdgeChain next, out float3 capDir)
        {
            capDir = default;
            bool haveDir = false;
            float dMax = math.min(kCornerZoneMaxM, math.min(prev.Length, next.Length) * 0.5f);
            float wminSq = kMinTipWidthM * kMinTipWidthM;
            float dStar = dMax;
            bool first = true;
            for (float d = math.min(kCornerWalkStepM, dMax); d <= dMax + 1e-3f; d += kCornerWalkStepM)
            {
                var a = ChainPoint(prev, d, fromEnd: true);
                var b = ChainPoint(next, d, fromEnd: false);
                float wSq = DistSqXZ(a, b);
                if (!haveDir && wSq > 0.0025f) // 5 cm — first usable normal
                {
                    float w = math.sqrt(wSq);
                    capDir = new float3((a.x - b.x) / w, 0f, (a.z - b.z) / w);
                    haveDir = true;
                }
                if (wSq >= wminSq)
                {
                    dStar = first ? 0f : d; // wide by the first probe → no thin zone at all
                    break;
                }
                first = false;
            }
            if (!haveDir)
            {
                // Chains never separate measurably — degenerate corner; perpendicular of the
                // outgoing chain's start direction keeps the cap orientation sane.
                var b0 = ChainPoint(next, 0f, false);
                var b1 = ChainPoint(next, math.min(1f, next.Length), false);
                float tx = b1.x - b0.x, tz = b1.z - b0.z;
                float tlen = math.max(math.sqrt(tx * tx + tz * tz), 1e-3f);
                capDir = new float3(-tz / tlen, 0f, tx / tlen);
            }
            return dStar;
        }

        /// <summary>Boundary point of the corner envelope at distance d from the anchor:
        /// m(d) ± max(w(d), w_min)/2 — the true chain wherever the region is wide enough, the
        /// fattened strip where it pinches. side +1 = prev-chain side, −1 = next-chain side.</summary>
        private static float3 CornerStripPoint(EdgeChain prev, EdgeChain next, float3 capDir, float d, int side)
        {
            var a = ChainPoint(prev, d, fromEnd: true);
            var b = ChainPoint(next, d, fromEnd: false);
            var m = (a + b) * 0.5f;
            float dx = a.x - b.x, dz = a.z - b.z;
            float len = math.sqrt(dx * dx + dz * dz);
            var nrm = len > 0.05f ? new float3(dx / len, 0f, dz / len) : capDir;
            float half = math.max(kMinTipWidthM, len) * 0.5f;
            return m + nrm * (side * half);
        }

        /// <summary>One side of a corner's thin-zone envelope, in ring order. rising emits
        /// d = 0 → d* (edge start), falling d* → 0 (edge end). The two d = 0 points of adjacent
        /// edges form the flat cap. zone == 0 → wide corner: the true anchor, emitted once.</summary>
        private static void EmitCornerStrip(List<float3> ring, EdgeChain prev, EdgeChain next,
                                            float3 anchor, float3 capDir, float dStar, int side, bool rising)
        {
            if (dStar <= 0f)
            {
                if (rising) ring.Add(anchor); // end-side skips: the next edge's start adds it
                return;
            }
            int steps = math.max(1, (int)math.ceil(dStar / kStripSpacingM));
            for (int s = 0; s <= steps; s++)
            {
                float d = dStar * (rising ? s : steps - s) / steps;
                ring.Add(CornerStripPoint(prev, next, capDir, d, side));
            }
        }

        /// <summary>True-curve stretch of an edge between its two corner zones, Douglas-Peucker
        /// simplified — straight runs contribute nothing.</summary>
        private static void EmitFreeZone(List<float3> ring, EdgeChain c, float from, float to)
        {
            var pts = new List<float3> { ChainPoint(c, from, false) };
            for (int i = 1; i < c.pts.Count - 1; i++)
                if (c.cum[i] > from && c.cum[i] < to) pts.Add(c.pts[i]);
            pts.Add(ChainPoint(c, to, false));
            if (pts.Count < 3)
            {
                ring.Add(pts[0]);
                ring.Add(pts[pts.Count - 1]);
                return;
            }
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            SimplifyDP(pts, 0, pts.Count - 1, kSimplifyTolM, keep);
            for (int i = 0; i < pts.Count; i++)
                if (keep[i]) ring.Add(pts[i]);
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

        // The game's GeometrySystem shrinks every area polygon inward by 0.1 m per side before
        // ear-clipping it — with a hard 2·n attempt budget, after which it CLEARS every
        // triangle and the fill silently vanishes. A knife-tip wedge (the NORMAL corner shape
        // for an island pinched between two crossing marking lines — interior angles of a few
        // degrees) stays thinner than the 0.2 m of total shrink for metres from its tip: the
        // shrunken ring folds over itself there and the clipper burns its budget on the folded
        // tongue. Chord triangles never hit this because a 3-node ring emits its single
        // triangle unchecked.
        //
        // Minimum local width of any emitted ring — the game's GeometrySystem shrinks every
        // polygon by 0.1 m per side before ear-clipping (hard 2·n budget, then it CLEARS all
        // triangles and the fill silently vanishes); anything thinner folds over. Corner zones
        // fatten to this width instead of being cut off: a chopped corner is plainly visible
        // once the covering lines are hidden, while ~10 cm per side hides under the paint.
        private const float kMinTipWidthM = 0.30f;

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
                if (av.refIndex < 0 || av.refIndex >= endpoints.Count) return false;
                pos = endpoints[av.refIndex].position;
                return true;
            }
            if (av.kind == 1)
            {
                if (av.refIndex < 0 || av.refIndex >= corners.Count) return false;
                pos = corners[av.refIndex].position;
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
        /// Bezier; the endpoints themselves are NOT appended). Used by the tool's draft
        /// preview — it shows the TRUE curve; the committed ring additionally fattens
        /// sub-w_min corner zones (ResolveOuterRing), a ≤ 15 cm difference at the tips.</summary>
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
                if (av.refIndex < 0 || av.refIndex >= endpoints.Count) return false;
                var ep = endpoints[av.refIndex];
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
            const uint kAlgoVersion = 9;
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
