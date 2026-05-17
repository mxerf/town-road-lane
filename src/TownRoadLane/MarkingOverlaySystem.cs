using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Net;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 4d/4f overlay: draws gap-based connector dots, drag-curve to the hover/cursor,
    /// and an outline-curve preview for every committed MarkingPair on the selected node.
    /// Idle unless <see cref="MarkingNodeToolSystem"/> is the active tool.
    /// All draws go through vanilla <see cref="OverlayRenderSystem"/>.
    ///
    /// Curve shape (drag preview + committed pairs): control points are offset along each
    /// endpoint's outward tangent by 1/3 of the chord length. This produces a smooth S/U
    /// that always leaves the dot perpendicular to the edge (looks like a real marking line
    /// flowing through the intersection), as opposed to the straight chord we had before.
    /// </summary>
    public partial class MarkingOverlaySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private ToolSystem _toolSystem;
        private MarkingNodeToolSystem _tool;
        private OverlayRenderSystem _overlayRenderSystem;
        private EntityQuery _nodesWithPairsQuery;

        private const float kDotDiameter        = 1.6f;
        private const float kDotOutlineWidth    = 0.18f;
        private const float kCurveWidth         = 0.35f;
        private const float kPairCurveWidth     = 0.30f;

        // Node-level highlights (Stage 5a).
        // Hover ring sits around the node centre to mark "click here to select".
        // Existing-pairs ring is smaller + thinner so two nodes (hovered AND has pairs)
        // read as two distinct rings, not one fat blob.
        private const float kNodeHoverDiameter      = 6.0f;
        private const float kNodeHoverOutlineWidth  = 0.35f;
        private const float kNodeHasPairsDiameter   = 4.0f;
        private const float kNodeHasPairsOutlineWidth = 0.18f;

        // Single-colour palette per the imho.JPG reference: orange dots, white when source/hover.
        private static readonly Color kColDot         = new Color(1.00f, 0.55f, 0.10f, 0.95f);
        private static readonly Color kColOutline     = new Color(0.05f, 0.05f, 0.10f, 0.95f);
        private static readonly Color kColSourceDot   = new Color(1.00f, 1.00f, 1.00f, 1.00f);
        private static readonly Color kColHoverDot    = new Color(1.00f, 0.85f, 0.50f, 1.00f);
        private static readonly Color kColDragCurve   = new Color(1.00f, 1.00f, 1.00f, 0.85f);
        private static readonly Color kColPairCurve   = new Color(0.30f, 1.00f, 0.50f, 0.85f);
        // Node ring colours: hover = bright cyan ("clickable"); has-pairs = soft green ("configured").
        private static readonly Color kColNodeHoverRing    = new Color(0.20f, 0.95f, 1.00f, 0.85f);
        private static readonly Color kColNodeHasPairsRing = new Color(0.30f, 1.00f, 0.50f, 0.70f);
        private static readonly Color kColTransparent      = new Color(0f, 0f, 0f, 0f);

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            // Every node that has at least one user-configured MarkingPair — used to render
            // a faint "this node has custom markings" ring while the tool is active. Buffer is
            // empty on most nodes so query stays cheap.
            _nodesWithPairsQuery = GetEntityQuery(
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<MarkingPair>());
        }

        protected override void OnUpdate()
        {
            if (_tool == null || _toolSystem.activeTool != _tool) return;

            var buf = _overlayRenderSystem.GetBuffer(out JobHandle deps);
            JobHandle our = JobHandle.CombineDependencies(deps, Dependency);

            // 0. "Configured" rings on every node that already has at least one MarkingPair.
            //    Faint green outline. Skipped for the currently-selected node to avoid stacking
            //    rings on top of the dot layer below.
            DrawHasPairsRings(buf, _tool.SelectedNode);

            // 0b. Hover ring on the node under the cursor (Default state only; once a node is
            //     selected the dots take over as the "you are here" indicator).
            if (_tool.ToolState == MarkingNodeToolSystem.State.Default && _tool.HoveredNode != Entity.Null)
            {
                DrawNodeRing(buf, _tool.HoveredNode, kColNodeHoverRing, kNodeHoverDiameter, kNodeHoverOutlineWidth);
            }

            var endpoints = _tool.Endpoints;
            if (endpoints == null || endpoints.Count == 0)
            {
                _overlayRenderSystem.AddBufferWriter(our);
                Dependency = our;
                return;
            }

            int sourceIdx = _tool.SourceEndpointIndex;
            int hoverIdx  = _tool.HoveredEndpointIndex;

            // 1. Committed pairs (bottom layer). Drawn as smooth curves between the two endpoints,
            //    using each endpoint's inward tangent so the curve leaves the dot perpendicular to
            //    the road — matches imho.JPG sample.
            var node = _tool.SelectedNode;
            if (node != Entity.Null && EntityManager.HasBuffer<MarkingPair>(node))
            {
                var pairs = EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true);
                for (int p = 0; p < pairs.Length; p++)
                {
                    if (TryResolvePair(pairs[p], out float3 a, out float2 ta, out float3 b, out float2 tb))
                    {
                        var bezier = BuildSmoothCurve(a, ta, b, tb);
                        buf.DrawCurve(kColPairCurve, bezier, kPairCurveWidth);
                    }
                }
            }

            // 2. Drag preview from source to hovered target (or to free cursor when no hover).
            if (sourceIdx >= 0 && sourceIdx < endpoints.Count)
            {
                var src = endpoints[sourceIdx];
                if (hoverIdx >= 0 && hoverIdx < endpoints.Count && hoverIdx != sourceIdx)
                {
                    var dst = endpoints[hoverIdx];
                    var bezier = BuildSmoothCurve(src.position, src.tangent, dst.position, dst.tangent);
                    buf.DrawCurve(kColDragCurve, bezier, kCurveWidth);
                }
                else
                {
                    // Free drag: straight line to cursor terrain hit.
                    float3 to = _tool.CursorWorldPos;
                    if (math.lengthsq(to - src.position) > 0.01f)
                        buf.DrawLine(kColDragCurve, new Line3.Segment(src.position, to), kCurveWidth);
                }
            }

            // 3. Dots on top. Source = white, hover = light orange, others = orange.
            for (int i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                Color fill = kColDot;
                if (i == sourceIdx)     fill = kColSourceDot;
                else if (i == hoverIdx) fill = kColHoverDot;

                buf.DrawCircle(
                    outlineColor: kColOutline,
                    fillColor: fill,
                    outlineWidth: kDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: ep.position,
                    diameter: kDotDiameter);
            }

            _overlayRenderSystem.AddBufferWriter(our);
            Dependency = our;
        }

        /// <summary>
        /// Cubic Bezier whose control points are pushed along each endpoint's tangent by 1/3 of
        /// the chord length. Tangent direction is taken to point INTO the node (away from the
        /// endpoint's parent edge), so the curve leaves the dot smoothly even when the two
        /// endpoints belong to roads facing different directions.
        /// </summary>
        private static Bezier4x3 BuildSmoothCurve(float3 a, float2 ta, float3 b, float2 tb)
        {
            // tangent stored in MarkingEndpoint points OUTWARD from the node into the edge. The
            // curve should leave the dot in the OPPOSITE direction (into the intersection), so
            // we negate.
            // Pull factor matches MarkingPairEmissionSystem so the drag preview reflects the
            // real Bezier the system will emit.
            float chord = math.distance(a, b);
            float pull = chord * 0.4f;
            float3 ta3 = new float3(-ta.x, 0f, -ta.y);
            float3 tb3 = new float3(-tb.x, 0f, -tb.y);
            float3 ctrl1 = a + ta3 * pull;
            float3 ctrl2 = b + tb3 * pull;
            return new Bezier4x3(a, ctrl1, ctrl2, b);
        }

        /// <summary>
        /// Resolve a MarkingPair entry to the two world-space attach points + tangents needed
        /// for curve drawing. We re-derive them by running the same extractor logic in-place —
        /// avoids caching a structure that would go stale on road edits.
        /// </summary>
        private bool TryResolvePair(MarkingPair pair, out float3 a, out float2 ta, out float3 b, out float2 tb)
        {
            a = default; ta = default; b = default; tb = default;
            var node = _tool.SelectedNode;
            if (node == Entity.Null) return false;
            if (!TryResolveEndpoint(node, pair.sourceEdge, pair.sourceGapIndex, out a, out ta)) return false;
            if (!TryResolveEndpoint(node, pair.targetEdge, pair.targetGapIndex, out b, out tb)) return false;
            return true;
        }

        /// <summary>
        /// Draw a faint outline ring around every node that has at least one MarkingPair.
        /// Lets the user spot "I already customised this intersection" at a glance while panning
        /// the city. Skips the currently-selected node — its dots already mark it clearly and a
        /// ring around the dots adds visual noise.
        /// </summary>
        private void DrawHasPairsRings(OverlayRenderSystem.Buffer buf, Entity excludeNode)
        {
            using var nodes = _nodesWithPairsQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n == excludeNode) continue;
                // Empty buffers still match the query — filter at draw time so we don't show
                // rings on nodes whose pairs were all deleted but buffer wasn't removed.
                if (!EntityManager.HasBuffer<MarkingPair>(n)) continue;
                if (EntityManager.GetBuffer<MarkingPair>(n, isReadOnly: true).Length == 0) continue;
                DrawNodeRing(buf, n, kColNodeHasPairsRing, kNodeHasPairsDiameter, kNodeHasPairsOutlineWidth);
            }
        }

        private void DrawNodeRing(OverlayRenderSystem.Buffer buf, Entity node, Color ringColor, float diameter, float outlineWidth)
        {
            if (!EntityManager.HasComponent<Node>(node)) return;
            float3 pos = EntityManager.GetComponentData<Node>(node).m_Position;
            buf.DrawCircle(
                outlineColor: ringColor,
                fillColor: kColTransparent,
                outlineWidth: outlineWidth,
                styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                direction: new float2(0f, 1f),
                position: pos,
                diameter: diameter);
        }

        private bool TryResolveEndpoint(Entity node, Entity edge, int gapIndex, out float3 pos, out float2 tan)
        {
            pos = default; tan = default;
            var list = new System.Collections.Generic.List<MarkingEndpoint>(8);
            // Extract only this edge's endpoints (avoids walking all ConnectedEdges of the node).
            // We rely on the extractor's internal per-edge pass; the easiest way to reuse it is
            // the public Extract(node) — small extra work but the lists are tiny.
            var all = MarkingEndpointExtractor.Extract(EntityManager, node);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].edge == edge && all[i].gapIndex == gapIndex)
                {
                    pos = all[i].position;
                    tan = all[i].tangent;
                    return true;
                }
            }
            return false;
        }
    }
}
