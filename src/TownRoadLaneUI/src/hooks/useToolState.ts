import { bindValue, useValue, trigger } from "cs2/api";

// Mirrors PanelStateVM published by C# `TownRoadLaneUISystem` (typed binding via
// GenericUIWriter — field names ARE the contract). C# pushes a fresh object only
// when a content hash of the authoritative buffers changes; React resync is
// automatic via useValue. Screen-space popover anchors travel on a separate
// per-frame binding — see positionRegistry.ts.

export interface SegmentVM {
  lineIndex: number;
  segmentIndex: number;   // dense per-line counter (0..K-1 for visible-or-not order in buffer)
  tStart: number;
  tEnd: number;
  visible: boolean;
  // Per-segment style (Stage 5d) — defaults from MarkingLine.style at creation but the user
  // can override individual pieces via the popover.
  style: number;
  lengthM: number;        // chord length of the segment in metres (approximated)
}

export interface LineVM {
  lineIndex: number;
  style: number;          // matches the MarkingStyle enum on C# side
  // Curvature stepper value, integer percent 0..100. 0 = straight chord, 50 = the
  // default arc (pull factor 0.4), 100 = maximum roundness (pull factor 0.8).
  curv: number;
  segments: SegmentVM[];
}

// One user-closed polygon area on the selected node. pieceCount/visiblePieces
// reflect the post-split state (lines crossing the area cut it into pieces).
export interface AreaVM {
  areaIndex: number;
  styleId: number;        // index into the C#-side kStyleSurfaceNames catalogue
  visible: boolean;
  vertexCount: number;
  pieceCount: number;
  visiblePieces: number;
}

// Mirrors MarkingNodeToolSystem.State — drives the panel's mode UI.
export const TOOL_STATE = {
  Default: 0,
  NodeSelected: 1,
  SourceSelected: 2,
  AreaSelecting: 3,
} as const;

export interface ToolStateVM {
  isActive: boolean;       // tool currently the active tool
  // Tool sub-state (see TOOL_STATE). AreaSelecting means the user is collecting
  // polygon vertices; the panel shows draft progress + a cancel affordance.
  toolState: number;
  // Vertices collected so far in the running area contour (AreaSelecting only).
  areaVertexCount: number;
  // Fill style for the NEXT area the user closes (cycled by U / set by the panel).
  currentAreaStyle: number;
  selectedNodeIndex: number; // -1 when no node selected
  currentStyle: number;    // enum value
  // Reverse hover-bridge: lineIndex of the line the user clicked on in the game world
  // (NodeSelected state, not a dot click — proximity to the line's Bezier). React watches
  // lastClickedTick (monotonic, bumped on every click even if the same line) and
  // auto-expands the matching accordion row.
  lastClickedLine: number;
  lastClickedTick: number;
  // Game→UI hover (Phase B5): index of the line the cursor is currently over in the
  // world (-1 when nothing). React mirrors this to highlight the matching panel row,
  // making the bridge symmetric — UI hover lights up the line in the world, and
  // world hover lights up the row in the panel.
  hoveredLineInGame: number;
  // True when the selected node carries the MarkingOverride{All} component — vanilla
  // markings on the node are suppressed regardless of user lines.
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
  vanillaHidden: false,
  lines: [],
  areas: [],
};

const STATE_BINDING = bindValue<ToolStateVM>("TownRoadLane", "GetPanelState", EMPTY);

export const useToolState = (): ToolStateVM => {
  const state = useValue(STATE_BINDING);
  // The typed binding guarantees the full field set (GenericUIWriter serializes
  // every VM field, arrays initialised empty on the C# side). Guard only against
  // a wholesale null/undefined push so consumers can trust ToolStateVM invariants.
  if (!state) return EMPTY;
  return {
    ...state,
    lines: Array.isArray(state.lines) ? state.lines : [],
    areas: Array.isArray(state.areas) ? state.areas : [],
  };
};

// --- Commands (push, React → C#) ---

