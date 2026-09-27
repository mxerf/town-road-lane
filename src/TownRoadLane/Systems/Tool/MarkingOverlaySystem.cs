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
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.UI;
using static TownRoadLane.Systems.Tool.OverlayPalette;
using MathUtils = Colossal.Mathematics.MathUtils;

namespace TownRoadLane.Systems.Tool
{
    /// <summary>
    /// Overlay of the marking tool: node rings, endpoint and corner dots, crossing markers,
    /// hover and hidden-segment traces, the drag preview, and the area-mode contour. Idle unless
    /// <see cref="MarkingNodeToolSystem"/> is the active tool. Everything is drawn through the
    /// vanilla <see cref="OverlayRenderSystem"/>; colours and sizes live in
    /// <see cref="OverlayPalette"/>.
    ///
    /// Overlay design: the user is here to judge the painted markings, so anything drawn on top
    /// competes with them. Visible committed lines get no overlay curve; hidden segments get a
    /// thin red ghost (otherwise there is no way to see them); hover gets a thin cyan trace.
    /// </summary>
    public partial class MarkingOverlaySystem : GameSystemBase
    {
        // Height above the terrain from which a marker counts as being on an elevated deck.
        private const float kElevatedMinHeight = 0.75f;
        // Minimum squared distance for a preview line to the cursor to be worth drawing.
        private const float kMinPreviewLengthSq = 0.01f;

        private ToolSystem _toolSystem;
        private MarkingNodeToolSystem _tool;
        private OverlayRenderSystem _overlayRenderSystem;
        private TownRoadLaneUISystem _uiSystem;
        private TerrainSystem _terrainSystem;
        private EntityQuery _nodesWithPairsQuery;
        // Refreshed every update; DotStyle uses it to choose projected or absolute drawing.
        private TerrainHeightData _heightData;

        // Per-frame scratch buffers, reused to avoid allocations.
        private readonly List<float3> _areaContourScratch = new List<float3>();
        // (edge, gapIndex) of every dot that anchors a MarkingLine on the selected node.
        private readonly HashSet<(Entity, int)> _connectedScratch = new HashSet<(Entity, int)>();

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

        protected override void OnUpdate()
        {
            if (_toolSystem.activeTool != _tool) return;

            _heightData = _terrainSystem.GetHeightData();
            var buf = _overlayRenderSystem.GetBuffer(out JobHandle deps);
            // Drawing happens on the main thread, so jobs still writing to the buffer finish first.
            deps.Complete();
            Draw(buf);
            _overlayRenderSystem.AddBufferWriter(Dependency);
        }

        /// <summary>Style for a dot or ring at the given position. At ground level it is
        /// <see cref="OverlayRenderSystem.StyleFlags.Projected"/>, so the marker follows terrain on
        /// slopes. On elevated decks (bridges, ramps) projection would drop the marker to the
        /// ground below, so those draw at the true 3D position.</summary>
        private OverlayRenderSystem.StyleFlags DotStyle(float3 pos)
        {
            float ground = TerrainUtils.SampleHeight(ref _heightData, pos);
            return pos.y - ground > kElevatedMinHeight
                ? (OverlayRenderSystem.StyleFlags)0
                : OverlayRenderSystem.StyleFlags.Projected;
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

            var node = _tool.SelectedNode;

            // Hovered area: UI hover wins over the cursor inside an area, as for lines.
            int hoveredArea = _uiSystem.UIHoveredAreaIndex;
            if (hoveredArea < 0) hoveredArea = _tool.HoveredAreaInGame;
            if (hoveredArea >= 0 && node != Entity.Null)
                DrawHoveredAreaOutline(buf, node, hoveredArea);

            DrawCommittedLines(buf, node, endpoints);
            DrawDragPreview(buf, endpoints);
            DrawEndpointDots(buf, node, endpoints);

            // Corner anchors. Not clickable in line mode (only area mode uses them), so they are
            // drawn smaller and with less contrast than lane endpoints.
            var corners = _tool.CornerAnchors;
            if (corners != null)
            {
                for (int i = 0; i < corners.Count; i++)
                    DrawDot(buf, corners[i].position, kCornerDotDiameter, kColCornerFill, kColCornerOutline, kCornerDotOutlineWidth);
            }
        }

