using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Tools;
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

        private const float kDotDiameter        = 1.6f;
        private const float kDotOutlineWidth    = 0.18f;
        private const float kCurveWidth         = 0.35f;
        private const float kPairCurveWidth     = 0.30f;

        // Single-colour palette per the imho.JPG reference: orange dots, white when source/hover.
        private static readonly Color kColDot         = new Color(1.00f, 0.55f, 0.10f, 0.95f);
        private static readonly Color kColOutline     = new Color(0.05f, 0.05f, 0.10f, 0.95f);
        private static readonly Color kColSourceDot   = new Color(1.00f, 1.00f, 1.00f, 1.00f);
        private static readonly Color kColHoverDot    = new Color(1.00f, 0.85f, 0.50f, 1.00f);
        private static readonly Color kColDragCurve   = new Color(1.00f, 1.00f, 1.00f, 0.85f);
        private static readonly Color kColPairCurve   = new Color(0.30f, 1.00f, 0.50f, 0.85f);

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnUpdate()
        {
            if (_tool == null || _toolSystem.activeTool != _tool) return;

            var endpoints = _tool.Endpoints;
            if (endpoints == null || endpoints.Count == 0) return;

            var buf = _overlayRenderSystem.GetBuffer(out JobHandle deps);
            JobHandle our = JobHandle.CombineDependencies(deps, Dependency);

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
