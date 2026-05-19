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
        // Overlay design — Phase A polish pass.
        //
        // Core principle: the user is here to see and judge the actual painted road
        // markings. Anything we draw on top competes with that. So:
        //
        //   - Default-state committed lines render NOTHING over the road itself. The
        //     endpoint dots + intersection markers already say "a line lives here";
        //     a fat green curve on top is service info that buries the result.
        //   - Hidden segments are the exception — without a marker the user has no
        //     way to know a segment is hidden. We keep a thin red ghost there.
        //   - Hover (UI panel row OR cursor over a line in the world) gets a calm
        //     cyan accent: thin enough to peek through the paint, bright enough to
        //     spot at a glance. No thick yellow blanket like before.
        //   - Drag preview during line creation stays as before — that pass already
        //     used the right "chalk guide" visual language.
        // ============================================================================

        // --- Committed segments (default state) ---
        // Visible segments render no overlay curve at all — the road paint speaks for
        // itself. Only hidden segments get a marker, since without it the user can't
        // tell what's missing.
        private const float kHiddenSegmentWidth = 0.14f;
        private static readonly Color kColHiddenSegment = new Color(1.00f, 0.35f, 0.35f, 0.30f);

        // --- Hover highlight (UI panel hover OR in-game cursor over line) ---
        // Thin cyan trace — readable as "this is the line you're focused on" without
        // burying the real markings. Replaces the old fat-yellow highlight that
        // doubled as the line's own visualisation.
        private const float kHighlightedPairCurveWidth = 0.08f;
        private static readonly Color kColHighlightedCurve = new Color(0.40f, 0.90f, 1.00f, 0.85f);
        private static readonly Color kColHighlightedHidden = new Color(1.00f, 0.55f, 0.55f, 0.65f);

        // --- Drag preview (during line creation) ---
        // Very thin, mostly transparent white — like a chalk guide line. Lets the road
        // markings under it stay visible while the user picks an endpoint.
        private const float kPreviewCurveWidth = 0.10f;
        private static readonly Color kColPreviewCurve = new Color(1.00f, 1.00f, 1.00f, 0.55f);

        // --- Endpoint dots ---
        // Compact (was 1.10m → now 0.75m). Per-edge tinting: every road approach
        // gets its own colour from a small qualitative palette, so visually the
        // user can tell at a glance which dots belong to which approach without
        // having to trace tangent directions. Style is no longer encoded in dot
        // colour — the StyleSelector dropdown is the source of truth for that.
        private const float kDotDiameter        = 0.75f;
        private const float kDotOutlineWidth    = 0.08f;
        private static readonly Color kColDotOutline      = new Color(0.06f, 0.08f, 0.12f, 0.90f);
        // Source dot (selected as origin for the new line) — bright white, fully filled.
        private static readonly Color kColDotFillSource   = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        private static readonly Color kColDotOutlineSrc   = new Color(0.10f, 0.10f, 0.10f, 1.00f);
        // Hover-target dot (the dot the cursor is over right now) — bright, slightly bigger.
        private const float kDotDiameterHover = 0.95f;
        private const float kDotOutlineWidthHover = 0.11f;

        // Per-edge palette. Qualitative tab10-ish set — high mutual contrast, none
        // of them clash with the green/red/cyan overlay accents. Picked via a stable
        // hash on edge.Index so the same edge always gets the same colour as the
        // user pans around.
        private static readonly Color[] kEdgePalette = new[]
        {
            new Color(1.00f, 0.55f, 0.20f, 0.85f), // orange
            new Color(0.40f, 0.75f, 1.00f, 0.85f), // sky blue
            new Color(0.55f, 0.95f, 0.55f, 0.85f), // mint green
            new Color(1.00f, 0.40f, 0.75f, 0.85f), // pink
            new Color(0.80f, 0.65f, 1.00f, 0.85f), // lavender
            new Color(1.00f, 0.90f, 0.40f, 0.85f), // yellow
            new Color(0.50f, 0.95f, 0.90f, 0.85f), // teal
            new Color(0.95f, 0.65f, 0.50f, 0.85f), // salmon
        };

        private static Color EdgeDotColor(Entity edge)
        {
            // Unsigned modulo on Entity.Index — Entity.Index can be negative for some
            // builds, so mask to positive before % palette length.
            int idx = (edge.Index & 0x7fffffff) % kEdgePalette.Length;
            return kEdgePalette[idx];
        }
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
            //    build the full Bezier then walk MarkingSegment entries and decide what to
            //    render. The matrix is:
            //
            //      visible × not hovered → nothing (let the real road paint speak)
            //      visible × hovered     → thin cyan trace (hover affordance)
            //      hidden  × not hovered → thin red ghost (otherwise invisible state)
            //      hidden  × hovered     → red ghost, slightly brighter
            //
            //    Intersection markers (red crosses) always render at internal segment
            //    boundaries so the user can see where lines cross — this is service info
            //    independent of hover.
            //
            //    Hover sources (in priority order):
            //      1. UIHoveredSegmentLine/Index — per-segment hover from React popover (C3).
            //         When set, only that ONE segment lights up; the rest of the line stays
            //         in default rendering.
            //      2. UIHoveredLineIndex — React panel row hover; lights all segments of the line.
            //      3. HoveredLineInGame — cursor over line in world; same effect as (2).
            int uiHoveredLine = _uiSystem?.UIHoveredLineIndex ?? -1;
            if (uiHoveredLine < 0) uiHoveredLine = _tool?.HoveredLineInGame ?? -1;
            int hoveredSegLine = _uiSystem?.UIHoveredSegmentLineIndex ?? -1;
            int hoveredSegIdx  = _uiSystem?.UIHoveredSegmentIndex ?? -1;

            var node = _tool.SelectedNode;
            if (node != Entity.Null && EntityManager.HasBuffer<MarkingLine>(node) && EntityManager.HasBuffer<MarkingSegment>(node))
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var segs  = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                int lineCount = lines.Length;
                for (int l = 0; l < lineCount; l++)
                {
                    if (!MarkingCurveBuilder.TryBuild(endpoints, lines[l], out var full)) continue;
                    bool isLineHighlighted = (l == uiHoveredLine);
                    // Per-line counter — matches the segmentIndex React publishes (dense
                    // 0..K-1 per line, not the flat buffer index).
                    int perLineCounter = -1;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        if (seg.lineIndex != l) continue;
                        perLineCounter++;
                        bool isThisSegmentHovered = (l == hoveredSegLine && perLineCounter == hoveredSegIdx);
                        bool isHighlighted = isLineHighlighted || isThisSegmentHovered;

                        // Pick what (if anything) to draw for this segment.
                        bool draw = false;
                        Color color = default;
                        float width = 0f;
                        if (isHighlighted)
                        {
                            draw = true;
                            color = seg.visible ? kColHighlightedCurve : kColHighlightedHidden;
                            // Per-segment hover gets an extra-thick line so it stands out even
                            // when the rest of the line is also highlighted.
                            width = isThisSegmentHovered ? kHighlightedPairCurveWidth * 1.8f : kHighlightedPairCurveWidth;
                        }
                        else if (!seg.visible)
                        {
                            draw = true;
                            color = kColHiddenSegment;
                            width = kHiddenSegmentWidth;
                        }

                        if (draw)
                        {
                            var segBez = Colossal.Mathematics.MathUtils.Cut(full, new float2(seg.tStart, seg.tEnd));
                            buf.DrawCurve(color, segBez, width);
                        }

                        // Intersection markers are always drawn — they're service info that
                        // doesn't compete with the road paint, just sits at the crossing point.
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

            // 3. Endpoint dots (C1):
            //    - normal idle: thin dark outline + edge-palette fill, with a gentle
            //      pulse on diameter (~6% breathing) so the dots read as alive and
            //      clickable rather than as static decoration.
            //    - source: bright white solid (anchor for the drag in SourceSelected
            //      state), wrapped in a wider white "selected" ring at lower alpha so
            //      it's instantly distinguishable from a regular dot.
            //    - hover:  same edge colour but brighter + slightly larger ring.
            //
            //    Pulse is keyed off Time.time so all dots breathe in sync — looks
            //    intentional and less noisy than per-dot phase offsets.
            // Use UnityEngine.Time explicitly — ComponentSystemBase.Time is a different
            // (now-deprecated) thing that resolves first due to inheritance.
            float pulse = 1f + 0.06f * math.sin((float)UnityEngine.Time.timeAsDouble * 2.4f);
            for (int i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                Color edgeColor = EdgeDotColor(ep.edge);
                Color fill;
                Color outline = kColDotOutline;
                float diameter = kDotDiameter;
                float outlineWidth = kDotOutlineWidth;

                if (i == sourceIdx)
                {
                    fill = kColDotFillSource;
                    outline = kColDotOutlineSrc;
                    // Outer "you have selected this point" ring — faint white halo,
                    // bigger than the dot. Drawn first so the dot sits on top.
                    buf.DrawCircle(
                        outlineColor: new Color(1f, 1f, 1f, 0.55f),
                        fillColor: kColTransparent,
                        outlineWidth: 0.10f,
                        styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                        direction: new float2(0f, 1f),
                        position: ep.position,
                        diameter: kDotDiameter * 2.2f);
                }
                else if (i == hoverIdx)
                {
                    fill = new Color(edgeColor.r, edgeColor.g, edgeColor.b, 1.00f);
                    diameter = kDotDiameterHover;
                    outlineWidth = kDotOutlineWidthHover;
                }
                else
                {
                    fill = edgeColor;
                    // Apply the pulse only on idle dots — hover/source already have
                    // their own size emphasis, no need to add wobble on top.
                    diameter *= pulse;
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
