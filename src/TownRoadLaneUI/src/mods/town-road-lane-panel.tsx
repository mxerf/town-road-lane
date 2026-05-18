import { Component, ErrorInfo, ReactNode, useEffect, useState } from "react";
import { createPortal } from "react-dom";
import {
  useToolState,
  cmdToggleSegment,
  cmdSetLineStyle,
  cmdSetSegmentStyle,
  cmdDeleteLine,
  cmdSetHoveredLine,
  LineVM,
  SegmentVM,
} from "../hooks/useToolState";

// Containment boundary — a JS exception inside the panel must not propagate to
// the game's React root and tear the whole HUD down. Caught errors are logged
// and the panel renders a tiny "broken" placeholder until the underlying state
// changes (typically next C# tick).
class PanelErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) { return { error }; }
  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("TownRoadLane panel crashed:", error, info);
  }
  render() {
    if (this.state.error) {
      return (
        <div className="trl-panel" style={{ borderColor: "#f47373" }}>
          <h3 className="trl-panel__title">Panel error</h3>
          <div className="trl-panel__hint">{this.state.error.message}</div>
          <button className="trl-btn" onClick={() => this.setState({ error: null })}>Retry</button>
        </div>
      );
    }
    return this.props.children;
  }
}

// Style enum values must match TownRoadLane.MarkingStyle on the C# side.
const STYLE_NAMES: Record<number, string> = {
  0: "Solid",
  1: "Dashed",
  2: "G87 Solid",
  3: "G87 Dashed",
  4: "Double Solid",
};
const STYLE_VALUES = [0, 1, 2, 3, 4];

// Exported wrapper — boundary first, then real panel. moduleRegistry mounts
// this into GameTopRight, so the boundary protects the game UI from our bugs.
export const TownRoadLanePanel = () => (
  <PanelErrorBoundary>
    <TownRoadLanePanelInner />
  </PanelErrorBoundary>
);

// Floating in-world popover anchored at a segment's midpoint. Buttons mirror
// the accordion controls (toggle visibility, change style) so users can edit
// without rolling open the side panel. Hidden when screen coords are negative
// (segment behind camera / off-screen).
const SegmentPopover = ({ seg }: { seg: SegmentVM }) => {
  // Diagnostic kept in commit history if needed: log seg pos / vis before render.
  if (seg.screenX < 0 || seg.screenY < 0) return null;
  // Portal into document.body — our panel mounts inside GameTopRight which
  // likely has CSS transform/will-change set up by CS2, creating a containing
  // block that pins our `position: fixed` to the slot instead of the viewport.
  // A body portal gives us the true viewport-relative coordinates the
  // Camera.WorldToScreenPoint values were computed against.
  return createPortal(
    <div
      className="trl-popover"
      style={{ left: seg.screenX, top: seg.screenY }}
      onMouseEnter={() => cmdSetHoveredLine(seg.lineIndex)}
      onMouseLeave={() => cmdSetHoveredLine(-1)}
    >
      <button
        className="trl-popover__btn"
        title={seg.visible ? "Hide segment" : "Show segment"}
        onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
      >
        {seg.visible ? "○" : "●"}
      </button>
      <button
        className="trl-popover__btn"
        title={`Cycle style (current: ${STYLE_NAMES[seg.style] ?? "?"})`}
        onClick={() => cmdSetSegmentStyle(seg.lineIndex, seg.segmentIndex, (seg.style + 1) % STYLE_VALUES.length)}
      >
        S
      </button>
    </div>,
    document.body,
  );
};

