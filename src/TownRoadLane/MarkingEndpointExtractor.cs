using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane
{
    /// <summary>
    /// One attach point for a per-node marking pair. Two are produced per qualifying sublane
    /// (one for each side: right/left). World-space position is computed from the same lane-corner
    /// formula vanilla uses internally — see <c>RESEARCH_ui_endpoints.md</c> §1.1 (decomp:
    /// <c>SecondaryLaneSystem.cs</c> lines 374-388).
    /// </summary>
    public struct MarkingEndpoint
    {
        public Entity edge;        // road edge entity this lane belongs to
        public int    laneIndex;   // index into the edge's SubLane buffer
        public Entity lane;        // the sublane entity itself
        public bool   isRight;     // true = right side of the lane (curb side typically), false = left
        public float3 position;    // world-space point where a marking would attach
        public float2 tangent;     // normalized horizontal tangent at this end of the lane
    }

    /// <summary>
    /// Extracts marking attach points for the node selected by the phase 4 tool.
    /// Managed (main-thread) — runs once per selection, output is a small list (≤ ~20 per node).
    /// For larger or per-frame use see Traffic's NativeQuadTree pattern instead.
    ///
    /// Algorithm (per RESEARCH_ui_endpoints.md §1.3):
    ///   1. ConnectedEdge buffer on the node → list of edges touching it.
    ///   2. For each edge: which end (start or end) touches our node? — compare Edge.m_Start / m_End.
    ///   3. SubLane buffer on the edge → candidate sublanes.
    ///   4. Filter to EdgeLane sublanes that actually touch this node end (EdgeLane.m_EdgeDelta).
    ///   5. Skip MasterLane and already-secondary sublanes (mirrors vanilla loop at
    ///      SecondaryLaneSystem.cs:362-365).
    ///   6. Apply the lane-corner offset formula:
    ///        attachRight = curve.endpoint ± Right(tangent) * width/2
    ///        attachLeft  = curve.endpoint ± Left(tangent)  * width/2
    ///      where the endpoint is curve.a (start node) or curve.d (end node).
    ///   7. Emit one entry per side (isRight = true | false).
    /// </summary>
    public static class MarkingEndpointExtractor
    {
        /// <summary>Result list size hint: typical 4-way intersection has ~16 endpoints (4 edges × 2 lanes × 2 sides).</summary>
        public static List<MarkingEndpoint> Extract(EntityManager em, Entity node)
        {
            var results = new List<MarkingEndpoint>(16);
            if (node == Entity.Null) return results;
            if (!em.HasBuffer<ConnectedEdge>(node)) return results;

            var connected = em.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
            for (int e = 0; e < connected.Length; e++)
            {
                var edgeEntity = connected[e].m_Edge;
                if (!em.HasComponent<Edge>(edgeEntity)) continue;
                if (!em.HasBuffer<SubLane>(edgeEntity)) continue;

                var edge = em.GetComponentData<Edge>(edgeEntity);
                bool nodeIsStart = edge.m_Start == node;
                bool nodeIsEnd   = edge.m_End == node;
                if (!nodeIsStart && !nodeIsEnd) continue; // not actually wired to us — shouldn't happen but guard

                var subLanes = em.GetBuffer<SubLane>(edgeEntity, isReadOnly: true);
                for (int s = 0; s < subLanes.Length; s++)
                {
                    var laneEntity = subLanes[s].m_SubLane;

                    // Only EdgeLane sublanes have endpoints relevant to edge-ends at a node.
                    // NodeLane (intersection routing) live on the node itself and need a separate path
                    // (open item in RESEARCH_ui_endpoints.md §3 — defer until we observe it matters).
                    if (!em.HasComponent<EdgeLane>(laneEntity)) continue;
                    if (!em.HasComponent<Curve>(laneEntity)) continue;
                    // Skip master + already-secondary; mirrors vanilla SecondaryLaneSystem.cs:362-365.
                    if (em.HasComponent<MasterLane>(laneEntity)) continue;
                    if (em.HasComponent<Game.Net.SecondaryLane>(laneEntity)) continue;

                    var edgeLane = em.GetComponentData<EdgeLane>(laneEntity);
                    bool touchesN = nodeIsStart
                        ? edgeLane.m_EdgeDelta.x == 0f
                        : edgeLane.m_EdgeDelta.y == 1f;
                    if (!touchesN) continue;

                    if (!em.HasComponent<PrefabRef>(laneEntity)) continue;
                    var lanePrefab = em.GetComponentData<PrefabRef>(laneEntity).m_Prefab;
                    if (!em.HasComponent<NetLaneData>(lanePrefab)) continue;
                    var netLaneData = em.GetComponentData<NetLaneData>(lanePrefab);
                    float2 width = netLaneData.m_Width;
                    if (em.HasComponent<NodeLane>(laneEntity))
                    {
                        var nl = em.GetComponentData<NodeLane>(laneEntity);
                        width += nl.m_WidthOffset;
                    }

                    var curve = em.GetComponentData<Curve>(laneEntity);
                    float2 startTangent = math.normalizesafe(MathUtils.StartTangent(curve.m_Bezier).xz);
                    float2 endTangent   = math.normalizesafe(MathUtils.EndTangent(curve.m_Bezier).xz);

                    // Compute lane-corner positions — identical formula to vanilla
                    // SecondaryLaneSystem.cs:385-388.
                    float3 startRight = curve.m_Bezier.a; startRight.xz += MathUtils.Right(startTangent) * (width.x * 0.5f);
                    float3 startLeft  = curve.m_Bezier.a; startLeft.xz  += MathUtils.Left(startTangent)  * (width.y * 0.5f);
                    float3 endRight   = curve.m_Bezier.d; endRight.xz   += MathUtils.Right(endTangent)   * (width.y * 0.5f);
                    float3 endLeft    = curve.m_Bezier.d; endLeft.xz    += MathUtils.Left(endTangent)    * (width.x * 0.5f);

                    float3 attachRight, attachLeft;
                    float2 tangentAtN;
                    if (nodeIsStart) { attachRight = startRight; attachLeft = startLeft;  tangentAtN = startTangent; }
                    else             { attachRight = endRight;   attachLeft = endLeft;    tangentAtN = endTangent;   }

                    results.Add(new MarkingEndpoint
                    {
                        edge = edgeEntity, laneIndex = s, lane = laneEntity,
                        isRight = true, position = attachRight, tangent = tangentAtN,
                    });
                    results.Add(new MarkingEndpoint
                    {
                        edge = edgeEntity, laneIndex = s, lane = laneEntity,
                        isRight = false, position = attachLeft, tangent = tangentAtN,
                    });
                }
            }

            return results;
        }
    }
}
