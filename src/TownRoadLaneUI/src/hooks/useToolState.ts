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
  lengthM: number;        // chord length of the segment in metres (approximated)
}

export interface LineVM {
  lineIndex: number;
  style: number;          // matches the MarkingStyle enum on C# side
  segments: SegmentVM[];
}

export interface ToolStateVM {
  isActive: boolean;       // tool currently the active tool
  selectedNodeIndex: number; // -1 when no node selected
  currentStyle: number;    // enum value
  lines: LineVM[];
}

const EMPTY: ToolStateVM = {
  isActive: false,
  selectedNodeIndex: -1,
  currentStyle: 0,
  lines: [],
};

const STATE_BINDING = bindValue<string>("TownRoadLane", "GetToolState", "{}");

export const useToolState = (): ToolStateVM => {
  const json = useValue(STATE_BINDING);
  try {
    if (!json || json === "{}") return EMPTY;
    return JSON.parse(json) as ToolStateVM;
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

export const cmdDeleteLine = (lineIndex: number) => {
  trigger("TownRoadLane", "DeleteLine", lineIndex);
};

// Toggle the marking tool active/inactive — same as Ctrl+M or the settings
// button. Triggered from the toolbar button in GameTopLeft.
export const cmdActivateTool = () => {
  trigger("TownRoadLane", "ActivateTool");
};
