using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// One attach point sitting BETWEEN two adjacent carriageway lanes (or on an outer kerb)
    /// at a road edge's node end. For an edge with N car-driveable lanes at the node, the
    /// extractor produces N+1 endpoints — one per lane-to-lane stitch + two outer kerbs.
    ///
    /// Composition: gapIndex 0 = outer-left kerb, gapIndex N = outer-right kerb, gapIndex i
    /// in between = stitch between lateral-position-sorted lanes (i-1) and (i).
    /// </summary>
    public struct MarkingEndpoint
    {
        public Entity edge;        // road edge this endpoint sits on
        public int    gapIndex;    // 0..N (where N = car-lane count on the edge composition)
        public float3 position;    // world-space point on the node-side cap of the edge
        public float2 tangent;     // normalized horizontal tangent INTO the edge from the node
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

            // Build the ordered (x, halfWidth) list and emit N+1 endpoints.
            var sorted = new List<(float x, float hw)>(lanesByX.Count);
            foreach (var kv in lanesByX) sorted.Add((kv.Key, kv.Value));

            // gap 0: left kerb. Lateral at first-lane's left edge = first.x - first.hw.
            float xLeftKerb = sorted[0].x - sorted[0].hw;
            outList.Add(MakeEndpoint(edgeEntity, 0, xLeftKerb, halfWidth, leftAtNode, rightAtNode, tIntoEdge));

            // Inner stitches.
            for (int i = 1; i < sorted.Count; i++)
            {
                float xMid = (sorted[i - 1].x + sorted[i].x) * 0.5f;
                outList.Add(MakeEndpoint(edgeEntity, i, xMid, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
            }

            // gap N: right kerb.
            int last = sorted.Count - 1;
            float xRightKerb = sorted[last].x + sorted[last].hw;
            outList.Add(MakeEndpoint(edgeEntity, sorted.Count, xRightKerb, halfWidth, leftAtNode, rightAtNode, tIntoEdge));
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
    }
}
