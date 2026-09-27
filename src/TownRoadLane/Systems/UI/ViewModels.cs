using System;

namespace TownRoadLane
{
    // Binding payloads. GenericUIWriter serializes field names verbatim, so they are camelCase
    // and must match the interfaces in useToolState.ts and usePinnedStyles.ts.

    /// <summary>Pinned style ids for the UI dropdowns.</summary>
    public class PinnedStylesVM
    {
        public int[] lineStyles = Array.Empty<int>();
        public int[] areaStyles = Array.Empty<int>();
    }

    /// <summary>Everything the panel renders except the camera-dependent popover anchors
    /// (those travel as <see cref="SegmentPointVM"/>).</summary>
    public class PanelStateVM
    {
        public bool isActive;
        public int toolState;
        public int areaVertexCount;
        public int currentAreaStyle;
        public int selectedNodeIndex = -1;
        public int currentStyle;
        public bool vanillaHidden;
        public int lastClickedLine = -1;
        public int lastClickedTick;
        public int hoveredLineInGame = -1;
        public int hoveredAreaInGame = -1;
        public LineVM[] lines = Array.Empty<LineVM>();
        public AreaVM[] areas = Array.Empty<AreaVM>();
    }

    public class LineVM
    {
        public int lineIndex;
        public int style;
        public int curv;
        public SegmentVM[] segments = Array.Empty<SegmentVM>();
    }

    public class SegmentVM
    {
        public int lineIndex;
        public int segmentIndex; // dense per-line counter, stable within one topology pass
        public float tStart;
        public float tEnd;
        public bool visible;
        public int style;
        public float lengthM;
    }

    public class AreaVM
    {
        public int areaIndex;
        public int styleId;
        public bool visible;
        public int vertexCount;
        public int pieceCount;
        public int visiblePieces;
    }

    /// <summary>Screen anchor of one in-world popover (CSS px, origin top-left). Segment
    /// anchors carry (lineIndex, segmentIndex); area anchors carry areaIndex with the segment
    /// fields at -1. A popover without an anchor is hidden.</summary>
    public class SegmentPointVM
    {
        public int lineIndex;
        public int segmentIndex;
        public int areaIndex = -1;
        public float x;
        public float y;
        // Camera-distance popover scale in [0.65, 1], see PopoverScale.
        public float scale = 1f;
    }
}
