using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// One attach point sitting BETWEEN two adjacent carriageway lanes (or on an outer kerb)
    /// at a road edge's node end. For an edge with N touching car-driveable lanes at the node,
    /// the extractor produces N+1 endpoints — one per lane-to-lane stitch + two outer kerbs.
    /// Lanes separated by a median (divider wider than kCarriagewayGapM) contribute two
    /// endpoints at that boundary instead of one — one per carriageway edge.
    ///
    /// gapIndex is a left-to-right running counter over the emitted endpoints (0 = outer-left
    /// kerb, max = outer-right kerb). It is an opaque identity — stable for a given composition,
    /// but renumbered if the road's lane layout changes. Two extra ranges never renumber the
    /// classic set: parking-bay edges (the true kerb line on roads with parking) start at
    /// kExtendedGapBase; setback anchors (the classic row repeated kSetbackDistanceM back along
    /// the road) start at kSetbackGapBase and mirror the classic gap numbers.
    /// </summary>
    public struct MarkingEndpoint
    {
        public Entity edge;        // road edge this endpoint sits on
        public int    gapIndex;    // 0..N (where N = car-lane count on the edge composition)
        public float3 position;    // world-space point on the node-side cap of the edge
        public float2 tangent;     // normalized horizontal tangent INTO the edge from the node
    }

    /// <summary>
    /// Phase 6a — corner anchor sitting at an intersection corner (where the kerb of one road
    /// meets the kerb of the adjacent road around the node). Derived from outer kerbs (gap=0
    /// and gap=N) of every ConnectedEdge by deduplicating near-coincident kerbs from neighbour
    /// edges into a single shared anchor.
    ///
    /// Use case: polygon area tool needs anchors at intersection corners so users can fill
    /// safety islands / yellow box junctions that touch the road edges. Lane endpoints don't
    /// cover this — they sit on the carriageway, not at the kerb between two roads.
    /// </summary>
    public struct MarkingCornerAnchor
    {
        public float3 position;  // world-space position of the corner
        // The two edges whose kerbs meet here, sorted ascending by Entity.Index. -1 (Entity.Null)
        // for edgeB when the corner is a standalone outer kerb (e.g. a dead-end node) with no
        // neighbour to dedupe against.
        public Entity edgeA;
        public Entity edgeB;
    }

    /// <summary>
    /// Reads endpoints from the edge's PREFAB COMPOSITION (NetCompositionLane buffer on the
    /// composition entity referenced by Composition.m_StartNode / m_EndNode). This is
    /// direction-agnostic — composition lanes describe the static lateral layout of the
    /// carriageway, not the runtime forward/backward sublanes — so a 2-way 2-lane road yields
    /// 3 endpoints (left kerb, centre divider, right kerb) regardless of traffic direction.
    ///
    /// Algorithm:
    ///   For each ConnectedEdge of the node:
    ///     1. Resolve the composition entity for the node-side cap:
    ///          nodeIsStart  → composition.m_StartNode
    ///          nodeIsEnd    → composition.m_EndNode
    ///     2. Read NetCompositionLane[] on that entity, filter to Road-flagged lanes (cars).
    ///     3. Dedupe by lateral position (forward + backward of the same physical lane share
    ///        m_Position.x but differ in Invert flag).
    ///     4. Sort by m_Position.x ascending.
    ///     5. For each pair of adjacent sorted lanes + the two outer kerbs, emit a world-space
    ///        point by interpolating along EdgeGeometry's m_Start (or m_End) right→left segment
    ///        at parameter t = (lateralX + width/2) / width, then offset by the lane's half-width
    ///        toward the stitch.
    /// </summary>
    public static class MarkingEndpointExtractor
    {
        public static List<MarkingEndpoint> Extract(EntityManager em, Entity node)
        {
            return Extract(em, node, log: false);
        }

        // Stable-identity matches must stay LOCAL — a fallback that grabs a dot metres away
        // would silently deform the area instead of failing into the carried-pieces path.
        private const float kAnchorMatchRadiusSq = 1.5f * 1.5f;

        /// <summary>Resolve a saved area vertex (kind 0) to today's endpoint list. v2 vertices
        /// match by stable (edge, gapIndex) identity — list ORDER is not deterministic across
        /// save loads, raw indexes are not trustworthy. Falls back to nearest-by-draw-position
        /// (composition changed), then to the legacy index for v1 saves. Returns -1 when
        /// nothing matches.</summary>
        public static int ResolveEndpointIndex(IReadOnlyList<MarkingEndpoint> endpoints, in MarkingAreaVertex av)
        {
            if (av.refEdgeA != Entity.Null)
            {
                for (int i = 0; i < endpoints.Count; i++)
                    if (endpoints[i].edge == av.refEdgeA && endpoints[i].gapIndex == av.refGap)
                        return i;
                int best = -1;
                float bestSq = kAnchorMatchRadiusSq;
                for (int i = 0; i < endpoints.Count; i++)
                {
                    float dx = endpoints[i].position.x - av.refPos.x;
                    float dz = endpoints[i].position.z - av.refPos.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
                return best;
            }
            return av.refIndex >= 0 && av.refIndex < endpoints.Count ? av.refIndex : -1;
        }

        /// <summary>Same for kind 1 corner anchors: match by the (edgeA, edgeB) pair; when the
        /// pair is ambiguous (standalone kerb corners share edgeA + Null — one per side) or
        /// missing, the nearest match by draw position wins. Legacy index for v1 saves.</summary>
        public static int ResolveCornerIndex(IReadOnlyList<MarkingCornerAnchor> corners, in MarkingAreaVertex av)
        {
            if (av.refEdgeA != Entity.Null)
            {
                int best = -1;
                float bestSq = kAnchorMatchRadiusSq;
                int exact = -1, exactCount = 0;
                for (int i = 0; i < corners.Count; i++)
                {
                    if (corners[i].edgeA != av.refEdgeA || corners[i].edgeB != av.refEdgeB) continue;
                    exact = i;
                    exactCount++;
                    float dx = corners[i].position.x - av.refPos.x;
                    float dz = corners[i].position.z - av.refPos.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
                if (exactCount == 1) return exact;
                if (best >= 0) return best;
                // Pair gone entirely (road demolished/rebuilt) — nearest corner of any pair.
                bestSq = kAnchorMatchRadiusSq;
                for (int i = 0; i < corners.Count; i++)
                {
                    float dx = corners[i].position.x - av.refPos.x;
                    float dz = corners[i].position.z - av.refPos.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
                return best;
            }
            return av.refIndex >= 0 && av.refIndex < corners.Count ? av.refIndex : -1;
        }

        // Two adjacent Road lanes whose inner edges sit further apart than this are treated as
        // separate carriageways (median strip / raised divider / pure-tram reservation between
        // them). Instead of one stitch endpoint at the middle of the divider we emit two — one
        // on each carriageway's inner edge, where the physical marking actually belongs.
        // Touching lanes have gap ≈ 0; the narrowest vanilla medians are well above 1m.
        private const float kCarriagewayGapM = 0.75f;

        // gapIndex base for endpoints derived from non-Road cross-section lanes (parking bays
        // today). The classic car-lane set stays numbered 0..N — inserting new gaps in that
        // range would renumber saved MarkingLine / area anchors, whose identity IS
        // (edge, gapIndex). Extended anchors get 1000+running instead: still stable
        // left-to-right for a given composition, never colliding with the classic range.
        private const int kExtendedGapBase = 1000;

        // Extended endpoints closer than this (laterally) to an already-emitted one are
        // dropped — e.g. the parking lane's inner edge coincides with the outer car-lane kerb.
        private const float kExtendedDedupeM = 0.10f;

        // Setback anchors: a second row of the classic stitches, this far back along the road
        // from the node cap (sampled on the EdgeGeometry curves, so curved approaches keep the
        // row on the carriageway). Use case: the solid pre-junction stretch of a lane divider
        // (no-lane-change zone before the stop line); also gives stretched Node Controller
        // junctions a usable anchor row behind the deformed cap. gapIndex = kSetbackGapBase +
        // classic gap — a range of its own, like the parking extension.
        private const int   kSetbackGapBase   = 2000;
        private const float kSetbackDistanceM = 8f;

        /// <summary>
        /// True when the edge's derived net state is filled in and endpoint extraction would see
        /// real data. False right after loading a save: <c>Composition</c> and
        /// <c>EdgeGeometry</c> are <c>IEmptySerializable</c> — they deserialize zeroed and are
        /// only refilled by CompositionSelectSystem (Modification3) / GeometrySystem
        /// (Modification4), i.e. AFTER our Modification1 systems have already run once.
        ///
        /// Any system that rewrites persistent buffers from extracted endpoints MUST defer while
        /// a referenced edge is alive but not ready, or it destroys user data by recomputing
        /// from a zero-endpoint state (the save/load style-loss bug).
        /// </summary>
        public static bool IsEdgeExtractionReady(EntityManager em, Entity edge)
        {
            if (edge == Entity.Null || !em.Exists(edge)) return false;
            if (em.HasComponent<Deleted>(edge)) return false;
            if (!em.HasComponent<Edge>(edge)) return false;
            if (!em.HasComponent<Composition>(edge)) return false;
            var comp = em.GetComponentData<Composition>(edge);
            if (comp.m_Edge == Entity.Null || !em.HasBuffer<NetCompositionLane>(comp.m_Edge)) return false;
            if (!em.HasComponent<EdgeGeometry>(edge)) return false;
            // Zeroed EdgeGeometry (pre-Modification4 on the load tick) has both caps collapsed
            // to the origin — left and right kerb coincide. Real roads always have width > 0.
            var geom = em.GetComponentData<EdgeGeometry>(edge);
            if (math.lengthsq(geom.m_Start.m_Right.a - geom.m_Start.m_Left.a) < 1e-6f
                && math.lengthsq(geom.m_End.m_Right.d - geom.m_End.m_Left.d) < 1e-6f) return false;
            return true;
        }

        /// <summary>True when the edge still exists as a road (not demolished) but
        /// <see cref="IsEdgeExtractionReady"/> is false — the transient window during save
        /// loading. Callers should retry next tick instead of recomputing.</summary>
        public static bool IsEdgeAliveButUnready(EntityManager em, Entity edge)
        {
            if (edge == Entity.Null || !em.Exists(edge)) return false;
            if (em.HasComponent<Deleted>(edge)) return false;
            if (!em.HasComponent<Edge>(edge)) return false;
            return !IsEdgeExtractionReady(em, edge);
        }

        // Maximum world-space distance for two raw curb points of different edges to be merged
        // into a single shared corner. Bumped to 3m to cover roads with wide shoulders /
        // generous fillet radii — still well under the typical inter-corner distance of an X
        // junction (lanes are 3.5m and a corner pair sits at least one road-width apart), so
        // we won't accidentally merge two distinct corners of the same intersection.
        private const float kCornerDedupRadiusM = 3.0f;

        // Pull merged corner points toward the node centre by this fraction of (node→raw-corner)
        // distance. EdgeGeometry curb points sit at the sharp mathematical kerb-line intersection
        // — that's slightly outside the actual rounded fillet visible on the road. Pulling 35%
        // toward the node lands the anchor approximately on the fillet curve for typical road
        // widths (4-12m fillet radii produce 6-14m sharp-point distance from node centre, so
        // 35% pull = 2-5m inward, comfortably on the fillet). Visually preferable for safety
        // islands + pedestrian zones where the user expects the dot to sit ON the curb, not
        // floating outside it. For yellow-box-junction "вафля" this trims the polygon a bit
        // inside the intersection, which actually matches Russian PDD spec (вафля рисуется не
        // вплотную к краям).
        private const float kCornerInwardPullFraction = 0.35f;

        /// <summary>
        /// Phase 6a: returns the unique corner points around a node — one per "where the kerb of
        /// edge A meets the kerb of edge B".
        ///
        /// Important: these are TRUE curb corners (boundary between road surface and footpath /
        /// grass / building plot), NOT the edge of the outermost car-driveable lane. They live
        /// directly on <c>EdgeGeometry.m_Start.m_Left.a</c> / <c>.m_Right.a</c> (or .d for the
        /// end-cap), which CS2 builds to include parking lanes, sidewalks, shoulders — anything
        /// the road prefab declares as part of its cross-section. Going through
        /// NetCompositionLane (like <see cref="Extract"/> does) would only give car-lane kerbs,
        /// which sit several metres inside the real curb on roads with parking or wide footpaths.
        ///
        /// For each ConnectedEdge we take 2 points (left-at-node + right-at-node) and then
        /// dedupe near-coincident pairs from neighbour edges within <see cref="kCornerDedupRadiusM"/>.
        /// A node with K connected edges yields K corners on a well-formed junction (T=3, X=4).
        /// Dead-end nodes (1 edge) yield 2 isolated corners with edgeB=Entity.Null.
        /// </summary>
        public static List<MarkingCornerAnchor> ExtractCornerAnchors(EntityManager em, Entity node)
        {
            var corners = new List<MarkingCornerAnchor>(8);
            if (node == Entity.Null) return corners;
            if (!em.HasBuffer<ConnectedEdge>(node)) return corners;
            if (!em.HasComponent<Node>(node)) return corners;

            float3 nodeCentre = em.GetComponentData<Node>(node).m_Position;
            var connected = em.GetBuffer<ConnectedEdge>(node, isReadOnly: true);

            // Collect the 2 raw kerb corner points for each connected edge from EdgeGeometry.
            var kerbList = new List<(Entity edge, float3 pos)>(connected.Length * 2);
            for (int i = 0; i < connected.Length; i++)
            {
                var edgeEntity = connected[i].m_Edge;
                if (!em.HasComponent<Edge>(edgeEntity)) continue;
                if (!em.HasComponent<EdgeGeometry>(edgeEntity)) continue;

                var edge = em.GetComponentData<Edge>(edgeEntity);
                bool nodeIsStart = edge.m_Start == node;
                bool nodeIsEnd   = edge.m_End == node;
                if (!nodeIsStart && !nodeIsEnd) continue;

                var geom = em.GetComponentData<EdgeGeometry>(edgeEntity);
                // For the node-side cap take both kerbs. Bezier4x3 .a = start point, .d = end
                // point; m_Start segment heads from .a (at the start node) into the edge, while
                // m_End segment ends at .d (at the end node).
                float3 leftPt, rightPt;
                if (nodeIsStart)
                {
                    leftPt  = geom.m_Start.m_Left.a;
                    rightPt = geom.m_Start.m_Right.a;
                }
                else
                {
                    leftPt  = geom.m_End.m_Left.d;
                    rightPt = geom.m_End.m_Right.d;
                }
                kerbList.Add((edgeEntity, leftPt));
                kerbList.Add((edgeEntity, rightPt));
            }

            // Greedy O(n²) pairwise merge — n is tiny (max ~8 kerbs on a 4-way junction).
            // Each kerb either merges with one already-claimed neighbour or stands alone.
            var consumed = new bool[kerbList.Count];
            float r2 = kCornerDedupRadiusM * kCornerDedupRadiusM;
            for (int i = 0; i < kerbList.Count; i++)
            {
                if (consumed[i]) continue;
                var a = kerbList[i];
                int matchIdx = -1;
                float bestD2 = r2;
                for (int j = i + 1; j < kerbList.Count; j++)
                {
                    if (consumed[j]) continue;
                    var b = kerbList[j];
                    if (b.edge == a.edge) continue;  // can't merge two kerbs of the same edge
                    float d2 = math.lengthsq(a.pos - b.pos);
                    if (d2 < bestD2) { bestD2 = d2; matchIdx = j; }
                }

                float3 rawPos;
                Entity eA, eB;
                if (matchIdx >= 0)
                {
                    var b = kerbList[matchIdx];
                    consumed[matchIdx] = true;
                    rawPos = (a.pos + b.pos) * 0.5f;
                    // Sort edges by Index so identical corners hash the same regardless of edge
                    // iteration order — useful when downstream code wants to dedupe across ticks.
                    eA = a.edge; eB = b.edge;
                    if (eA.Index > eB.Index) (eA, eB) = (eB, eA);
                }
                else
                {
                    // Lone kerb — keep it (dead-end / unmatched).
                    rawPos = a.pos;
                    eA = a.edge;
                    eB = Entity.Null;
                }

                // Pull inward along (node → raw) by a fraction so the anchor lands on the actual
                // rounded fillet curb rather than the sharp mathematical outer-intersection point.
                // Y stays at the raw point's height — pulling Y toward node centre would dip below
                // the road if node sits on a slope summit.
                float3 inward = rawPos - nodeCentre;
                float3 pulledPos = rawPos - inward * kCornerInwardPullFraction;
                pulledPos.y = rawPos.y;
                corners.Add(new MarkingCornerAnchor
                {
                    position = pulledPos,
                    edgeA = eA,
                    edgeB = eB,
                });
            }

            return corners;
        }

        public static List<MarkingEndpoint> Extract(EntityManager em, Entity node, bool log)
        {
            var results = new List<MarkingEndpoint>(16);
            if (node == Entity.Null) return results;
            if (!em.HasBuffer<ConnectedEdge>(node)) return results;

            var connected = em.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
            if (log) Mod.log.Info($"extractor: node #{node.Index} has {connected.Length} ConnectedEdge(s)");
            for (int e = 0; e < connected.Length; e++)
            {
                ExtractForEdge(em, node, connected[e].m_Edge, results, log);
            }
            if (log) Mod.log.Info($"extractor: total endpoints = {results.Count}");
            return results;
        }

        private static void ExtractForEdge(EntityManager em, Entity node, Entity edgeEntity, List<MarkingEndpoint> outList, bool log)
        {
            if (!em.HasComponent<Edge>(edgeEntity)) return;
            if (!em.HasComponent<Composition>(edgeEntity)) return;
            if (!em.HasComponent<EdgeGeometry>(edgeEntity)) return;

            var edge = em.GetComponentData<Edge>(edgeEntity);
            bool nodeIsStart = edge.m_Start == node;
            bool nodeIsEnd   = edge.m_End == node;
            if (!nodeIsStart && !nodeIsEnd) return;

            var composition = em.GetComponentData<Composition>(edgeEntity);
            // Composition has three prefab refs: m_Edge (edge cross-section composition; carries
            // NetCompositionLane), m_StartNode + m_EndNode (per-cap node compositions; just
            // height / shoulder data, NO lane buffer). Traffic uses m_Edge — see
            // GenerateConnectorsSystem.GenerateConnectorsJob.cs:100. We initially read from
            // m_StartNode / m_EndNode and got empty buffers (38632-38635 log).
            Entity compEntity = composition.m_Edge;
            if (compEntity == Entity.Null) return;
            if (!em.HasBuffer<NetCompositionLane>(compEntity)) { if (log) Mod.log.Info($"  edge #{edgeEntity.Index}: composition #{compEntity.Index} has no NetCompositionLane buffer"); return; }
            if (!em.HasComponent<NetCompositionData>(compEntity)) { if (log) Mod.log.Info($"  edge #{edgeEntity.Index}: composition #{compEntity.Index} has no NetCompositionData"); return; }

            var compLanes = em.GetBuffer<NetCompositionLane>(compEntity, isReadOnly: true);
            var compData = em.GetComponentData<NetCompositionData>(compEntity);
            float halfWidth = compData.m_Width * 0.5f;

            // EdgeGeometry.m_Start / m_End each carry a Segment with m_Left + m_Right (Bezier4x3).
            // For each cap, m_Left.a and m_Right.a meet at the node end of the road (cross-section
            // across the carriageway at the node). The "left" side is left of the road's
            // direction of travel; "right" is right. We lerp lateral-fraction along this segment.
            var edgeGeom = em.GetComponentData<EdgeGeometry>(edgeEntity);
            Bezier4x3 capLeftCurve, capRightCurve;
            if (nodeIsStart)
            {
                capLeftCurve  = edgeGeom.m_Start.m_Left;
                capRightCurve = edgeGeom.m_Start.m_Right;
            }
            else
            {
                capLeftCurve  = edgeGeom.m_End.m_Left;
                capRightCurve = edgeGeom.m_End.m_Right;
            }
            // The cross-section line at the node is the chord from left-side-at-node to
            // right-side-at-node. For start cap, that's m_Left.a → m_Right.a. For end cap it's
            // m_Left.d → m_Right.d (Bezier curves are oriented so .d is the far end).
            float3 leftAtNode  = nodeIsStart ? capLeftCurve.a  : capLeftCurve.d;
            float3 rightAtNode = nodeIsStart ? capRightCurve.a : capRightCurve.d;
            // Tangent INTO the edge from the node — used by MarkingOverlaySystem to orient the
            // drag-curve so it leaves the dot perpendicular to the road. At start cap, the
            // curves head a→d into the edge so any of (.b - .a) gives the inward tangent; at
            // end cap, reverse.
            float3 tangentSrc = nodeIsStart
                ? (capRightCurve.b - capRightCurve.a)
                : (capRightCurve.c - capRightCurve.d);
            float2 tIntoEdge = math.normalizesafe(tangentSrc.xz);

            // Collect car-driveable composition lanes (one entry per physical lane after dedupe).
            var lanesByX = new SortedDictionary<float, float>(); // lateral X → half-width of the lane (largest seen)
            int total = compLanes.Length, filtered = 0;
            for (int i = 0; i < compLanes.Length; i++)
            {
                var cl = compLanes[i];
                if (log)
                {
                    float w = em.HasComponent<NetLaneData>(cl.m_Lane) ? em.GetComponentData<NetLaneData>(cl.m_Lane).m_Width : 0f;
                    Mod.log.Info($"    comp lane[{i}]: x={cl.m_Position.x:F2} w={w:F2} flags={cl.m_Flags}");
                }
                // Only "Road" carriageway lanes — pedestrian / parking / utility / track / secondary skipped.
                if ((cl.m_Flags & LaneFlags.Road) == 0) continue;
                // Drop Secondary (marking) and Utility (power/water).
                if ((cl.m_Flags & (LaneFlags.Secondary | LaneFlags.Utility)) != 0) continue;
                // Master lane is the virtual "container" for a slave group — skip, slaves give us
                // the real per-lane positions.
                if ((cl.m_Flags & LaneFlags.Master) != 0) continue;

                filtered++;
                float x = cl.m_Position.x;
                // Pull the lane's width from its referenced NetLanePrefab so we know where the
                // stitch between two adjacent lanes sits.
                float laneHalfWidth = 0f;
                if (em.HasComponent<NetLaneData>(cl.m_Lane))
                {
                    var nld = em.GetComponentData<NetLaneData>(cl.m_Lane);
                    laneHalfWidth = nld.m_Width * 0.5f;
                }
                // Dedupe by lateral X: forward + backward of the same physical lane have the same
                // x. Keep the largest half-width if they differ at all (edge case).
                if (lanesByX.TryGetValue(x, out float existing))
                    lanesByX[x] = math.max(existing, laneHalfWidth);
                else
                    lanesByX[x] = laneHalfWidth;
            }

            if (log) Mod.log.Info($"  edge #{edgeEntity.Index} (nodeIsStart={nodeIsStart}, width={compData.m_Width:F2}): {total} composition lanes, {filtered} are Road, {lanesByX.Count} unique lateral positions");

            if (lanesByX.Count == 0) return;

            // Build the ordered (x, halfWidth) list and emit N+1 endpoints. Every emitted
            // lateral X is recorded so the extended (parking) pass below can dedupe against
            // the classic set.
            var sorted = new List<(float x, float hw)>(lanesByX.Count);
            foreach (var kv in lanesByX) sorted.Add((kv.Key, kv.Value));
            var emittedX = new List<float>(sorted.Count + 3);

            // gap 0: left kerb. Lateral at first-lane's left edge = first.x - first.hw.
            float xLeftKerb = sorted[0].x - sorted[0].hw;
            int gap = 0;
            outList.Add(MakeEndpoint(edgeEntity, gap++, xLeftKerb, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
            emittedX.Add(xLeftKerb);

            // Inner stitches. Touching lanes share one stitch at their common edge; lanes
            // separated by a median (divider strip, tram reservation — anything wider than
            // kCarriagewayGapM) get one endpoint per carriageway edge instead, so the user
            // draws from the edge of the actual lane rather than the middle of the divider.
            for (int i = 1; i < sorted.Count; i++)
            {
                float prevRightEdge = sorted[i - 1].x + sorted[i - 1].hw;
                float nextLeftEdge  = sorted[i].x - sorted[i].hw;
                if (nextLeftEdge - prevRightEdge > kCarriagewayGapM)
                {
                    outList.Add(MakeEndpoint(edgeEntity, gap++, prevRightEdge, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
                    outList.Add(MakeEndpoint(edgeEntity, gap++, nextLeftEdge, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
                    emittedX.Add(prevRightEdge);
                    emittedX.Add(nextLeftEdge);
                }
                else
                {
                    // Midpoint of the shared EDGE, not of the lane centres — for equal-width
                    // lanes they coincide, but a bike lane (1.5 m) next to a car lane (3 m)
                    // would otherwise pull the dot 0.375 m off the paint into the wider lane.
                    float xMid = (prevRightEdge + nextLeftEdge) * 0.5f;
                    outList.Add(MakeEndpoint(edgeEntity, gap++, xMid, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
                    emittedX.Add(xMid);
                }
            }

            // Last gap: right kerb.
            int last = sorted.Count - 1;
            float xRightKerb = sorted[last].x + sorted[last].hw;
            outList.Add(MakeEndpoint(edgeEntity, gap, xRightKerb, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
            emittedX.Add(xRightKerb);

            // emittedX[i] ↔ classic gap i from here on; the passes below append more entries.
            int classicCount = emittedX.Count;

            // Setback anchors — mirror every classic stitch kSetbackDistanceM back along the
            // road, on the cross-section slice of the EdgeGeometry curves at that arc length.
            // Skipped when the cap-side geometry segment (≈ half the edge) is shorter than the
            // setback distance: a row past the edge midpoint would collide with the row coming
            // from the opposite node.
            if (TryFindParamAtDistance(capLeftCurve, capRightCurve, nodeIsStart, kSetbackDistanceM, out float tSet))
            {
                float3 setLeft   = MathUtils.Position(capLeftCurve, tSet);
                float3 setRight  = MathUtils.Position(capRightCurve, tSet);
                float3 tanLeft   = MathUtils.Tangent(capLeftCurve, tSet);
                float3 tanRight  = MathUtils.Tangent(capRightCurve, tSet);
                for (int i = 0; i < classicCount; i++)
                {
                    float f = math.saturate((emittedX[i] + halfWidth) / math.max(0.001f, halfWidth * 2f));
                    float3 pos  = math.lerp(setLeft, setRight, f);
                    float3 tan3 = math.lerp(tanLeft, tanRight, f);
                    // Keep the "tangent points INTO the edge, away from the node" convention:
                    // start-cap curves head away from the node as t grows; end-cap curves head
                    // toward it, so flip.
                    if (!nodeIsStart) tan3 = -tan3;
                    outList.Add(new MarkingEndpoint
                    {
                        edge     = edgeEntity,
                        gapIndex = kSetbackGapBase + i,
                        position = pos,
                        tangent  = math.normalizesafe(tan3.xz),
                    });
                }
            }

            // Extended anchors: parking-bay edges. The classic set above covers only Road
            // lanes, so on roads with parking the outer endpoints sit at the drive|parking
            // boundary — metres inside the actual kerb. Emit extra endpoints at the parking
            // lanes' lateral edges (the true carriageway edge) so lines and areas can start
            // at the kerb line. Pedestrian / utility lanes stay excluded — no dots on the
            // sidewalk. gapIndex numbering starts at kExtendedGapBase and always advances
            // (even across deduped entries) so a marginal composition change can't renumber
            // the surviving anchors.
            var extendedEdges = new SortedSet<float>();
            for (int i = 0; i < compLanes.Length; i++)
            {
                var cl = compLanes[i];
                if ((cl.m_Flags & LaneFlags.Parking) == 0) continue;
                if ((cl.m_Flags & (LaneFlags.Secondary | LaneFlags.Utility | LaneFlags.Master)) != 0) continue;
                float hw = 0f;
                if (em.HasComponent<NetLaneData>(cl.m_Lane))
                    hw = em.GetComponentData<NetLaneData>(cl.m_Lane).m_Width * 0.5f;
                if (hw <= 0f) continue;
                extendedEdges.Add(cl.m_Position.x - hw);
                extendedEdges.Add(cl.m_Position.x + hw);
            }
            int extGap = kExtendedGapBase;
            foreach (float x in extendedEdges)
            {
                bool duplicate = false;
                for (int i = 0; i < emittedX.Count; i++)
                {
                    if (math.abs(emittedX[i] - x) < kExtendedDedupeM) { duplicate = true; break; }
                }
                int g = extGap++;
                if (duplicate) continue;
                outList.Add(MakeEndpoint(edgeEntity, g, x, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
                emittedX.Add(x);
            }
        }

        /// <summary>Lerp from the left-cap point to the right-cap point at lateral-fraction t,
        /// where t = (lateralX + halfWidth) / fullWidth ∈ [0, 1]. Both points are the
        /// node-side ends of the EdgeGeometry left/right curves.</summary>
        private static MarkingEndpoint MakeEndpoint(Entity edge, int gapIndex, float lateralX, float halfWidth, float3 leftAtNode, float3 rightAtNode, float2 tangent)
        {
            float t = math.saturate((lateralX + halfWidth) / math.max(0.001f, halfWidth * 2f));
            float3 pos = math.lerp(leftAtNode, rightAtNode, t);
            return new MarkingEndpoint { edge = edge, gapIndex = gapIndex, position = pos, tangent = tangent };
        }

        /// <summary>Find the curve parameter t where the centreline of the cap-side geometry
        /// segment reaches <paramref name="distance"/> metres of arc length from the node.
        /// Walks a fixed sample grid on the average of the left/right curves; horizontal (XZ)
        /// distance only, so steep approaches don't shorten the visible setback. Returns false
        /// when the segment runs out before the distance is reached.</summary>
        private static bool TryFindParamAtDistance(in Bezier4x3 left, in Bezier4x3 right, bool fromStart, float distance, out float t)
        {
            const int kSamples = 24;
            t = 0f;
            float acc = 0f;
            float prevT = fromStart ? 0f : 1f;
            float3 prev = (MathUtils.Position(left, prevT) + MathUtils.Position(right, prevT)) * 0.5f;
            for (int i = 1; i <= kSamples; i++)
            {
                float raw = i / (float)kSamples;
                float tt = fromStart ? raw : 1f - raw;
                float3 cur = (MathUtils.Position(left, tt) + MathUtils.Position(right, tt)) * 0.5f;
                float step = math.distance(prev.xz, cur.xz);
                if (acc + step >= distance)
                {
                    float frac = step > 1e-6f ? (distance - acc) / step : 0f;
                    t = math.lerp(prevT, tt, frac);
                    return true;
                }
                acc += step;
                prev = cur;
                prevT = tt;
            }
            return false;
        }
    }
}