const TownRoadLanePanelInner = () => {
  const state = useToolState();
  // Index of the currently-expanded line row. -1 = all collapsed.
  // Auto-snaps to a single-line selection so a fresh node opens immediately.
  const [expandedLine, setExpandedLine] = useState<number>(-1);

  // When the node changes (or the line count drops to 1), default to expanding line 0
  // so the user doesn't have to manually click to see anything useful.
  useEffect(() => {
    if (state.lines.length === 0) {
      setExpandedLine(-1);
    } else if (state.lines.length === 1) {
      setExpandedLine(0);
    } else if (expandedLine >= state.lines.length) {
      // Line got deleted — collapse so we don't show a stale row.
      setExpandedLine(-1);
    }
  }, [state.selectedNodeIndex, state.lines.length]);

  // Reverse hover-bridge: user clicked on a line in the game world (not a dot, not on
  // an existing accordion row). Auto-expand the matching line. Tick-based — same line
  // clicked twice still fires the effect because the tick increments on every click.
  useEffect(() => {
    if (state.lastClickedLine >= 0 && state.lastClickedLine < state.lines.length) {
      setExpandedLine(state.lastClickedLine);
    } else if (state.lastClickedTick > 0 && state.lastClickedLine === -1) {
      // Click on empty space inside the node — collapse everything.
      setExpandedLine(-1);
    }
  }, [state.lastClickedTick]);

  if (!state.isActive || state.selectedNodeIndex < 0) return null;

  // Popovers render for the currently-expanded line — one per segment, anchored
  // at the world-space midpoint. Wrapped in a fragment because they live in
  // screen-absolute coordinates, outside the panel's layout flow.
  const popoverLine =
    expandedLine >= 0 && expandedLine < state.lines.length ? state.lines[expandedLine] : null;

  return (
    <>
      <div className="trl-panel">
        <h3 className="trl-panel__title">Node #{state.selectedNodeIndex}</h3>
        <div className="trl-panel__meta">
          {state.lines.length} line(s){" · "}default style:{" "}
          <b>{STYLE_NAMES[state.currentStyle] ?? "?"}</b>
        </div>

        {state.lines.length === 0 && (
          <div className="trl-panel__hint">Click two endpoint dots to draw a line.</div>
        )}

        <div className="trl-panel__list">
          {state.lines.map((line) => (
            <LineRow
              key={line.lineIndex}
              line={line}
              isExpanded={expandedLine === line.lineIndex}
              onToggleExpand={() =>
                setExpandedLine(expandedLine === line.lineIndex ? -1 : line.lineIndex)
              }
            />
          ))}
        </div>
      </div>
      {popoverLine?.segments.map((seg) => (
        <SegmentPopover
          key={`pop-${seg.lineIndex}-${seg.segmentIndex}`}
          seg={seg}
        />
      ))}
    </>
  );
};

// Single accordion row. Header is always visible (clickable to expand/collapse,
// hover triggers in-game line highlight via SetHoveredLine bridge). Body with
// segment list + style selector + delete is shown only when expanded.
const LineRow = ({
  line,
  isExpanded,
  onToggleExpand,
}: {
  line: LineVM;
  isExpanded: boolean;
  onToggleExpand: () => void;
}) => {
  const visibleCount = line.segments.filter((s) => s.visible).length;
  return (
    <div
      className={`trl-line${isExpanded ? " trl-line--expanded" : ""}`}
      onMouseEnter={() => cmdSetHoveredLine(line.lineIndex)}
      onMouseLeave={() => cmdSetHoveredLine(-1)}
    >
      <div className="trl-line__header" onClick={onToggleExpand}>
        <span className={`trl-line__chevron${isExpanded ? " trl-line__chevron--open" : ""}`}>
          ▸
        </span>
        <span className="trl-line__title">Line #{line.lineIndex}</span>
        <span className="trl-line__style-tag">{STYLE_NAMES[line.style] ?? "?"}</span>
        <span className="trl-line__seg-count">
          {visibleCount}/{line.segments.length}
        </span>
      </div>
      {isExpanded && (
        <div className="trl-line__body">
          <StyleSelector line={line} />
          {line.segments.map((seg) => (
            <SegmentRow key={`${seg.lineIndex}-${seg.segmentIndex}`} seg={seg} />
          ))}
          <button
            className="trl-btn trl-btn--danger"
            onClick={() => cmdDeleteLine(line.lineIndex)}
          >
            Delete line
          </button>
        </div>
      )}
    </div>
  );
};

// Temporarily reverted from a native <select> to button row — cohtml's
// embedded JS runtime appears to choke on <select>/<option> rendering with a
// non-actionable "Cannot read properties of undefined (reading 'length')"
// error. Buttons render fine and the panel stays alive.
const StyleSelector = ({ line }: { line: LineVM }) => (
  <div className="trl-line__style-row">
    {STYLE_VALUES.map((s) => (
      <button
        key={s}
        className={`trl-btn trl-btn--style${s === line.style ? " trl-btn--active" : ""}`}
        onClick={() => s !== line.style && cmdSetLineStyle(line.lineIndex, s)}
      >
        {STYLE_NAMES[s]}
      </button>
    ))}
  </div>
);

const SegmentRow = ({ seg }: { seg: SegmentVM }) => (
  <div
    className={`trl-segment${seg.visible ? "" : " trl-segment--hidden"}`}
    onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
  >
    <span className="trl-segment__info">
      seg {seg.segmentIndex} · {seg.lengthM.toFixed(1)}m
    </span>
    <span className="trl-segment__indicator">{seg.visible ? "✓" : "✕"}</span>
  </div>
);
