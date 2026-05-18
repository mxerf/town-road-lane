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
        private TownRoadLaneUISystem _uiSystem;
        private EntityQuery _nodesWithPairsQuery;

        // ============================================================================
        // Overlay design — Stage 5d polish pass.
        //
        // Goals:
        //   1. Don't obscure the road markings the user is editing — preview curves and
        //      committed-segment overlays use thin lines + low opacity so existing paint
        //      stays visible underneath.
        //   2. Endpoint dots read as "clickable target" not as "huge fluorescent blob".
        //      Thin outline rings + filled hollow centres mimic UI affordance shapes.
        //   3. Style-aware tinting on dots — current-style choice telegraphed through
        //      the dot fill colour, not a separate UI element.
        // ============================================================================

        // --- Committed segment curves (drawn on top of real markings) ---
        // Thin enough that real paint underneath remains readable; opacity moderate so
        // visible vs hidden segments still distinguish at a glance.
        private const float kPairCurveWidth = 0.18f;
        private static readonly Color kColPairCurve     = new Color(0.45f, 1.00f, 0.55f, 0.65f);
        private static readonly Color kColHiddenSegment = new Color(1.00f, 0.35f, 0.35f, 0.30f);

        // --- UI hover-bridge highlight ---
        // Same family as kColPairCurve but cranked to full alpha + thicker so the user can
        // spot the line being hovered in the panel without confusing it with the rest.
        private const float kHighlightedPairCurveWidth = 0.45f;
        private static readonly Color kColHighlightedCurve = new Color(1.00f, 0.90f, 0.25f, 0.95f);

        // --- Drag preview (during line creation) ---
        // Very thin, mostly transparent white — like a chalk guide line. Lets the road
        // markings under it stay visible while the user picks an endpoint.
        private const float kPreviewCurveWidth = 0.10f;
        private static readonly Color kColPreviewCurve = new Color(1.00f, 1.00f, 1.00f, 0.55f);

        // --- Endpoint dots ---
        // Smaller diameter than 5b (was 1.6m), thin outline. The fill is mostly transparent
        // so the dot reads as a ring with a soft tint rather than a solid disc.
        private const float kDotDiameter        = 1.10f;
        private const float kDotOutlineWidth    = 0.10f;
        private static readonly Color kColDotOutline      = new Color(0.08f, 0.10f, 0.14f, 0.85f);
        private static readonly Color kColDotFillSolid    = new Color(1.00f, 0.65f, 0.20f, 0.55f);
        private static readonly Color kColDotFillDashed   = new Color(0.45f, 0.85f, 1.00f, 0.55f);
        // Source dot (selected as origin for the new line) — bright white, fully filled.
        private static readonly Color kColDotFillSource   = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        private static readonly Color kColDotOutlineSrc   = new Color(0.10f, 0.10f, 0.10f, 1.00f);
        // Hover-target dot (the dot the cursor is over right now) — bright, sized larger.
        private const float kDotDiameterHover = 1.45f;
        private const float kDotOutlineWidthHover = 0.14f;
        // Intersection markers (the "+" shape at every Bezier crossing on the selected node).
        private const float kIntersectionMarkerSize  = 0.55f;
        private const float kIntersectionMarkerWidth = 0.10f;
        private static readonly Color kColIntersection = new Color(1.00f, 0.30f, 0.30f, 0.75f);

        // --- Node rings (overlay on the selectable / configured nodes) ---
        // Sit beneath the dot layer — slightly bigger than the actual node so they read as
        // an outer halo, not as a hit-test target competing with the dots.
        private const float kNodeHoverDiameter      = 5.5f;
        private const float kNodeHoverOutlineWidth  = 0.22f;
        private const float kNodeHasPairsDiameter   = 3.6f;
        private const float kNodeHasPairsOutlineWidth = 0.12f;
        private static readonly Color kColNodeHoverRing    = new Color(0.30f, 0.95f, 1.00f, 0.70f);
        private static readonly Color kColNodeHasPairsRing = new Color(0.45f, 1.00f, 0.55f, 0.55f);
        private static readonly Color kColTransparent      = new Color(0f, 0f, 0f, 0f);

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _uiSystem = World.GetOrCreateSystemManaged<TownRoadLaneUISystem>();
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
            //    Stage 5d hover-bridge: highlight the line that's either
            //      - hovered in the React panel (UIHoveredLineIndex), or
            //      - hovered by the cursor in the game world (HoveredLineInGame).
            //    UI hover wins when both are set; otherwise either source highlights.
            int uiHoveredLine = _uiSystem?.UIHoveredLineIndex ?? -1;
            if (uiHoveredLine < 0) uiHoveredLine = _tool?.HoveredLineInGame ?? -1;
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
                    bool isHighlighted = (l == uiHoveredLine);
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        if (seg.lineIndex != l) continue;
                        var segBez = Colossal.Mathematics.MathUtils.Cut(full, new float2(seg.tStart, seg.tEnd));
                        Color color;
                        float width;
                        if (isHighlighted)
                        {
                            color = seg.visible ? kColHighlightedCurve : new Color(kColHighlightedCurve.r, kColHighlightedCurve.g, kColHighlightedCurve.b, 0.45f);
                            width = kHighlightedPairCurveWidth;
                        }
                        else
                        {
                            color = seg.visible ? kColPairCurve : kColHiddenSegment;
                            width = kPairCurveWidth;
                        }
                        buf.DrawCurve(color, segBez, width);
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
            //    Thin white semi-transparent — see kColPreviewCurve / kPreviewCurveWidth. Lets
            //    the road markings under it stay visible while the user lines up the click.
            if (sourceIdx >= 0 && sourceIdx < endpoints.Count)
            {
                var src = endpoints[sourceIdx];
                if (hoverIdx >= 0 && hoverIdx < endpoints.Count && hoverIdx != sourceIdx)
                {
                    var dst = endpoints[hoverIdx];
                    var bezier = BuildSmoothCurve(src.position, src.tangent, dst.position, dst.tangent);
                    buf.DrawCurve(kColPreviewCurve, bezier, kPreviewCurveWidth);
                }
                else
                {
                    // Free drag: straight line to cursor terrain hit.
                    float3 to = _tool.CursorWorldPos;
                    if (math.lengthsq(to - src.position) > 0.01f)
                        buf.DrawLine(kColPreviewCurve, new Line3.Segment(src.position, to), kPreviewCurveWidth);
                }
            }

            // 3. Endpoint dots. Compact ring shape:
            //    - normal: thin dark outline + soft style-tinted fill (orange = Solid, blue = Dashed)
            //    - source: bright white solid (anchor for the drag in SourceSelected state)
            //    - hover:  same style colour but brighter + larger ring (cursor target affordance)
            var styleColor = StyleDotColor(_tool.CurrentStyle);
            for (int i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                Color fill;
                Color outline = kColDotOutline;
                float diameter = kDotDiameter;
                float outlineWidth = kDotOutlineWidth;

                if (i == sourceIdx)
                {
                    fill = kColDotFillSource;
                    outline = kColDotOutlineSrc;
                }
                else if (i == hoverIdx)
                {
                    fill = new Color(styleColor.r, styleColor.g, styleColor.b, 0.85f);
                    diameter = kDotDiameterHover;
                    outlineWidth = kDotOutlineWidthHover;
                }
                else
                {
                    fill = styleColor;
                }

                buf.DrawCircle(
                    outlineColor: outline,
                    fillColor: fill,
                    outlineWidth: outlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: ep.position,
                    diameter: diameter);
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

        /// <summary>Pick the dot fill colour for the currently-selected style. New style values
        /// land in <c>switch</c>; unrecognised ones fall back to the solid (orange) tint so
        /// the UI doesn't go invisible when a future-version style isn't known yet. G87
        /// variants use the same tint as their non-G87 counterparts — the visual difference
        /// shows up in the actual painted line, not the picker affordance.</summary>
        private static Color StyleDotColor(MarkingStyle style) => style switch
        {
            MarkingStyle.Solid       => kColDotFillSolid,
            MarkingStyle.Dashed      => kColDotFillDashed,
            MarkingStyle.G87Solid    => kColDotFillSolid,
            MarkingStyle.G87Dashed   => kColDotFillDashed,
            MarkingStyle.DoubleSolid => kColDotFillSolid,
            _                        => kColDotFillSolid,
        };

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
