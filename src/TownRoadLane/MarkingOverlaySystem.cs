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
        // Stage 5b: hidden segment ghost — soft red so the user can tell "this would be drawn
        // if I un-hid it" apart from "this is the active line". Intersection markers = bright red X.
        private static readonly Color kColHiddenSegment = new Color(1.00f, 0.30f, 0.30f, 0.35f);
        private static readonly Color kColIntersection  = new Color(1.00f, 0.20f, 0.20f, 0.95f);
        private const float kIntersectionMarkerSize  = 0.7f;
        private const float kIntersectionMarkerWidth = 0.18f;
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
            // Every node that has at least one user-configured MarkingLine — used to render
            // a faint "this node has custom markings" ring while the tool is active. Buffer is
            // empty on most nodes so query stays cheap.
            _nodesWithPairsQuery = GetEntityQuery(
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<MarkingLine>());
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

            // 1. Committed lines + intersection markers (bottom layer). For each MarkingLine,
            //    build the full Bezier then walk MarkingSegment entries and draw visible segments
            //    in solid green, hidden segments as dashed red ghost (so the user can see what
            //    Stage 5d will let them un-hide). Intersection markers (red crosses) sit at every
            //    boundary that isn't 0 or 1.
            var node = _tool.SelectedNode;
            if (node != Entity.Null && EntityManager.HasBuffer<MarkingLine>(node) && EntityManager.HasBuffer<MarkingSegment>(node))
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var segs  = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                // Build full Bezier per line once.
                int lineCount = lines.Length;
                for (int l = 0; l < lineCount; l++)
                {
                    if (!MarkingCurveBuilder.TryBuild(endpoints, lines[l], out var full)) continue;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        if (seg.lineIndex != l) continue;
                        var segBez = Colossal.Mathematics.MathUtils.Cut(full, new float2(seg.tStart, seg.tEnd));
                        var color = seg.visible ? kColPairCurve : kColHiddenSegment;
                        buf.DrawCurve(color, segBez, kPairCurveWidth);
                        // Draw a small "X" at every internal boundary. Same X is emitted twice
                        // (once for each adjacent segment); cheap, doesn't matter visually.
                        if (seg.tStart > 0.001f && seg.tStart < 0.999f)
                            DrawIntersectionMarker(buf, Colossal.Mathematics.MathUtils.Position(full, seg.tStart));
                        if (seg.tEnd > 0.001f && seg.tEnd < 0.999f)
                            DrawIntersectionMarker(buf, Colossal.Mathematics.MathUtils.Position(full, seg.tEnd));
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
        /// Cubic Bezier shared by drag preview + emission. Thin wrapper around
        /// <see cref="MarkingCurveBuilder"/> kept so the existing call site below doesn't change
        /// shape — all the actual math lives in the shared builder.
        /// </summary>
        private static Bezier4x3 BuildSmoothCurve(float3 a, float2 ta, float3 b, float2 tb)
            => MarkingCurveBuilder.Build(a, ta, b, tb);

        /// <summary>Draw a small "+" shape at the intersection point. Projected on terrain so it
        /// stays visible on slopes.</summary>
        private static void DrawIntersectionMarker(OverlayRenderSystem.Buffer buf, float3 p)
        {
            float s = kIntersectionMarkerSize;
            // Two perpendicular line segments centred at p, oriented along world XZ axes.
            buf.DrawLine(kColIntersection,
                new Line3.Segment(p + new float3(-s, 0f, 0f), p + new float3(s, 0f, 0f)),
                kIntersectionMarkerWidth);
            buf.DrawLine(kColIntersection,
                new Line3.Segment(p + new float3(0f, 0f, -s), p + new float3(0f, 0f, s)),
                kIntersectionMarkerWidth);
        }

        /// <summary>
        /// Draw a faint outline ring around every node that has at least one MarkingLine.
        /// Lets the user spot "I already customised this intersection" at a glance while panning
        /// the city. Skips the currently-selected node — its dots already mark it clearly.
        /// </summary>
        private void DrawHasPairsRings(OverlayRenderSystem.Buffer buf, Entity excludeNode)
        {
            using var nodes = _nodesWithPairsQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n == excludeNode) continue;
                if (!EntityManager.HasBuffer<MarkingLine>(n)) continue;
                if (EntityManager.GetBuffer<MarkingLine>(n, isReadOnly: true).Length == 0) continue;
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
    }
}