        /// <summary>
        /// Committed lines and crossing markers (bottom layer). Per segment:
        ///   visible, not hovered: nothing (the road paint shows it)
        ///   visible, hovered:     thin cyan trace
        ///   hidden, not hovered:  thin red ghost
        ///   hidden, hovered:      brighter red ghost
        /// Crossing markers are drawn at inner segment boundaries regardless of hover.
        ///
        /// Hover sources: a segment hovered in the UI popover lights only that segment; a line
        /// hovered in the UI panel (or, failing that, under the cursor in the world) lights all of
        /// its segments.
        /// </summary>
        private void DrawCommittedLines(OverlayRenderSystem.Buffer buf, Entity node, IReadOnlyList<MarkingEndpoint> endpoints)
        {
            if (node == Entity.Null || !EntityManager.HasBuffer<MarkingLine>(node) || !EntityManager.HasBuffer<MarkingSegment>(node))
                return;

            int hoveredLine = _uiSystem.UIHoveredLineIndex;
            if (hoveredLine < 0) hoveredLine = _tool.HoveredLineInGame;
            int hoveredSegLine = _uiSystem.UIHoveredSegmentLineIndex;
            int hoveredSegIdx = _uiSystem.UIHoveredSegmentIndex;

            var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
            var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
            for (int l = 0; l < lines.Length; l++)
            {
                if (!MarkingCurveBuilder.TryBuild(endpoints, lines[l], out var full)) continue;
                bool isLineHighlighted = l == hoveredLine;
                // The UI numbers segments 0..K-1 within each line, not by buffer index.
                int perLineCounter = -1;
                for (int s = 0; s < segs.Length; s++)
                {
                    var seg = segs[s];
                    if (seg.lineIndex != l) continue;
                    perLineCounter++;
                    bool isThisSegmentHovered = l == hoveredSegLine && perLineCounter == hoveredSegIdx;

                    if (isLineHighlighted || isThisSegmentHovered)
                    {
                        Color color = seg.visible ? kColHighlightedCurve : kColHighlightedHidden;
                        float width = isThisSegmentHovered ? kHoveredSegmentCurveWidth : kHighlightedPairCurveWidth;
                        DrawSegmentCurve(buf, full, seg, color, width);
                    }
                    else if (!seg.visible)
                    {
                        DrawSegmentCurve(buf, full, seg, kColHiddenSegment, kHiddenSegmentWidth);
                    }

                    if (IsInnerBoundary(seg.tStart))
                        DrawIntersectionMarker(buf, MathUtils.Position(full, seg.tStart));
                    if (IsInnerBoundary(seg.tEnd))
                        DrawIntersectionMarker(buf, MathUtils.Position(full, seg.tEnd));
                }
            }
        }

        private static bool IsInnerBoundary(float t) => t > 0.001f && t < 0.999f;

        private static void DrawSegmentCurve(OverlayRenderSystem.Buffer buf, Bezier4x3 line, MarkingSegment seg, Color color, float width)
            => buf.DrawCurve(color, MathUtils.Cut(line, new float2(seg.tStart, seg.tEnd)), width);

        /// <summary>Drag preview from the source dot to the hovered dot, or to the cursor.</summary>
        private void DrawDragPreview(OverlayRenderSystem.Buffer buf, IReadOnlyList<MarkingEndpoint> endpoints)
        {
            int sourceIdx = _tool.SourceEndpointIndex;
            int hoverIdx = _tool.HoveredEndpointIndex;
            if (sourceIdx < 0 || sourceIdx >= endpoints.Count) return;

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
                if (math.lengthsq(to - src.position) > kMinPreviewLengthSq)
                    buf.DrawLine(kColPreviewCurve, new Line3.Segment(src.position, to), kPreviewCurveWidth);
            }
        }

        /// <summary>Drag-preview curve. Uses <see cref="MarkingCurveBuilder"/> with the same
        /// starting pull factor a new line gets, so the preview matches the committed line.</summary>
        private static Bezier4x3 BuildSmoothCurve(float3 a, float2 ta, float3 b, float2 tb)
            => MarkingCurveBuilder.Build(a, ta, b, tb, MarkingCurveBuilder.AdaptivePullFactor(a, ta, b, tb));

