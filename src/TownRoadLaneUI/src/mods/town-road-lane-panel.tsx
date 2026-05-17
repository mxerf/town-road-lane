import { useToolState, cmdToggleSegment, cmdSetLineStyle, cmdDeleteLine, LineVM, SegmentVM } from "../hooks/useToolState";

// Style enum values must match TownRoadLane.MarkingStyle on the C# side.
const STYLE_NAMES: Record<number, string> = {
  0: "Solid",
  1: "Dashed",
  2: "G87 Solid",
  3: "G87 Dashed",
};
const STYLE_VALUES = [0, 1, 2, 3]; // cycled in order; missing styles fall back to Solid on C# side

export const TownRoadLanePanel = () => {
  const state = useToolState();

  // Render nothing when the tool isn't active OR when no node has been picked yet.
  // Game UI stays clean unless the user is actively customising a node.
  if (!state.isActive || state.selectedNodeIndex < 0) return null;

  return (
    <div className="trl-panel">
      <h3 className="trl-panel__title">Node #{state.selectedNodeIndex}</h3>
      <div className="trl-panel__meta">
        {state.lines.length} line(s), {state.lines.reduce((acc, l) => acc + l.segments.length, 0)} segment(s)
        {" · "}default style: <b>{STYLE_NAMES[state.currentStyle] ?? "?"}</b>
      </div>
      {state.lines.length === 0 && (
        <div className="trl-panel__meta" style={{ fontStyle: "italic" }}>
          Click two endpoint dots to draw a line.
        </div>
      )}
      {state.lines.map((line) => (
        <LineRow key={line.lineIndex} line={line} />
      ))}
    </div>
  );
};

const LineRow = ({ line }: { line: LineVM }) => {
  const visibleCount = line.segments.filter((s) => s.visible).length;
  return (
    <div className="trl-panel__line">
      <div className="trl-panel__line-header">
        <span>
          Line #{line.lineIndex} <span className="trl-panel__line-style">({STYLE_NAMES[line.style] ?? "?"})</span>
        </span>
        <span style={{ color: "#9aa0a6", fontSize: 10 }}>
          {visibleCount}/{line.segments.length} visible
        </span>
      </div>
      <StyleSelector line={line} />
      {line.segments.map((seg) => (
        <SegmentRow key={`${seg.lineIndex}-${seg.segmentIndex}`} seg={seg} />
      ))}
      <button
        className="trl-panel__toggle-btn"
        style={{ width: "100%", marginTop: 4, color: "#f4b4b4" }}
        onClick={() => cmdDeleteLine(line.lineIndex)}
      >
        Delete line
      </button>
    </div>
  );
};

const StyleSelector = ({ line }: { line: LineVM }) => (
  <div style={{ display: "flex", gap: 4, marginBottom: 4 }}>
    {STYLE_VALUES.map((s) => (
      <button
        key={s}
        className="trl-panel__toggle-btn"
        style={{
          flex: 1,
          background: s === line.style ? "rgba(70, 140, 255, 0.35)" : "transparent",
          fontSize: 10,
        }}
        onClick={() => s !== line.style && cmdSetLineStyle(line.lineIndex, s)}
      >
        {STYLE_NAMES[s]}
      </button>
    ))}
  </div>
);

const SegmentRow = ({ seg }: { seg: SegmentVM }) => (
  <div
    className={`trl-panel__segment${seg.visible ? "" : " trl-panel__segment--hidden"}`}
    onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
  >
    <span className="trl-panel__segment-info">
      seg {seg.segmentIndex} · {seg.lengthM.toFixed(1)}m · t[{seg.tStart.toFixed(2)}–{seg.tEnd.toFixed(2)}]
    </span>
    <span className="trl-panel__toggle-btn" style={{ pointerEvents: "none" }}>
      {seg.visible ? "✓" : "✕"}
    </span>
  </div>
);