export const cmdToggleSegment = (lineIndex: number, segmentIndex: number) => {
  trigger("TownRoadLane", "ToggleSegment", lineIndex, segmentIndex);
};

export const cmdSetLineStyle = (lineIndex: number, style: number) => {
  trigger("TownRoadLane", "SetLineStyle", lineIndex, style);
};

// Per-segment style override (Stage 5d). Same arg layout as cmdToggleSegment,
// plus the new style value. The line-level default style stays unchanged.
export const cmdSetSegmentStyle = (lineIndex: number, segmentIndex: number, style: number) => {
  trigger("TownRoadLane", "SetSegmentStyle", lineIndex, segmentIndex, style);
};

export const cmdDeleteLine = (lineIndex: number) => {
  trigger("TownRoadLane", "DeleteLine", lineIndex);
};

// Set the line's curvature from the panel stepper. percent ∈ [0, 100]; C# maps
// it onto the Bezier pull-factor range [0, 0.8] (50% = the 0.4 default arc).
export const cmdSetLineCurvature = (lineIndex: number, percent: number) => {
  trigger("TownRoadLane", "SetLineCurvature", lineIndex, percent);
};

// Toggle the standalone "hide vanilla markings" override on the selected node.
// Works with zero lines drawn — this is the pure hide switch.
export const cmdToggleVanillaMarkings = () => {
  trigger("TownRoadLane", "ToggleVanillaMarkings");
};

// Toggle the marking tool active/inactive — same as Ctrl+M or the settings
// button. Triggered from the toolbar button in GameTopLeft.
export const cmdActivateTool = () => {
  trigger("TownRoadLane", "ActivateTool");
};

// Style for the NEXT line drawn — panel dropdown mirror of the Y hotkey cycle.
export const cmdSetCurrentStyle = (style: number) => {
  trigger("TownRoadLane", "SetCurrentStyle", style);
};

// Fill style for the NEXT area closed — panel dropdown mirror of the U hotkey.
export const cmdSetCurrentAreaStyle = (styleId: number) => {
  trigger("TownRoadLane", "SetCurrentAreaStyle", styleId);
};

// Switch between line drawing (NodeSelected) and polygon-area collection
// (AreaSelecting) — panel mode buttons, mirrors the A hotkey. Leaving area
// mode drops any partially collected contour without committing.
export const cmdToggleAreaMode = () => {
  trigger("TownRoadLane", "ToggleAreaMode");
};

// Change the fill style of a committed area (panel dropdown per area row).
export const cmdSetAreaStyle = (areaIndex: number, styleId: number) => {
  trigger("TownRoadLane", "SetAreaStyle", areaIndex, styleId);
};

// Hide/show a committed area without deleting it.
export const cmdToggleAreaVisible = (areaIndex: number) => {
  trigger("TownRoadLane", "ToggleAreaVisible", areaIndex);
};

// Delete a committed area (its pieces + vanilla Area entities follow next tick).
export const cmdDeleteArea = (areaIndex: number) => {
  trigger("TownRoadLane", "DeleteArea", areaIndex);
};

// Full reset of the selected node: all lines, all areas, and the vanilla-hide
// override — back to stock game markings in one click.
export const cmdResetNode = () => {
  trigger("TownRoadLane", "ResetNode");
};

// Tell C# which line row the user is currently hovering over in the panel.
// MarkingOverlaySystem reads this and draws that line on the road thicker +
// brighter so the user can correlate UI row ↔ physical line. Pass -1 on leave.
export const cmdSetHoveredLine = (lineIndex: number) => {
  trigger("TownRoadLane", "SetHoveredLine", lineIndex);
};

// Per-segment hover (C3). Same idea as cmdSetHoveredLine but scoped to a
// specific segment of a specific line — overlay highlights ONLY that segment
// (brighter, slightly thicker) rather than the whole line. Pass (-1, -1) on
// leave. Used by SegmentPopover hover handlers.
export const cmdSetHoveredSegment = (lineIndex: number, segmentIndex: number) => {
  trigger("TownRoadLane", "SetHoveredSegment", lineIndex, segmentIndex);
};
