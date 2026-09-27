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
    /// An attach point between two adjacent carriageway lanes (or on an outer kerb) at a road
    /// edge's node end. N touching car lanes give N+1 endpoints: one per lane-to-lane stitch
    /// plus the two outer kerbs. Lanes separated by a median (wider than kCarriagewayGapM) give
    /// two endpoints at that boundary, one on each carriageway edge.
    ///
    /// gapIndex counts the emitted endpoints left to right (0 = left kerb). It is stable for a
    /// given composition but renumbers when the road's lane layout changes. Two separate ranges
    /// keep the main set from being renumbered: parking-bay edges (the real kerb line on roads
    /// with parking) start at kExtendedGapBase, and setback anchors (the main row repeated
    /// kSetbackDistanceM back along the road) start at kSetbackGapBase with the same gap numbers.
    /// </summary>
    public struct MarkingEndpoint
    {
        public Entity edge;        // road edge this endpoint sits on
        public int    gapIndex;    // see the summary for the numbering
        public float3 position;    // world-space point on the node-side cap of the edge
        public float2 tangent;     // normalized horizontal tangent into the edge, away from the node
    }

    /// <summary>
    /// An intersection corner, where the kerb of one road meets the kerb of the next road
    /// around the node. The area tool needs these to fill safety islands and yellow box
    /// junctions that touch the kerbs; lane endpoints sit on the carriageway instead.
    /// </summary>
    public struct MarkingCornerAnchor
    {
        public float3 position;  // world-space position of the corner
        // The two edges whose kerbs meet here, sorted by Entity.Index. edgeB is Entity.Null
        // for a standalone kerb (e.g. a dead-end node) with no neighbour to merge with.
        public Entity edgeA;
        public Entity edgeB;
    }

    /// <summary>
    /// Reads endpoints from the edge's prefab composition (the NetCompositionLane buffer on
    /// Composition.m_Edge). Composition lanes describe the static lateral layout, not the
    /// forward/backward runtime sublanes, so a two-way two-lane road yields 3 endpoints (left
    /// kerb, centre, right kerb) regardless of traffic direction.
    ///
    /// Per connected edge: take the Road lanes, merge forward/backward copies of the same
    /// physical lane (same m_Position.x), sort by lateral position, and place each stitch and
    /// kerb by lateral fraction on the chord between the left and right kerb points of the
    /// node-side cap.
    /// </summary>
    public static class MarkingEndpointExtractor
    {
        public static List<MarkingEndpoint> Extract(EntityManager em, Entity node)
        {
            return Extract(em, node, log: false);
        }

        // Keep the position fallback local: grabbing a dot metres away would silently deform
        // the area instead of failing to resolve (the area then keeps its cached pieces).
        private const float kAnchorMatchRadiusSq = 1.5f * 1.5f;

        /// <summary>Resolves a saved endpoint vertex (kind 0) against the current endpoint list.
        /// Matches by (edge, gapIndex), because list order is not deterministic across loads;
        /// then by nearest draw-time position (the composition changed). Older saves without
        /// an edge reference store a raw list index. Returns -1 when nothing matches.</summary>
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

        /// <summary>Same for corner vertices (kind 1): matches by the (edgeA, edgeB) pair. When
        /// the pair is ambiguous (the two standalone kerbs of one edge both have edgeB = Null)
        /// or gone, the nearest draw-time position wins. Older saves use the raw index.</summary>
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
                // Pair gone (road demolished or rebuilt): nearest corner of any pair.
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

        // Adjacent Road lanes whose inner edges are further apart than this are separate
        // carriageways (median, raised divider, tram reservation). They get one endpoint on
        // each carriageway edge, where the marking belongs, instead of one in the middle of the
        // divider. Touching lanes have a gap of about 0; the narrowest vanilla medians are well
        // over 1 m.
        private const float kCarriagewayGapM = 0.75f;

        // gapIndex base for endpoints from non-Road lanes (parking bays). Saved lines and area
        // anchors are identified by (edge, gapIndex), so adding these to the main 0..N range
        // would renumber them.
        private const int kExtendedGapBase = 1000;

        // Extended endpoints laterally closer than this to an emitted one are dropped, e.g. a
        // parking lane's inner edge that coincides with the outer car-lane kerb.
        private const float kExtendedDedupeM = 0.10f;

        // Setback anchors: the main row repeated this far back along the road, sampled on the
        // EdgeGeometry curves so curved approaches keep the row on the carriageway. Used for the
        // solid stretch of a lane divider before the stop line, and as a usable row behind the
        // deformed cap of junctions stretched with Node Controller. gapIndex = kSetbackGapBase +
        // main gap.
        private const int   kSetbackGapBase   = 2000;
        private const float kSetbackDistanceM = 8f;

        /// <summary>
        /// True when the edge's derived net state is filled in and extraction sees real data.
        /// False right after loading a save: <c>Composition</c> and <c>EdgeGeometry</c> are
        /// <c>IEmptySerializable</c>, so they load zeroed and are refilled only by
        /// CompositionSelectSystem (Modification3) and GeometrySystem (Modification4), after the
        /// mod's Modification1 systems have already run once.
        ///
        /// A system that rewrites saved buffers from extracted endpoints has to wait while a
        /// referenced edge is alive but not ready. Recomputing from zero endpoints wipes the
        /// user's lines and styles.
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
            // A zeroed EdgeGeometry (before Modification4 on the load frame) has both caps
            // collapsed to the origin. Real roads always have width > 0.
            var geom = em.GetComponentData<EdgeGeometry>(edge);
            if (math.lengthsq(geom.m_Start.m_Right.a - geom.m_Start.m_Left.a) < 1e-6f
                && math.lengthsq(geom.m_End.m_Right.d - geom.m_End.m_Left.d) < 1e-6f) return false;
            return true;
        }

        /// <summary>True when the edge still exists as a road (not demolished) but
        /// <see cref="IsEdgeExtractionReady"/> is false, the transient state during save loading.
        /// Callers should retry next frame instead of recomputing.</summary>
        public static bool IsEdgeAliveButUnready(EntityManager em, Entity edge)
        {
            if (edge == Entity.Null || !em.Exists(edge)) return false;
            if (em.HasComponent<Deleted>(edge)) return false;
            if (!em.HasComponent<Edge>(edge)) return false;
            return !IsEdgeExtractionReady(em, edge);
        }

        // Two kerb points of different edges closer than this merge into one corner. 3 m covers
        // wide shoulders and large fillet radii, and is still well under the distance between
        // two corners of one junction (at least a road width), so distinct corners never merge.
        private const float kCornerDedupRadiusM = 3.0f;

        // Corners are pulled toward the node centre by this fraction of their distance to it.
        // EdgeGeometry kerb points sit at the sharp intersection of the kerb lines, slightly
        // outside the rounded fillet drawn on the road. Typical fillets put that point 6-14 m
        // from the centre, so 35% moves it 2-5 m inward, onto the fillet, where users expect the
        // dot. For a yellow box junction this also keeps the grid slightly off the kerbs, as
        // Russian road-marking rules require.
        private const float kCornerInwardPullFraction = 0.35f;

        /// <summary>
        /// The unique corners around a node, one per place where the kerb of one edge meets the
        /// kerb of the next.
        ///
        /// These are the real kerbs (road surface against footpath, grass or plot), not the edge
        /// of the outermost car lane. They come straight from the EdgeGeometry cap points, which
        /// include parking lanes, sidewalks and shoulders. NetCompositionLane (as used by
        /// <see cref="Extract"/>) only gives car-lane edges, which sit several metres inside the
        /// real kerb on roads with parking or wide footpaths.
        ///
        /// Each connected edge contributes its left and right cap point; points of neighbouring
        /// edges within <see cref="kCornerDedupRadiusM"/> merge. A well-formed junction with K
        /// edges yields K corners; a dead end yields 2 corners with edgeB = Entity.Null.
        /// </summary>
        public static List<MarkingCornerAnchor> ExtractCornerAnchors(EntityManager em, Entity node)
        {
            var corners = new List<MarkingCornerAnchor>(8);
            if (node == Entity.Null) return corners;
            if (!em.HasBuffer<ConnectedEdge>(node)) return corners;
            if (!em.HasComponent<Node>(node)) return corners;

            float3 nodeCentre = em.GetComponentData<Node>(node).m_Position;
            var connected = em.GetBuffer<ConnectedEdge>(node, isReadOnly: true);

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
                // m_Start begins at the start node (.a); m_End ends at the end node (.d).
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

            // Greedy pairwise merge (a 4-way junction has ~8 kerb points): each point merges with
            // its nearest unclaimed neighbour or stands alone.
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
                    if (b.edge == a.edge) continue;
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
                    // Sorted by Index so the same corner gets the same pair regardless of edge
                    // order; saved corner vertices match on this pair.
                    eA = a.edge; eB = b.edge;
                    if (eA.Index > eB.Index) (eA, eB) = (eB, eA);
                }
                else
                {
                    rawPos = a.pos;
                    eA = a.edge;
                    eB = Entity.Null;
                }

                // Y keeps the kerb point's height: pulling it toward the node centre would sink
                // the anchor below the road when the node sits on a crest.
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
            if (log) Mod.log.Debug($"extractor: node #{node.Index} has {connected.Length} ConnectedEdge(s)");
            for (int e = 0; e < connected.Length; e++)
            {
                ExtractForEdge(em, node, connected[e].m_Edge, results, log);
            }
            if (log) Mod.log.Debug($"extractor: total endpoints = {results.Count}");
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
            // Only m_Edge carries the NetCompositionLane buffer. m_StartNode and m_EndNode are
            // the cap compositions (height and shoulder data) and have no lanes; vanilla traffic
            // code (GenerateConnectorsSystem) reads lanes from m_Edge too.
            Entity compEntity = composition.m_Edge;
            if (compEntity == Entity.Null) return;
            if (!em.HasBuffer<NetCompositionLane>(compEntity)) { if (log) Mod.log.Debug($"  edge #{edgeEntity.Index}: composition #{compEntity.Index} has no NetCompositionLane buffer"); return; }
            if (!em.HasComponent<NetCompositionData>(compEntity)) { if (log) Mod.log.Debug($"  edge #{edgeEntity.Index}: composition #{compEntity.Index} has no NetCompositionData"); return; }

            var compLanes = em.GetBuffer<NetCompositionLane>(compEntity, isReadOnly: true);
            var compData = em.GetComponentData<NetCompositionData>(compEntity);
            float halfWidth = compData.m_Width * 0.5f;

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
            // The cross-section at the node is the chord between the left and right kerb curves:
            // their .a points at the start cap, their .d points at the end cap. Endpoints are
            // placed along it by lateral fraction.
            float3 leftAtNode  = nodeIsStart ? capLeftCurve.a  : capLeftCurve.d;
            float3 rightAtNode = nodeIsStart ? capRightCurve.a : capRightCurve.d;
            // Tangent into the edge, away from the node; curves drawn from the dot are oriented
            // by it. Start-cap curves run a→d into the edge, end-cap curves the other way.
            float3 tangentSrc = nodeIsStart
                ? (capRightCurve.b - capRightCurve.a)
                : (capRightCurve.c - capRightCurve.d);
            float2 tIntoEdge = math.normalizesafe(tangentSrc.xz);

            var lanesByX = new SortedDictionary<float, float>(); // lateral X → lane half-width (largest seen)
            int total = compLanes.Length, filtered = 0;
            for (int i = 0; i < compLanes.Length; i++)
            {
                var cl = compLanes[i];
                if (log)
                {
                    float w = em.HasComponent<NetLaneData>(cl.m_Lane) ? em.GetComponentData<NetLaneData>(cl.m_Lane).m_Width : 0f;
                    Mod.log.Debug($"    comp lane[{i}]: x={cl.m_Position.x:F2} w={w:F2} flags={cl.m_Flags}");
                }
                if ((cl.m_Flags & LaneFlags.Road) == 0) continue;
                // Secondary lanes are markings, Utility lanes are power and water.
                if ((cl.m_Flags & (LaneFlags.Secondary | LaneFlags.Utility)) != 0) continue;
                // A master lane is the virtual container of a slave group; the slaves carry the
                // real per-lane positions.
                if ((cl.m_Flags & LaneFlags.Master) != 0) continue;

                filtered++;
                float x = cl.m_Position.x;
                float laneHalfWidth = 0f;
                if (em.HasComponent<NetLaneData>(cl.m_Lane))
                {
                    var nld = em.GetComponentData<NetLaneData>(cl.m_Lane);
                    laneHalfWidth = nld.m_Width * 0.5f;
                }
                // Forward and backward copies of one physical lane share the same x.
                if (lanesByX.TryGetValue(x, out float existing))
                    lanesByX[x] = math.max(existing, laneHalfWidth);
                else
                    lanesByX[x] = laneHalfWidth;
            }

            if (log) Mod.log.Debug($"  edge #{edgeEntity.Index} (nodeIsStart={nodeIsStart}, width={compData.m_Width:F2}): {total} composition lanes, {filtered} are Road, {lanesByX.Count} unique lateral positions");

            if (lanesByX.Count == 0) return;

            // Every emitted lateral X is recorded so the parking pass below can skip duplicates.
            var sorted = new List<(float x, float hw)>(lanesByX.Count);
            foreach (var kv in lanesByX) sorted.Add((kv.Key, kv.Value));
            var emittedX = new List<float>(sorted.Count + 3);

            float xLeftKerb = sorted[0].x - sorted[0].hw;
            int gap = 0;
            outList.Add(MakeEndpoint(edgeEntity, gap++, xLeftKerb, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
            emittedX.Add(xLeftKerb);

            // Touching lanes share one stitch; lanes separated by a median get one endpoint on
            // each carriageway edge (see kCarriagewayGapM).
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
                    // Midpoint of the shared edge, not of the lane centres: for a bike lane
                    // (1.5 m) next to a car lane (3 m) the centre midpoint would sit 0.375 m off
                    // the paint, inside the wider lane.
                    float xMid = (prevRightEdge + nextLeftEdge) * 0.5f;
                    outList.Add(MakeEndpoint(edgeEntity, gap++, xMid, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
                    emittedX.Add(xMid);
                }
            }

            int last = sorted.Count - 1;
            float xRightKerb = sorted[last].x + sorted[last].hw;
            outList.Add(MakeEndpoint(edgeEntity, gap, xRightKerb, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
            emittedX.Add(xRightKerb);

            // emittedX[i] is main gap i for i < classicCount; the passes below append more.
            int classicCount = emittedX.Count;

            // Setback row. Skipped when the cap-side geometry segment (about half the edge) is
            // shorter than the setback: a row past the edge midpoint would collide with the row
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
                    // The tangent must point away from the node; end-cap curves run toward it.
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

            // Parking-bay edges. The main set covers only Road lanes, so on roads with parking
            // its outer endpoints sit at the driving/parking boundary, metres inside the kerb.
            // Pedestrian and utility lanes stay excluded (no dots on the sidewalk). The gap
            // counter advances even for skipped duplicates, so a small composition change does
            // not renumber the surviving anchors.
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

        /// <summary>Places an endpoint on the cap chord at lateral fraction
        /// (lateralX + halfWidth) / fullWidth.</summary>
        private static MarkingEndpoint MakeEndpoint(Entity edge, int gapIndex, float lateralX, float halfWidth, float3 leftAtNode, float3 rightAtNode, float2 tangent)
        {
            float t = math.saturate((lateralX + halfWidth) / math.max(0.001f, halfWidth * 2f));
            float3 pos = math.lerp(leftAtNode, rightAtNode, t);
            return new MarkingEndpoint { edge = edge, gapIndex = gapIndex, position = pos, tangent = tangent };
        }

        /// <summary>Finds the parameter t where the centreline of the cap-side geometry segment
        /// is <paramref name="distance"/> metres of arc length from the node. Measures XZ
        /// distance only, so steep approaches do not shorten the visible setback. False when the
        /// segment ends first.</summary>
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
