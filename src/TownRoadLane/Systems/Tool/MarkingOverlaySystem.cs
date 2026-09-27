using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Net;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TownRoadLane
{
    /// <summary>
    /// Overlay of the marking tool: node rings, endpoint and corner dots, crossing markers,
    /// hover and hidden-segment traces, the drag preview, and the area-mode contour. Idle unless
    /// <see cref="MarkingNodeToolSystem"/> is the active tool. Everything is drawn through the
    /// vanilla <see cref="OverlayRenderSystem"/>.
    ///
    /// Overlay design: the user is here to judge the painted markings, so anything drawn on top
    /// competes with them. Visible committed lines get no overlay curve; hidden segments get a
    /// thin red ghost (otherwise there is no way to see them); hover gets a thin cyan trace.
    /// </summary>
    public partial class MarkingOverlaySystem : GameSystemBase
    {

        private ToolSystem _toolSystem;
        private MarkingNodeToolSystem _tool;
        private OverlayRenderSystem _overlayRenderSystem;
        private TownRoadLaneUISystem _uiSystem;
        private EntityQuery _nodesWithPairsQuery;
        private TerrainSystem _terrainSystem;
        // Refreshed every update; DotStyle uses it to choose projected or absolute drawing.
        private TerrainHeightData _heightData;

        // Hidden segments (visible ones get no overlay curve).
        private const float kHiddenSegmentWidth = 0.14f;
        private static readonly Color kColHiddenSegment = new Color(1.00f, 0.35f, 0.35f, 0.30f);

        // Hover highlight, from the UI panel or the cursor over a line in the world.
        private const float kHighlightedPairCurveWidth = 0.08f;
        private static readonly Color kColHighlightedCurve = new Color(0.40f, 0.90f, 1.00f, 0.85f);
        private static readonly Color kColHighlightedHidden = new Color(1.00f, 0.55f, 0.55f, 0.65f);

        // Drag preview while creating a line: thin, semi-transparent white, so the markings
        // underneath stay visible.
        private const float kPreviewCurveWidth = 0.10f;
        private static readonly Color kColPreviewCurve = new Color(1.00f, 1.00f, 1.00f, 0.55f);

        // Endpoint dots. Each road approach gets its own colour so the user can tell which dots
        // belong to which approach. A free dot is a hollow ring; a dot that anchors at least one
        // MarkingLine is filled and gets a small white core.
        private const float kDotDiameter        = 0.65f;
        private const float kDotOutlineWidth    = 0.10f;
        private const float kDotFreeFillAlpha   = 0.14f;
        private const float kDotConnectedCoreDiameter = 0.20f;
        private static readonly Color kColDotConnectedCore = new Color(1.00f, 1.00f, 1.00f, 0.90f);
        private static readonly Color kColDotOutline      = new Color(0.06f, 0.08f, 0.12f, 0.90f);
        // Source dot: the origin of the line being drawn.
        private static readonly Color kColDotFillSource   = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        private static readonly Color kColDotOutlineSrc   = new Color(0.10f, 0.10f, 0.10f, 1.00f);
        // Dot under the cursor.
        private const float kDotDiameterHover = 0.95f;
        private const float kDotOutlineWidthHover = 0.11f;

        // Per-edge palette: high mutual contrast, and none of the colours clash with the
        // green, red and cyan overlay accents. Indexed by edge.Index, so an edge keeps its colour.
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
            // Mask off the sign bit so a negative Entity.Index can't produce a negative index.
            int idx = (edge.Index & 0x7fffffff) % kEdgePalette.Length;
            return kEdgePalette[idx];
        }
        // Crossing markers: a small red dot where two lines on the selected node cross.
        private const float kIntersectionDotDiameter     = 0.32f;
        private const float kIntersectionDotOutlineWidth = 0.06f;
        private static readonly Color kColIntersection        = new Color(1.00f, 0.30f, 0.30f, 0.60f);
        private static readonly Color kColIntersectionOutline = new Color(0.25f, 0.05f, 0.05f, 0.80f);

        // Corner anchors sit where the kerbs of neighbouring edges meet. OverlayRenderSystem has
        // no easy square or diamond shape, so they are told apart from lane endpoints by being
        // smaller, whitish rings with a darker outline.
        private const float kCornerDotDiameter      = 0.55f;
        private const float kCornerDotOutlineWidth  = 0.10f;
        private static readonly Color kColCornerFill    = new Color(0.95f, 0.95f, 0.95f, 0.55f);
        private static readonly Color kColCornerOutline = new Color(0.15f, 0.20f, 0.25f, 0.90f);

        // Area mode, with its own palette: candidates are hollow warm-yellow rings, the hovered
        // candidate solid bright yellow, the placed contour solid white, the preview edge thin
        // white, and the edge that would close the polygon bright green.
        private const float kAreaCandDotDiameter      = 0.90f;
        private const float kAreaCandDotOutlineWidth  = 0.12f;
        private static readonly Color kColAreaCandFill         = new Color(1.00f, 0.85f, 0.20f, 0.14f);
        private static readonly Color kColAreaCandRing         = new Color(1.00f, 0.85f, 0.20f, 0.95f);
        private static readonly Color kColAreaCandOutline      = new Color(0.20f, 0.16f, 0.04f, 0.95f);
        private static readonly Color kColAreaHoverFill        = new Color(1.00f, 0.95f, 0.45f, 1.00f);
        private const float kAreaHoverDotDiameter     = 1.15f;
        private const float kAreaPlacedDotDiameter    = 1.10f;
        private static readonly Color kColAreaPlacedFill       = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        private const float kAreaContourWidth         = 0.16f;
        private static readonly Color kColAreaContour          = new Color(1.00f, 1.00f, 1.00f, 0.90f);
        private const float kAreaPreviewWidth         = 0.13f;
        private static readonly Color kColAreaPreview          = new Color(1.00f, 1.00f, 1.00f, 0.55f);
        // Also used for the ring around the start vertex once the polygon can be closed.
        private static readonly Color kColAreaPreviewClose     = new Color(0.40f, 1.00f, 0.55f, 0.95f);
        private const float kAreaStartRingDiameter    = 1.70f;
        private const float kAreaStartRingWidth       = 0.14f;

        // Node rings (hovered node, nodes with custom markings). Larger than the node so they
        // read as a halo and don't compete with the dots as click targets.
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
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            // Nodes with a MarkingLine buffer, for the "has custom markings" ring. Few nodes
            // carry the buffer, so the query stays cheap.
            _nodesWithPairsQuery = GetEntityQuery(
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<MarkingLine>());
        }

        /// <summary>Style for a dot or ring at the given position. At ground level it is
        /// <see cref="OverlayRenderSystem.StyleFlags.Projected"/>, so the marker follows terrain on
        /// slopes. On elevated decks (bridges, ramps) projection would drop the marker to the
        /// ground below, so those draw at the true 3D position.</summary>
        private OverlayRenderSystem.StyleFlags DotStyle(float3 pos)
        {
            float ground = TerrainUtils.SampleHeight(ref _heightData, pos);
            return pos.y - ground > 0.75f
                ? (OverlayRenderSystem.StyleFlags)0
                : OverlayRenderSystem.StyleFlags.Projected;
        }

        protected override void OnUpdate()
        {
            if (_tool == null || _toolSystem.activeTool != _tool) return;

            _heightData = _terrainSystem.GetHeightData();
            var buf = _overlayRenderSystem.GetBuffer(out JobHandle deps);
            // Drawing happens on the main thread, so jobs still writing to the buffer finish first.
            deps.Complete();
            Draw(buf);
            _overlayRenderSystem.AddBufferWriter(Dependency);
        }

        private void Draw(OverlayRenderSystem.Buffer buf)
        {
            DrawHasPairsRings(buf, _tool.SelectedNode);

            // Only before a node is selected; after that the dots show where the user is.
            if (_tool.ToolState == MarkingToolState.Default && _tool.HoveredNode != Entity.Null)
            {
                DrawNodeRing(buf, _tool.HoveredNode, kColNodeHoverRing, kNodeHoverDiameter, kNodeHoverOutlineWidth);
            }

            // Area mode replaces the line overlay. Checked before the early return on empty
            // endpoints so an area built only from corner anchors still draws.
            if (_tool.ToolState == MarkingToolState.AreaSelecting)
            {
                DrawAreaModeOverlay(buf);
                return;
            }

            var endpoints = _tool.Endpoints;
            if (endpoints == null || endpoints.Count == 0)
                return;

            int sourceIdx = _tool.SourceEndpointIndex;
            int hoverIdx  = _tool.HoveredEndpointIndex;

            // 1. Committed lines and crossing markers (bottom layer). Per segment:
            //      visible, not hovered: nothing (the road paint shows it)
            //      visible, hovered:     thin cyan trace
            //      hidden, not hovered:  thin red ghost
            //      hidden, hovered:      brighter red ghost
            //    Crossing markers are drawn at inner segment boundaries regardless of hover.
            //
            //    Hover sources: a segment hovered in the UI popover lights only that segment; a
            //    line hovered in the UI panel (or, failing that, under the cursor in the world)
            //    lights all of its segments.
            int uiHoveredLine = _uiSystem?.UIHoveredLineIndex ?? -1;
            if (uiHoveredLine < 0) uiHoveredLine = _tool?.HoveredLineInGame ?? -1;
            int hoveredSegLine = _uiSystem?.UIHoveredSegmentLineIndex ?? -1;
            int hoveredSegIdx  = _uiSystem?.UIHoveredSegmentIndex ?? -1;

            // Hovered area: UI hover wins over the cursor inside an area, as for lines.
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
                    // The UI numbers segments 0..K-1 within each line, not by buffer index.
                    int perLineCounter = -1;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        if (seg.lineIndex != l) continue;
                        perLineCounter++;
                        bool isThisSegmentHovered = (l == hoveredSegLine && perLineCounter == hoveredSegIdx);
                        bool isHighlighted = isLineHighlighted || isThisSegmentHovered;

                        bool draw = false;
                        Color color = default;
                        float width = 0f;
                        if (isHighlighted)
                        {
                            draw = true;
                            color = seg.visible ? kColHighlightedCurve : kColHighlightedHidden;
                            // Thicker for a hovered segment, so it stands out even when the
                            // whole line is highlighted.
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

                        if (seg.tStart > 0.001f && seg.tStart < 0.999f)
                            DrawIntersectionMarker(buf, Colossal.Mathematics.MathUtils.Position(full, seg.tStart));
                        if (seg.tEnd > 0.001f && seg.tEnd < 0.999f)
                            DrawIntersectionMarker(buf, Colossal.Mathematics.MathUtils.Position(full, seg.tEnd));
                    }
                }
            }

            // 2. Drag preview from the source dot to the hovered dot, or to the cursor.
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
                    // No dot hovered: straight line to the cursor's terrain hit.
                    float3 to = _tool.CursorWorldPos;
                    if (math.lengthsq(to - src.position) > 0.01f)
                        buf.DrawLine(kColPreviewCurve, new Line3.Segment(src.position, to), kPreviewCurveWidth);
                }
            }

            // 3. Endpoint dots: free ones as hollow rings in the edge colour, connected ones
            //    filled with a white core, the source dot solid white inside a faint halo, the
            //    hovered dot solid and slightly larger.
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
                    // Halo, drawn first so the dot sits on top.
                    buf.DrawCircle(
                        outlineColor: new Color(1f, 1f, 1f, 0.55f),
                        fillColor: kColTransparent,
                        outlineWidth: 0.10f,
                        styleFlags: DotStyle(ep.position),
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
                    // Hollow ring; the faint fill keeps it readable on dark asphalt.
                    fill = new Color(edgeColor.r, edgeColor.g, edgeColor.b, kDotFreeFillAlpha);
                    outline = new Color(edgeColor.r, edgeColor.g, edgeColor.b, 0.95f);
                }

                buf.DrawCircle(
                    outlineColor: outline,
                    fillColor: fill,
                    outlineWidth: outlineWidth,
                    styleFlags: DotStyle(ep.position),
                    direction: new float2(0f, 1f),
                    position: ep.position,
                    diameter: diameter);

                if (connected && i != sourceIdx)
                {
                    buf.DrawCircle(
                        outlineColor: kColTransparent,
                        fillColor: kColDotConnectedCore,
                        outlineWidth: 0f,
                        styleFlags: DotStyle(ep.position),
                        direction: new float2(0f, 1f),
                        position: ep.position,
                        diameter: kDotConnectedCoreDiameter);
                }
            }

            // 4. Corner anchors. Not clickable in line mode (only area mode uses them), so they
            //    are drawn smaller and with less contrast than lane endpoints.
            var corners = _tool.CornerAnchors;
            if (corners != null)
            {
                for (int i = 0; i < corners.Count; i++)
                {
                    buf.DrawCircle(
                        outlineColor: kColCornerOutline,
                        fillColor: kColCornerFill,
                        outlineWidth: kCornerDotOutlineWidth,
                        styleFlags: DotStyle(corners[i].position),
                        direction: new float2(0f, 1f),
                        position: corners[i].position,
                        diameter: kCornerDotDiameter);
                }
            }
        }

        /// <summary>Drag-preview curve. Uses <see cref="MarkingCurveBuilder"/> with the same
        /// starting pull factor a new line gets, so the preview matches the committed line.</summary>
        private static Bezier4x3 BuildSmoothCurve(float3 a, float2 ta, float3 b, float2 tb)
            => MarkingCurveBuilder.Build(a, ta, b, tb, MarkingCurveBuilder.AdaptivePullFactor(a, ta, b, tb));

        /// <summary>Outlines every piece of the hovered area, the area counterpart of the line
        /// hover trace: cyan for visible pieces, red for hidden ones.</summary>
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

        // Per-frame scratch buffers, reused to avoid allocations.
        private readonly List<float3> _areaContourScratch = new List<float3>();

        // (edge, gapIndex) of every dot that anchors a MarkingLine on the selected node.
        private readonly HashSet<(Entity, int)> _connectedScratch = new HashSet<(Entity, int)>();

        /// <summary>Overlay while the user places the vertices of an area polygon. Layers are
        /// drawn back to front, as numbered below.</summary>
        private void DrawAreaModeOverlay(OverlayRenderSystem.Buffer buf)
        {
            var endpoints = _tool.Endpoints;
            var corners = _tool.CornerAnchors;
            var crossings = _tool.LineIntersections;
            var polygon = _tool.AreaPolygon;
            var hover = _tool.AreaHover;
            float3 cursor = _tool.CursorWorldPos;

            // 1. Candidates: lane endpoints, corner anchors and line crossings, all as hollow
            //    rings so the junction doesn't drown in solid dots.
            for (int i = 0; i < endpoints.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandRing,
                    fillColor: kColAreaCandFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: DotStyle(endpoints[i].position),
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
                    styleFlags: DotStyle(corners[i].position),
                    direction: new float2(0f, 1f),
                    position: corners[i].position,
                    diameter: kAreaCandDotDiameter);
            }
            for (int i = 0; i < crossings.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandRing,
                    fillColor: kColAreaCandFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: DotStyle(crossings[i].position),
                    direction: new float2(0f, 1f),
                    position: crossings[i].position,
                    diameter: kAreaCandDotDiameter);
            }

            // 2. Contour through the placed vertices. Edges that follow a line come back sampled
            //    along it, so the preview shows the arc the committed area will follow.
            _tool.BuildAreaContourPath(_areaContourScratch);
            for (int i = 1; i < _areaContourScratch.Count; i++)
            {
                buf.DrawLine(kColAreaContour,
                    new Line3.Segment(_areaContourScratch[i - 1], _areaContourScratch[i]),
                    kAreaContourWidth);
            }

            // 3. Preview edge from the last placed vertex to the hovered candidate or the cursor,
            //    green when it would close the polygon.
            if (polygon.Count > 0)
            {
                var lastPos = polygon[polygon.Count - 1].position;
                float3 targetPos;
                bool closingImminent = false;
                if (hover.IsValid && _tool.TryGetAreaAnchorPos(hover, out var hovPos))
                {
                    targetPos = hovPos;
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

            // 4. Placed vertices, on top of the contour.
            for (int i = 0; i < polygon.Count; i++)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandOutline,
                    fillColor: kColAreaPlacedFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: DotStyle(polygon[i].position),
                    direction: new float2(0f, 1f),
                    position: polygon[i].position,
                    diameter: kAreaPlacedDotDiameter);
            }

            // 5. Ring around the start vertex once clicking it would close the polygon.
            if (polygon.Count >= 3)
            {
                buf.DrawCircle(
                    outlineColor: kColAreaPreviewClose,
                    fillColor: kColTransparent,
                    outlineWidth: kAreaStartRingWidth,
                    styleFlags: DotStyle(polygon[0].position),
                    direction: new float2(0f, 1f),
                    position: polygon[0].position,
                    diameter: kAreaStartRingDiameter);
            }

            // 6. Hovered candidate, on top of everything.
            if (hover.IsValid && _tool.TryGetAreaAnchorPos(hover, out var hp))
            {
                buf.DrawCircle(
                    outlineColor: kColAreaCandOutline,
                    fillColor: kColAreaHoverFill,
                    outlineWidth: kAreaCandDotOutlineWidth,
                    styleFlags: DotStyle(hp),
                    direction: new float2(0f, 1f),
                    position: hp,
                    diameter: kAreaHoverDotDiameter);
            }
        }

        private void DrawIntersectionMarker(OverlayRenderSystem.Buffer buf, float3 p)
        {
            buf.DrawCircle(
                outlineColor: kColIntersectionOutline,
                fillColor: kColIntersection,
                outlineWidth: kIntersectionDotOutlineWidth,
                styleFlags: DotStyle(p),
                direction: new float2(0f, 1f),
                position: p,
                diameter: kIntersectionDotDiameter);
        }

        /// <summary>Faint ring around every node with at least one MarkingLine, so customised
        /// junctions are easy to spot. Skips the selected node, whose dots already mark it.</summary>
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
                styleFlags: DotStyle(pos),
                direction: new float2(0f, 1f),
                position: pos,
                diameter: diameter);
        }
    }
}
