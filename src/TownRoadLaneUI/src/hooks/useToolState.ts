import { bindValue, useValue, trigger } from "cs2/api";

// Mirrors PanelStateVM published by the C# `TownRoadLaneUISystem`. The binding is typed
// through GenericUIWriter, so field names are the contract. C# pushes a new object only when
// a hash of the marking buffers changes. Screen-space popover anchors travel on a separate
// per-frame binding (positionRegistry.ts).

export interface SegmentVM {
  lineIndex: number;
  segmentIndex: number;   // dense per-line index in buffer order, hidden segments included
  tStart: number;
  tEnd: number;
  visible: boolean;
  // Starts as the line's style; the user can override single segments in the popover.
  style: number;
  lengthM: number;        // approximate chord length in metres
}

export interface LineVM {
  lineIndex: number;
  style: number;          // MarkingStyle enum value
  // Curvature in integer percent: 0 is a straight chord, 50 the default arc (pull factor
  // 0.4), 100 the roundest (pull factor 0.8).
  curv: number;
  segments: SegmentVM[];
}

// A closed polygon area on the selected node. Lines crossing the area cut it into pieces;
// pieceCount and visiblePieces count those.
export interface AreaVM {
  areaIndex: number;
  styleId: number;        // index into the C# kStyleSurfaceNames catalogue
  visible: boolean;
  vertexCount: number;
  pieceCount: number;
  visiblePieces: number;
}

// Mirrors MarkingNodeToolSystem.State.
export const TOOL_STATE = {
  Default: 0,
  NodeSelected: 1,
  SourceSelected: 2,
  AreaSelecting: 3,
} as const;

export interface ToolStateVM {
  isActive: boolean;
  // TOOL_STATE value. In AreaSelecting the panel shows the contour progress and a cancel
  // button.
  toolState: number;
  // Vertices collected so far in the area contour being drawn (AreaSelecting only).
  areaVertexCount: number;
  // Fill style for the next area the user closes (cycled by U or set in the panel).
  currentAreaStyle: number;
  selectedNodeIndex: number; // -1 when no node is selected
  currentStyle: number;    // style for the next line drawn
  // Line the user clicked in the world (near its Bezier, not on a dot). lastClickedTick is
  // bumped on every click, even on the same line, and the panel expands the matching row
  // when it changes.
  lastClickedLine: number;
  lastClickedTick: number;
  // Line under the cursor in the world, -1 for none. The panel highlights its row, the
  // counterpart of cmdSetHoveredLine.
  hoveredLineInGame: number;
  // Area whose piece is under the cursor in the world. Lines and dots win when both match.
  hoveredAreaInGame: number;
  // The selected node carries MarkingOverride{All}: its vanilla markings are hidden
  // regardless of user lines.
  vanillaHidden: boolean;
  lines: LineVM[];
  areas: AreaVM[];
}

const EMPTY: ToolStateVM = {
  isActive: false,
  toolState: 0,
  areaVertexCount: 0,
  currentAreaStyle: 0,
  selectedNodeIndex: -1,
  currentStyle: 0,
  lastClickedLine: -1,
  lastClickedTick: 0,
  hoveredLineInGame: -1,
  hoveredAreaInGame: -1,
  vanillaHidden: false,
  lines: [],
  areas: [],
};

const STATE_BINDING = bindValue<ToolStateVM>("TownRoadLane", "GetPanelState", EMPTY);

export const useToolState = (): ToolStateVM => {
  const state = useValue(STATE_BINDING);
  // GenericUIWriter always writes every field and C# initialises the arrays, so only a
  // null or undefined push needs guarding.
  if (!state) return EMPTY;
  return {
    ...state,
    lines: Array.isArray(state.lines) ? state.lines : [],
    areas: Array.isArray(state.areas) ? state.areas : [],
  };
};

// Commands to C#.

export const cmdToggleSegment = (lineIndex: number, segmentIndex: number) => {
  trigger("TownRoadLane", "ToggleSegment", lineIndex, segmentIndex);
};

export const cmdSetLineStyle = (lineIndex: number, style: number) => {
  trigger("TownRoadLane", "SetLineStyle", lineIndex, style);
};

// Per-segment style override; the line's own style stays unchanged.
export const cmdSetSegmentStyle = (lineIndex: number, segmentIndex: number, style: number) => {
  trigger("TownRoadLane", "SetSegmentStyle", lineIndex, segmentIndex, style);
};

export const cmdDeleteLine = (lineIndex: number) => {
  trigger("TownRoadLane", "DeleteLine", lineIndex);
};

// percent is 0..100; C# maps it onto the Bezier pull factor range 0..0.8.
export const cmdSetLineCurvature = (lineIndex: number, percent: number) => {
  trigger("TownRoadLane", "SetLineCurvature", lineIndex, percent);
};

// Toggles the "hide vanilla markings" override on the selected node. Works with no lines
// drawn.
export const cmdToggleVanillaMarkings = () => {
  trigger("TownRoadLane", "ToggleVanillaMarkings");
};

// Toggles the marking tool, same as Ctrl+M. Used by the toolbar button in GameTopLeft.
export const cmdActivateTool = () => {
  trigger("TownRoadLane", "ActivateTool");
};

// Style for the next line drawn; the panel counterpart of the Y hotkey.
export const cmdSetCurrentStyle = (style: number) => {
  trigger("TownRoadLane", "SetCurrentStyle", style);
};

// Fill style for the next area closed; the panel counterpart of the U hotkey.
export const cmdSetCurrentAreaStyle = (styleId: number) => {
  trigger("TownRoadLane", "SetCurrentAreaStyle", styleId);
};

// Switches between line drawing (NodeSelected) and area drawing (AreaSelecting), like the
// A hotkey. Leaving area mode drops an unfinished contour.
export const cmdToggleAreaMode = () => {
  trigger("TownRoadLane", "ToggleAreaMode");
};

export const cmdSetAreaStyle = (areaIndex: number, styleId: number) => {
  trigger("TownRoadLane", "SetAreaStyle", areaIndex, styleId);
};

export const cmdToggleAreaVisible = (areaIndex: number) => {
  trigger("TownRoadLane", "ToggleAreaVisible", areaIndex);
};

// The area's pieces and vanilla Area entities go away on the next tick.
export const cmdDeleteArea = (areaIndex: number) => {
  trigger("TownRoadLane", "DeleteArea", areaIndex);
};

// Removes all lines and areas from the selected node and clears the vanilla-hide override.
export const cmdResetNode = () => {
  trigger("TownRoadLane", "ResetNode");
};

// Line row hovered in the panel; MarkingOverlaySystem draws that line thicker and brighter
// on the road. Pass -1 on leave.
export const cmdSetHoveredLine = (lineIndex: number) => {
  trigger("TownRoadLane", "SetHoveredLine", lineIndex);
};

// Like cmdSetHoveredLine, but highlights a single segment. Pass (-1, -1) on leave.
export const cmdSetHoveredSegment = (lineIndex: number, segmentIndex: number) => {
  trigger("TownRoadLane", "SetHoveredSegment", lineIndex, segmentIndex);
};

// Area counterpart of cmdSetHoveredLine: the overlay outlines every piece of the area.
export const cmdSetHoveredArea = (areaIndex: number) => {
  trigger("TownRoadLane", "SetHoveredArea", areaIndex);
};

// Passes the row's own index, and C# clears the hover only if that index still holds it:
// cohtml can deliver the next row's mouseenter before this row's mouseleave.
export const cmdClearHoveredArea = (areaIndex: number) => {
  trigger("TownRoadLane", "ClearHoveredArea", areaIndex);
};
