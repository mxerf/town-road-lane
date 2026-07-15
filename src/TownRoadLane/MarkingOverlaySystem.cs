using System.Collections.Generic;
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
        // Per-edge tinting: every road approach gets its own colour from a small
        // qualitative palette, so visually the user can tell at a glance which dots
        // belong to which approach without having to trace tangent directions. Style
        // is no longer encoded in dot colour — the StyleSelector dropdown owns that.
        //
        // Free vs connected convention (UI polish pass): a dot with no committed line
        // renders as a hollow ring ("empty socket"), a dot that already anchors at
        // least one MarkingLine renders filled with a small white core ("plugged").
        private const float kDotDiameter        = 0.65f;
        private const float kDotOutlineWidth    = 0.10f;
        private const float kDotFreeFillAlpha   = 0.14f;
        private const float kDotConnectedCoreDiameter = 0.20f;
        private static readonly Color kColDotConnectedCore = new Color(1.00f, 1.00f, 1.00f, 0.90f);
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
        // Intersection markers — a small red dot at every Bezier crossing on the selected node.
        // Was a "+" cross 1.1m across; that read as an alarm icon and buried the road paint.
        // A compact dot still says "lines cross here" without shouting.
        private const float kIntersectionDotDiameter     = 0.32f;
        private const float kIntersectionDotOutlineWidth = 0.06f;
        private static readonly Color kColIntersection        = new Color(1.00f, 0.30f, 0.30f, 0.60f);
        private static readonly Color kColIntersectionOutline = new Color(0.25f, 0.05f, 0.05f, 0.80f);

        // --- Corner anchors (Phase 6a) ---
        // Sit at intersection corners where kerbs of neighbour edges meet. Visually distinct
        // from lane endpoints: square-ish (diamond from rotated outline) silhouette is hard
        // in OverlayRenderSystem, so we use a smaller white ring with a darker outline — it
        // reads as "infrastructure point, not a line attach point". Only visible to the user
        // for now; the polygon area tool (6b) will make them clickable.
        private const float kCornerDotDiameter      = 0.55f;
        private const float kCornerDotOutlineWidth  = 0.10f;
        private static readonly Color kColCornerFill    = new Color(0.95f, 0.95f, 0.95f, 0.55f);
        private static readonly Color kColCornerOutline = new Color(0.15f, 0.20f, 0.25f, 0.90f);

        // --- Area-tool visuals (Phase 6b) ---
        // Distinct palette from the line tool. IMT convention, restyled to the same
        // ring language as the line-mode endpoint dots (UI polish pass):
        //   • available candidate dots = hollow warm-yellow rings — "you can click this"
        //     without blanketing the junction in solid candy dots.
        //   • hovered candidate        = solid bright yellow, slightly larger.
        //   • placed contour edges     = solid white (the polygon-so-far, chalk outline)
        //   • live preview edge        = thin white from last placed vertex to cursor
        //   • preview-to-close (cursor over start vertex with ≥3 placed) = bright green
        //   • start-vertex highlight   = bright outline ring, slightly larger
        private const float kAreaCandDotDiameter      = 0.90f;
        private const float kAreaCandDotOutlineWidth  = 0.12f;
        private static readonly Color kColAreaCandFill         = new Color(1.00f, 0.85f, 0.20f, 0.14f);
        private static readonly Color kColAreaCandRing         = new Color(1.00f, 0.85f, 0.20f, 0.95f);
        private static readonly Color kColAreaCandOutline      = new Color(0.20f, 0.16f, 0.04f, 0.95f);
        private static readonly Color kColAreaHoverFill        = new Color(1.00f, 0.95f, 0.45f, 1.00f);
        private const float kAreaHoverDotDiameter     = 1.15f;
        // Placed vertices: white with dark outline — stand out against the warm candidate set.
        private const float kAreaPlacedDotDiameter    = 1.10f;
        private static readonly Color kColAreaPlacedFill       = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        // Outline of the contour-so-far + the preview edge to cursor.
        private const float kAreaContourWidth         = 0.16f;
        private static readonly Color kColAreaContour          = new Color(1.00f, 1.00f, 1.00f, 0.90f);
        private const float kAreaPreviewWidth         = 0.13f;
        private static readonly Color kColAreaPreview          = new Color(1.00f, 1.00f, 1.00f, 0.55f);
        // Closing-imminent preview (cursor over start vertex, ≥3 placed).
        private static readonly Color kColAreaPreviewClose     = new Color(0.40f, 1.00f, 0.55f, 0.95f);
        // Start-vertex closure ring — sits behind the start dot when close is possible.
        private const float kAreaStartRingDiameter    = 1.70f;
        private const float kAreaStartRingWidth       = 0.14f;

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

            // Phase 6b: while collecting a polygon area, replace the line-tool overlay with a
            // dedicated visualisation (candidate dots + contour). Done before the early-return
            // on empty endpoints so an area with only corner anchors still draws.
            if (_tool.ToolState == MarkingNodeToolSystem.State.AreaSelecting)
            {
                DrawAreaModeOverlay(buf);
                _overlayRenderSystem.AddBufferWriter(our);
                Dependency = our;
                return;
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

            // Phase 7c: hovered-area outline. Same source priority as lines — panel row /
            // popover hover (UI) wins, else cursor-inside-area from the tool.
            int hoveredArea = _uiSystem?.UIHoveredAreaIndex ?? -1;
            if (hoveredArea < 0) hoveredArea = _tool?.HoveredAreaInGame ?? -1;

            var node = _tool.SelectedNode;
            if (hoveredArea >= 0 && node != Entity.Null)
                DrawHoveredAreaOutline(buf, node, hoveredArea);
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
            //    - free (no committed line): hollow ring in the edge colour — reads as
            //      an empty socket you can plug a line into. No pulse — static rings
            //      look like instrumentation, breathing dots looked like a toy.
            //    - connected (anchors ≥1 MarkingLine): filled edge colour + small white
            //      core, so occupied points are obvious at a glance.
            //    - source: bright white solid (anchor for the drag in SourceSelected
            //      state), wrapped in a wider white "selected" ring at lower alpha so
            //      it's instantly distinguishable from a regular dot.
            //    - hover:  same edge colour but solid bright + slightly larger ring.
            _connectedScratch.Clear();
            if (node != Entity.Null && EntityManager.HasBuffer<MarkingLine>(node))
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                for (int l = 0; l < lines.Length; l++)
                {
                    _connectedScratch.Add((lines[l].sourceEdge, lines[l].sourceGapIndex));
                    _connectedScratch.Add((lines[l].targetEdge, lines[l].targetGapIndex));
                }
            }
            for (int i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                Color edgeColor = EdgeDotColor(ep.edge);
                bool connected = _connectedScratch.Contains((ep.edge, ep.gapIndex));
                Color fill;
                Color outline;
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
                    outline = kColDotOutline;
                    diameter = kDotDiameterHover;
                    outlineWidth = kDotOutlineWidthHover;
                }
                else if (connected)
                {
                    fill = edgeColor;
                    outline = kColDotOutline;
                }
                else
                {
                    // Hollow ring: the edge colour lives on the outline, the middle is
                    // a barely-there tint so the dot still reads on dark asphalt.
                    fill = new Color(edgeColor.r, edgeColor.g, edgeColor.b, kDotFreeFillAlpha);
                    outline = new Color(edgeColor.r, edgeColor.g, edgeColor.b, 0.95f);
                }

                buf.DrawCircle(
                    outlineColor: outline,
                    fillColor: fill,
                    outlineWidth: outlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: ep.position,
                    diameter: diameter);

                if (connected && i != sourceIdx)
                {
                    buf.DrawCircle(
                        outlineColor: kColTransparent,
                        fillColor: kColDotConnectedCore,
                        outlineWidth: 0f,
                        styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                        direction: new float2(0f, 1f),
                        position: ep.position,
                        diameter: kDotConnectedCoreDiameter);
                }
            }

            // 4. Corner anchors (Phase 6a). Currently render-only — not clickable yet. Live
            //    underneath lane endpoints visually (smaller, lower contrast) so they don't
            //    compete for attention while the user is building lines, but are visible enough
            //    to confirm extraction is working.
            var corners = _tool.CornerAnchors;
            if (corners != null)
            {
                for (int i = 0; i < corners.Count; i++)
                {
                    buf.DrawCircle(
                        outlineColor: kColCornerOutline,
                        fillColor: kColCornerFill,
                        outlineWidth: kCornerDotOutlineWidth,
                        styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                        direction: new float2(0f, 1f),
                        position: corners[i].position,
                        diameter: kCornerDotDiameter);
                }
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

        /// <summary>
        /// Phase 6b: overlay while the user is collecting vertices for an area polygon.
        /// Layers (back to front):
        ///   1. Candidate dots (lane endpoints, corner anchors, line crossings) — warm yellow,
        ///      all clickable.
        ///   2. Already-placed contour edges — solid white chord between consecutive vertices.
        ///      (Curved edges of kind LineBezier are still rendered as a chord here — sampling
        ///      to the line's Bezier happens at emission time in 6c; for the 6b preview a
        ///      straight chord is good enough and keeps the overlay cheap.)
        ///   3. Preview edge from the last placed vertex to the cursor (or hovered candidate).
        ///      Bright green if closing on the start vertex is possible, white otherwise.
        ///   4. Placed vertices — white filled dots (drawn on top of the contour so they read as
        ///      "vertex you placed here").
        ///   5. Start-vertex closure ring — bright halo around the first vertex once 3+ are
        ///      placed, hinting that a click on it will close the polygon.
        ///   6. Hovered candidate — brighter fill + larger ring on top of everything.
        /// </summary>
        /// <summary>Phase 7c: bright contour around every piece of the hovered area — the area
        /// counterpart of the line hover trace. Visible pieces get the calm cyan, hidden pieces
        /// the red ghost tint (same colour language as segments).</summary>
        private void DrawHoveredAreaOutline(OverlayRenderSystem.Buffer buf, Entity node, int areaIndex)
        {
            if (!EntityManager.HasBuffer<MarkingAreaPiece>(node)
                || !EntityManager.HasBuffer<MarkingAreaPieceVertex>(node)) return;
            var pieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
            var verts = EntityManager.GetBuffer<MarkingAreaPieceVertex>(node, isReadOnly: true);
            for (int p = 0; p < pieces.Length; p++)
            {
                var pd = pieces[p];
                if (pd.areaIndex != areaIndex || pd.vertexCount < 3) continue;
                var color = pd.visible ? kColHighlightedCurve : kColHighlightedHidden;
                for (int v = 0; v < pd.vertexCount; v++)
                {
                    int i0 = pd.firstVertex + v;
                    int i1 = pd.firstVertex + (v + 1) % pd.vertexCount;
                    if (i0 < 0 || i1 < 0 || i0 >= verts.Length || i1 >= verts.Length) return;
                    buf.DrawLine(color, new Line3.Segment(verts[i0].position, verts[i1].position), kHighlightedPairCurveWidth);
                }
            }
        }

        // Scratch for the sampled draft contour — reused every frame to stay alloc-free.
        private readonly List<float3> _areaContourScratch = new List<float3>();

        // Scratch set of (edge, gapIndex) keys that anchor at least one committed
        // MarkingLine on the selected node — rebuilt every frame for the free/connected
        // dot styling, reused to stay alloc-free.
        private readonly HashSet<(Entity, int)> _connectedScratch = new HashSet<(Entity, int)>();

        private void DrawAreaModeOverlay(OverlayRenderSystem.Buffer buf)
        {
            var endpoints = _tool.Endpoints;
            var corners = _tool.CornerAnchors;
            var crossings = _tool.LineIntersections;
            var polygon = _tool.AreaPolygon;
            var hover = _tool.AreaHover;
            float3 cursor = _tool.CursorWorldPos;

            // 1. Candidate dots — all selectable anchors, drawn as hollow yellow rings
            //    (the colour lives on the outline, the middle is a barely-there tint) so
            //    the junction doesn't drown in solid dots.
            for (int i = 0; i < endpoints.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandRing,
                    fillColor: kColAreaCandFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: endpoints[i].position,
                    diameter: kAreaCandDotDiameter);
            }
            for (int i = 0; i < corners.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandRing,
                    fillColor: kColAreaCandFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: corners[i].position,
                    diameter: kAreaCandDotDiameter);
            }
            // Phase 7a: line-crossing anchors — same candidate affordance as the dots above
            // (they behave identically on click), drawn from the tool's current extraction.
            for (int i = 0; i < crossings.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandRing,
                    fillColor: kColAreaCandFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: crossings[i].position,
                    diameter: kAreaCandDotDiameter);
            }

            // 2. Contour edges (placed vertices' chain). Phase 7b: LineBezier edges come back
            //    sampled along their shared line, so the preview shows the arc the committed
            //    area will actually follow (straight edges stay two-point chords).
            _tool.BuildAreaContourPath(_areaContourScratch);
            for (int i = 1; i < _areaContourScratch.Count; i++)
            {
                buf.DrawLine(kColAreaContour,
                    new Line3.Segment(_areaContourScratch[i - 1], _areaContourScratch[i]),
                    kAreaContourWidth);
            }

            // 3. Preview edge from last placed vertex to cursor / hovered candidate.
            if (polygon.Count > 0)
            {
                var lastPos = polygon[polygon.Count - 1].position;
                float3 targetPos;
                bool closingImminent = false;
                if (hover.IsValid && _tool.TryGetAreaAnchorPos(hover, out var hovPos))
                {
                    targetPos = hovPos;
                    // Closure preview: cursor is over the first vertex and we have ≥3 placed.
                    if (polygon.Count >= 3
                        && hover.kind == polygon[0].kind
                        && hover.refIndex == polygon[0].refIndex)
                    {
                        closingImminent = true;
                    }
                }
                else
                {
                    targetPos = cursor;
                }
                if (math.lengthsq(targetPos - lastPos) > 0.01f)
                {
                    buf.DrawLine(
                        closingImminent ? kColAreaPreviewClose : kColAreaPreview,
                        new Line3.Segment(lastPos, targetPos),
                        kAreaPreviewWidth);
                }
            }

            // 4. Placed vertices — white filled dots on top of contour.
            for (int i = 0; i < polygon.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandOutline,
                    fillColor: kColAreaPlacedFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: polygon[i].position,
                    diameter: kAreaPlacedDotDiameter);
            }

            // 5. Start-vertex closure ring — bright halo around the first vertex once 3+ placed.
            if (polygon.Count >= 3)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaPreviewClose,
                    fillColor: kColTransparent,
                    outlineWidth: kAreaStartRingWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: polygon[0].position,
                    diameter: kAreaStartRingDiameter);
            }

            // 6. Hover emphasis — overlay a brighter, larger dot at the hovered candidate.
            if (hover.IsValid && _tool.TryGetAreaAnchorPos(hover, out var hp))
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandOutline,
                    fillColor: kColAreaHoverFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: hp,
                    diameter: kAreaHoverDotDiameter);
            }
        }

        /// <summary>Draw a small red dot at the intersection point. Projected on terrain so it
        /// stays visible on slopes.</summary>
        private static void DrawIntersectionMarker(OverlayRenderSystem.Buffer buf, float3 p)
        {
            buf.DrawCircle(
                outlineColor: kColIntersectionOutline,
                fillColor: kColIntersection,
                outlineWidth: kIntersectionDotOutlineWidth,
                styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                direction: new float2(0f, 1f),
                position: p,
                diameter: kIntersectionDotDiameter);
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