        /// <summary>Endpoint dots: free ones as hollow rings in the edge colour, connected ones
        /// filled with a white core, the source dot solid white inside a faint halo, the hovered
        /// dot solid and slightly larger.</summary>
        private void DrawEndpointDots(OverlayRenderSystem.Buffer buf, Entity node, IReadOnlyList<MarkingEndpoint> endpoints)
        {
            int sourceIdx = _tool.SourceEndpointIndex;
            int hoverIdx = _tool.HoveredEndpointIndex;

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
                    DrawDot(buf, ep.position, kSourceHaloDiameter, kColTransparent, kColSourceHalo, kSourceHaloOutlineWidth);
                }
                else if (i == hoverIdx)
                {
                    fill = WithAlpha(edgeColor, 1.00f);
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
                    fill = WithAlpha(edgeColor, kDotFreeFillAlpha);
                    outline = WithAlpha(edgeColor, kDotFreeOutlineAlpha);
                }

                DrawDot(buf, ep.position, diameter, fill, outline, outlineWidth);

                if (connected && i != sourceIdx)
                    DrawDot(buf, ep.position, kDotConnectedCoreDiameter, kColDotConnectedCore, kColTransparent, 0f);
            }
        }

        private static Color WithAlpha(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha);

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
                DrawAreaCandidate(buf, endpoints[i].position);
            for (int i = 0; i < corners.Count; i++)
                DrawAreaCandidate(buf, corners[i].position);
            for (int i = 0; i < crossings.Count; i++)
                DrawAreaCandidate(buf, crossings[i].position);

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
                    closingImminent = polygon.Count >= 3
                        && hover.kind == polygon[0].kind
                        && hover.refIndex == polygon[0].refIndex;
                }
                else
                {
                    targetPos = cursor;
                }
                if (math.lengthsq(targetPos - lastPos) > kMinPreviewLengthSq)
                {
                    buf.DrawLine(
                        closingImminent ? kColAreaPreviewClose : kColAreaPreview,
                        new Line3.Segment(lastPos, targetPos),
                        kAreaPreviewWidth);
                }
            }

            // 4. Placed vertices, on top of the contour.
            for (int i = 0; i < polygon.Count; i++)
                DrawDot(buf, polygon[i].position, kAreaPlacedDotDiameter, kColAreaPlacedFill, kColAreaCandOutline, kAreaCandDotOutlineWidth);

            // 5. Ring around the start vertex once clicking it would close the polygon.
            if (polygon.Count >= 3)
                DrawDot(buf, polygon[0].position, kAreaStartRingDiameter, kColTransparent, kColAreaPreviewClose, kAreaStartRingWidth);

            // 6. Hovered candidate, on top of everything.
            if (hover.IsValid && _tool.TryGetAreaAnchorPos(hover, out var hp))
                DrawDot(buf, hp, kAreaHoverDotDiameter, kColAreaHoverFill, kColAreaCandOutline, kAreaCandDotOutlineWidth);
        }

        private void DrawAreaCandidate(OverlayRenderSystem.Buffer buf, float3 position)
            => DrawDot(buf, position, kAreaCandDotDiameter, kColAreaCandFill, kColAreaCandRing, kAreaCandDotOutlineWidth);

        private void DrawIntersectionMarker(OverlayRenderSystem.Buffer buf, float3 p)
            => DrawDot(buf, p, kIntersectionDotDiameter, kColIntersection, kColIntersectionOutline, kIntersectionDotOutlineWidth);

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
            DrawDot(buf, pos, diameter, kColTransparent, ringColor, outlineWidth);
        }

        /// <summary>Flat circle at <paramref name="position"/>, projected onto the terrain unless
        /// it sits on an elevated deck (see <see cref="DotStyle"/>).</summary>
        private void DrawDot(OverlayRenderSystem.Buffer buf, float3 position, float diameter, Color fill, Color outline, float outlineWidth)
        {
            buf.DrawCircle(
                outlineColor: outline,
                fillColor: fill,
                outlineWidth: outlineWidth,
                styleFlags: DotStyle(position),
                direction: new float2(0f, 1f),
                position: position,
                diameter: diameter);
        }
    }
}
