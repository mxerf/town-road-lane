using Unity.Entities;
using UnityEngine;

namespace TownRoadLane.Systems.Tool
{
    /// <summary>
    /// Colours and sizes (metres) of the marking tool overlay drawn by
    /// <see cref="MarkingOverlaySystem"/>, whose summary explains the overlay design.
    /// </summary>
    internal static class OverlayPalette
    {
        public static readonly Color kColTransparent = new Color(0f, 0f, 0f, 0f);

        // Hidden segments (visible ones get no overlay curve).
        public const float kHiddenSegmentWidth = 0.14f;
        public static readonly Color kColHiddenSegment = new Color(1.00f, 0.35f, 0.35f, 0.30f);

        // Hover highlight, from the UI panel or the cursor over a line in the world.
        public const float kHighlightedPairCurveWidth = 0.08f;
        // Thicker for a hovered segment, so it stands out even when the whole line is highlighted.
        public const float kHoveredSegmentCurveWidth = kHighlightedPairCurveWidth * 1.8f;
        public static readonly Color kColHighlightedCurve = new Color(0.40f, 0.90f, 1.00f, 0.85f);
        public static readonly Color kColHighlightedHidden = new Color(1.00f, 0.55f, 0.55f, 0.65f);

        // Drag preview while creating a line: thin, semi-transparent white, so the markings
        // underneath stay visible.
        public const float kPreviewCurveWidth = 0.10f;
        public static readonly Color kColPreviewCurve = new Color(1.00f, 1.00f, 1.00f, 0.55f);

        // Endpoint dots. Each road approach gets its own colour so the user can tell which dots
        // belong to which approach. A free dot is a hollow ring; a dot that anchors at least one
        // MarkingLine is filled and gets a small white core.
        public const float kDotDiameter = 0.65f;
        public const float kDotOutlineWidth = 0.10f;
        public const float kDotFreeFillAlpha = 0.14f;
        public const float kDotFreeOutlineAlpha = 0.95f;
        public const float kDotConnectedCoreDiameter = 0.20f;
        public static readonly Color kColDotConnectedCore = new Color(1.00f, 1.00f, 1.00f, 0.90f);
        public static readonly Color kColDotOutline = new Color(0.06f, 0.08f, 0.12f, 0.90f);
        // Source dot: the origin of the line being drawn, inside a faint halo.
        public static readonly Color kColDotFillSource = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        public static readonly Color kColDotOutlineSrc = new Color(0.10f, 0.10f, 0.10f, 1.00f);
        public const float kSourceHaloDiameter = kDotDiameter * 2.2f;
        public const float kSourceHaloOutlineWidth = 0.10f;
        public static readonly Color kColSourceHalo = new Color(1f, 1f, 1f, 0.55f);
        // Dot under the cursor.
        public const float kDotDiameterHover = 0.95f;
        public const float kDotOutlineWidthHover = 0.11f;

        // Per-edge palette: high mutual contrast, and none of the colours clash with the
        // green, red and cyan overlay accents. Indexed by edge.Index, so an edge keeps its colour.
        private static readonly Color[] kEdgePalette =
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

        public static Color EdgeDotColor(Entity edge)
        {
            // Mask off the sign bit so a negative Entity.Index can't produce a negative index.
            int idx = (edge.Index & 0x7fffffff) % kEdgePalette.Length;
            return kEdgePalette[idx];
        }

        // Crossing markers: a small red dot where two lines on the selected node cross.
        public const float kIntersectionDotDiameter = 0.32f;
        public const float kIntersectionDotOutlineWidth = 0.06f;
        public static readonly Color kColIntersection = new Color(1.00f, 0.30f, 0.30f, 0.60f);
        public static readonly Color kColIntersectionOutline = new Color(0.25f, 0.05f, 0.05f, 0.80f);

        // Corner anchors sit where the kerbs of neighbouring edges meet. OverlayRenderSystem has
        // no easy square or diamond shape, so they are told apart from lane endpoints by being
        // smaller, whitish rings with a darker outline.
        public const float kCornerDotDiameter = 0.55f;
        public const float kCornerDotOutlineWidth = 0.10f;
        public static readonly Color kColCornerFill = new Color(0.95f, 0.95f, 0.95f, 0.55f);
        public static readonly Color kColCornerOutline = new Color(0.15f, 0.20f, 0.25f, 0.90f);

        // Area mode, with its own palette: candidates are hollow warm-yellow rings, the hovered
        // candidate solid bright yellow, the placed contour solid white, the preview edge thin
        // white, and the edge that would close the polygon bright green.
        public const float kAreaCandDotDiameter = 0.90f;
        public const float kAreaCandDotOutlineWidth = 0.12f;
        public static readonly Color kColAreaCandFill = new Color(1.00f, 0.85f, 0.20f, 0.14f);
        public static readonly Color kColAreaCandRing = new Color(1.00f, 0.85f, 0.20f, 0.95f);
        public static readonly Color kColAreaCandOutline = new Color(0.20f, 0.16f, 0.04f, 0.95f);
        public static readonly Color kColAreaHoverFill = new Color(1.00f, 0.95f, 0.45f, 1.00f);
        public const float kAreaHoverDotDiameter = 1.15f;
        public const float kAreaPlacedDotDiameter = 1.10f;
        public static readonly Color kColAreaPlacedFill = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        public const float kAreaContourWidth = 0.16f;
        public static readonly Color kColAreaContour = new Color(1.00f, 1.00f, 1.00f, 0.90f);
        public const float kAreaPreviewWidth = 0.13f;
        public static readonly Color kColAreaPreview = new Color(1.00f, 1.00f, 1.00f, 0.55f);
        // Also used for the ring around the start vertex once the polygon can be closed.
        public static readonly Color kColAreaPreviewClose = new Color(0.40f, 1.00f, 0.55f, 0.95f);
        public const float kAreaStartRingDiameter = 1.70f;
        public const float kAreaStartRingWidth = 0.14f;

        // Node rings (hovered node, nodes with custom markings). Larger than the node so they
        // read as a halo and don't compete with the dots as click targets.
        public const float kNodeHoverDiameter = 5.5f;
        public const float kNodeHoverOutlineWidth = 0.22f;
        public const float kNodeHasPairsDiameter = 3.6f;
        public const float kNodeHasPairsOutlineWidth = 0.12f;
        public static readonly Color kColNodeHoverRing = new Color(0.30f, 0.95f, 1.00f, 0.70f);
        public static readonly Color kColNodeHasPairsRing = new Color(0.45f, 1.00f, 0.55f, 0.55f);
    }
}
