import { bindValue, useValue, trigger } from "cs2/api";

// Mirrors the JSON shape published by C# `TownRoadLaneUISystem`. Whenever the user
// selects a different node OR changes anything that affects topology (add/remove
// line, toggle visibility), C# bumps a state version and republishes the whole
// blob. React resync is automatic via useValue.

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
  // Screen-space midpoint anchor for the per-segment popover (Stage 5d). CSS pixels,
  // origin top-left. -1 when the segment is behind the camera or off-screen — UI hides
  // the popover in that case.
  screenX: number;
  screenY: number;
}

export interface LineVM {
  lineIndex: number;
  style: number;          // matches the MarkingStyle enum on C# side
  // Curvature stepper value, integer percent 0..100. 0 = straight chord, 50 = the
  // default arc (pull factor 0.4), 100 = maximum roundness (pull factor 0.8).
  curv: number;
  segments: SegmentVM[];
}

export interface ToolStateVM {
  isActive: boolean;       // tool currently the active tool
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
}

const EMPTY: ToolStateVM = {
  isActive: false,
  selectedNodeIndex: -1,
  currentStyle: 0,
  lastClickedLine: -1,
  lastClickedTick: 0,
  hoveredLineInGame: -1,
  vanillaHidden: false,
  lines: [],
};

const STATE_BINDING = bindValue<string>("TownRoadLane", "GetToolState", "{}");

export const useToolState = (): ToolStateVM => {
  const json = useValue(STATE_BINDING);
  try {
    if (!json || json === "{}") return EMPTY;
    const raw = JSON.parse(json) as Partial<ToolStateVM>;
    // Defensive normalisation — every C# tick rebuilds the state JSON, and any
    // missing/null fields would crash deeper in the component tree (e.g. the
    // expandedLine effect reads state.lines.length). Coerce to known shapes here
    // so consumers can trust ToolStateVM invariants.
    return {
      isActive: Boolean(raw.isActive),
      selectedNodeIndex: typeof raw.selectedNodeIndex === "number" ? raw.selectedNodeIndex : -1,
      currentStyle: typeof raw.currentStyle === "number" ? raw.currentStyle : 0,
      lastClickedLine: typeof raw.lastClickedLine === "number" ? raw.lastClickedLine : -1,
      lastClickedTick: typeof raw.lastClickedTick === "number" ? raw.lastClickedTick : 0,
      hoveredLineInGame: typeof raw.hoveredLineInGame === "number" ? raw.hoveredLineInGame : -1,
      vanillaHidden: Boolean(raw.vanillaHidden),
      lines: Array.isArray(raw.lines)
        ? raw.lines.map((l: any) => ({
            lineIndex: typeof l?.lineIndex === "number" ? l.lineIndex : -1,
            style: typeof l?.style === "number" ? l.style : 0,
            curv: typeof l?.curv === "number" ? l.curv : 50,
            segments: Array.isArray(l?.segments)
              ? l.segments.map((s: any) => ({
                  lineIndex: typeof s?.lineIndex === "number" ? s.lineIndex : -1,
                  segmentIndex: typeof s?.segmentIndex === "number" ? s.segmentIndex : 0,
                  tStart: typeof s?.tStart === "number" ? s.tStart : 0,
                  tEnd: typeof s?.tEnd === "number" ? s.tEnd : 1,
                  visible: Boolean(s?.visible),
                  style: typeof s?.style === "number" ? s.style : 0,
                  lengthM: typeof s?.lengthM === "number" ? s.lengthM : 0,
                  screenX: typeof s?.screenX === "number" ? s.screenX : -1,
                  screenY: typeof s?.screenY === "number" ? s.screenY : -1,
                }))
              : [],
          }))
        : [],
    };
  } catch (e) {
    console.error("TownRoadLane UI: bad state JSON:", e);
    return EMPTY;
  }
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
