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
import { ChevronRight, Eye, EyeOff, Trash, Cycle } from "../components/icons";
import { Dropdown, DropdownOption } from "../components/Dropdown";
import { useT } from "../i18n";
import type { StringKey } from "../i18n";
import { tokens as T } from "../styles/tokens";
import {
  Panel,
  PanelStickyChrome,
  PanelTitle,
  PanelMeta,
  PanelMetaValue,
  PanelMetaSep,
  PanelHint,
  PanelList,
  LineRowOuter,
  LineHeader,
  LineChevron,
  LineTitle,
  LineStyleTag,
  LineSegCount,
  LineBody,
  StyleRow,
  PopoverRoot,
  PopoverBtn,
  SegmentRow,
  SegmentInfo,
  SegmentIndicator,
  Btn,
  ConfirmRow,
} from "./panel.styles";

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
      return <PanelErrorFallback error={this.state.error} onRetry={() => this.setState({ error: null })} />;
    }
    return this.props.children;
  }
}

const PanelErrorFallback = ({ error, onRetry }: { error: Error; onRetry: () => void }) => {
  const t = useT();
  return (
    <Panel style={{ borderColor: T.colorDanger }}>
      <PanelTitle>{t("panel.error.title")}</PanelTitle>
      <PanelHint>{error.message}</PanelHint>
      <Btn onClick={onRetry}>{t("panel.error.retry")}</Btn>
    </Panel>
  );
};

// MarkingStyle enum on the C# side — numeric values must stay in sync.
const STYLE_VALUES = [0, 1, 2, 3, 4] as const;
type StyleValue = typeof STYLE_VALUES[number];

// Lookup table: enum value → i18n string key. Keeps style label rendering
// alongside the enum mapping rather than scattered across components.
const STYLE_KEYS: Record<number, StringKey> = {
  0: "style.solid",
  1: "style.dashed",
  2: "style.g87Solid",
  3: "style.g87Dashed",
  4: "style.doubleSolid",
};

const styleLabel = (t: ReturnType<typeof useT>, style: number): string =>
  t(STYLE_KEYS[style] ?? "style.unknown");

// Exported wrapper — boundary first, then real panel. moduleRegistry mounts
// this into GameTopRight, so the boundary protects the game UI from our bugs.
export const TownRoadLanePanel = () => (
  <PanelErrorBoundary>
    <TownRoadLanePanelInner />
  </PanelErrorBoundary>
);

// Floating in-world popover anchored at a segment's midpoint. Buttons mirror
// the accordion controls (toggle visibility, cycle style) so users can edit
// without rolling open the side panel. Hidden when screen coords are negative
// (segment behind camera / off-screen).
const SegmentPopover = ({ seg }: { seg: SegmentVM }) => {
  const t = useT();
  if (seg.screenX < 0 || seg.screenY < 0) return null;
  // Portal into document.body — our panel mounts inside GameTopRight which
  // likely has CSS transform/will-change set up by CS2, creating a containing
  // block that pins our `position: fixed` to the slot instead of the viewport.
  // A body portal gives us the true viewport-relative coordinates the
  // Camera.WorldToScreenPoint values were computed against.
  return createPortal(
    <PopoverRoot
      style={{ left: seg.screenX, top: seg.screenY }}
      onMouseEnter={() => cmdSetHoveredLine(seg.lineIndex)}
      onMouseLeave={() => cmdSetHoveredLine(-1)}
    >
      <PopoverBtn
        // $active when the segment is hidden — telegraphs the toggle state at
        // a glance without forcing the user to interpret the icon.
        $active={!seg.visible}
        title={seg.visible ? t("segment.hide.tooltip") : t("segment.show.tooltip")}
        onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
      >
        {seg.visible ? <Eye size={14} /> : <EyeOff size={14} />}
      </PopoverBtn>
      <PopoverBtn
        title={t("segment.cycleStyle.tooltip", { style: styleLabel(t, seg.style) })}
        onClick={() =>
          cmdSetSegmentStyle(seg.lineIndex, seg.segmentIndex, (seg.style + 1) % STYLE_VALUES.length)
        }
      >
        <Cycle size={14} />
      </PopoverBtn>
    </PopoverRoot>,
    document.body,
  );
};

const TownRoadLanePanelInner = () => {
  const state = useToolState();
  const t = useT();
  // Index of the currently-expanded line row. -1 = all collapsed.
  // Auto-snaps to a single-line selection so a fresh node opens immediately.
  const [expandedLine, setExpandedLine] = useState<number>(-1);
  // Pending delete confirmation for the expanded line, driven by the Delete
  // keyboard shortcut (B2). First press flips this on; second press inside the
  // 3s window actually deletes. The DeleteLineButton mirrors this state so the
  // UI matches what the keyboard is doing.
  const [pendingDelete, setPendingDelete] = useState<number>(-1);

  // When the node changes (or the line count drops to 1), default to expanding line 0
  // so the user doesn't have to manually click to see anything useful.
  useEffect(() => {
    if (state.lines.length === 0) {
      setExpandedLine(-1);
    } else if (state.lines.length === 1) {
      setExpandedLine(0);
    } else if (expandedLine >= state.lines.length) {
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
      setExpandedLine(-1);
    }
  }, [state.lastClickedTick]);

  // Keyboard shortcuts (B2) — active only while the panel is mounted (i.e.
  // tool active + node selected). The listener filters out inputs/selects so
  // it doesn't intercept typing into a future text field. Also bails early on
  // modified keys (Ctrl/Meta combos) — those belong to the game.
  useEffect(() => {
    if (!state.isActive || state.selectedNodeIndex < 0) return;

    const handler = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const tag = (e.target as HTMLElement | null)?.tagName?.toUpperCase();
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;

      const lineCount = state.lines.length;

      // Tab / Shift+Tab — cycle expanded line. With nothing expanded, opens 0.
      if (e.key === "Tab" && lineCount > 0) {
        e.preventDefault();
        const cur = expandedLine < 0 ? -1 : expandedLine;
        const step = e.shiftKey ? -1 : 1;
        const next = ((cur + step) % lineCount + lineCount) % lineCount;
        setExpandedLine(next);
        setPendingDelete(-1);
        return;
      }

      // Esc — collapse / clear pending-delete (cancel chain).
      if (e.key === "Escape") {
        if (pendingDelete >= 0) {
          setPendingDelete(-1);
        } else if (expandedLine >= 0) {
          setExpandedLine(-1);
        }
        return;
      }

      // Enter — toggle expand of the first line if nothing expanded, else
      // collapse the currently expanded one. Useful after Tab-navigation.
      if (e.key === "Enter") {
        if (expandedLine < 0 && lineCount > 0) setExpandedLine(0);
        return;
      }

      // Delete — two-press confirm matching the button flow. First press arms
      // pendingDelete; second confirms and dispatches the actual delete.
      if (e.key === "Delete" && expandedLine >= 0) {
        if (pendingDelete === expandedLine) {
          cmdDeleteLine(expandedLine);
          setPendingDelete(-1);
        } else {
          setPendingDelete(expandedLine);
        }
        return;
      }

      // 1..5 — quick style pick for the expanded line.
      if (expandedLine >= 0 && /^[1-5]$/.test(e.key)) {
        const idx = parseInt(e.key, 10) - 1;
        if (idx >= 0 && idx < STYLE_VALUES.length) {
          cmdSetLineStyle(expandedLine, STYLE_VALUES[idx]);
        }
        return;
      }
    };

    document.addEventListener("keydown", handler);
    return () => document.removeEventListener("keydown", handler);
  }, [state.isActive, state.selectedNodeIndex, state.lines.length, expandedLine, pendingDelete]);

  // Auto-clear pending-delete after the same 3s window the button uses, so a
  // half-pressed Delete shortcut doesn't sit armed forever.
  useEffect(() => {
    if (pendingDelete < 0) return;
    const id = window.setTimeout(() => setPendingDelete(-1), 3000);
    return () => window.clearTimeout(id);
  }, [pendingDelete]);

  if (!state.isActive || state.selectedNodeIndex < 0) return null;

  const popoverLine =
    expandedLine >= 0 && expandedLine < state.lines.length ? state.lines[expandedLine] : null;

  return (
    <>
      <Panel>
        <PanelStickyChrome>
          <PanelTitle>{t("panel.title", { n: state.selectedNodeIndex })}</PanelTitle>
          <PanelMeta>
            <span>{t("panel.meta.lines", { n: state.lines.length })}</span>
            <PanelMetaSep />
            <span>
              {t("panel.meta.defaultStyle")} <PanelMetaValue>{styleLabel(t, state.currentStyle)}</PanelMetaValue>
            </span>
          </PanelMeta>
        </PanelStickyChrome>

        {state.lines.length === 0 && (
          <PanelHint>{t("panel.hint.empty")}</PanelHint>
        )}

        <PanelList>
          {state.lines.map((line) => (
            <LineRow
              key={line.lineIndex}
              line={line}
              isExpanded={expandedLine === line.lineIndex}
              isGameHovered={state.hoveredLineInGame === line.lineIndex}
              isPendingDelete={pendingDelete === line.lineIndex}
              onToggleExpand={() =>
                setExpandedLine(expandedLine === line.lineIndex ? -1 : line.lineIndex)
              }
              onCancelPendingDelete={() => setPendingDelete(-1)}
            />
          ))}
        </PanelList>
      </Panel>
      {popoverLine?.segments.map((seg) => (
        <SegmentPopover
          key={`pop-${seg.lineIndex}-${seg.segmentIndex}`}
          seg={seg}
        />
      ))}
    </>
  );
};

const LineRow = ({
  line,
  isExpanded,
  isGameHovered,
  isPendingDelete,
  onToggleExpand,
  onCancelPendingDelete,
}: {
  line: LineVM;
  isExpanded: boolean;
  isGameHovered: boolean;
  isPendingDelete: boolean;
  onToggleExpand: () => void;
  onCancelPendingDelete: () => void;
}) => {
  const t = useT();
  const visibleCount = line.segments.filter((s) => s.visible).length;
  return (
    <LineRowOuter
      $expanded={isExpanded}
      $gameHovered={isGameHovered}
      onMouseEnter={() => cmdSetHoveredLine(line.lineIndex)}
      onMouseLeave={() => cmdSetHoveredLine(-1)}
    >
      <LineHeader onClick={onToggleExpand}>
        <LineChevron $open={isExpanded}>
          <ChevronRight size={10} />
        </LineChevron>
        <LineTitle>{t("line.title", { n: line.lineIndex })}</LineTitle>
        <LineStyleTag>{styleLabel(t, line.style)}</LineStyleTag>
        <LineSegCount>
          {t("line.segCount", { visible: visibleCount, total: line.segments.length })}
        </LineSegCount>
      </LineHeader>
      {isExpanded && (
        <LineBody>
          <StyleSelector line={line} />
          {line.segments.map((seg) => (
            <SegmentRowComponent key={`${seg.lineIndex}-${seg.segmentIndex}`} seg={seg} />
          ))}
          <DeleteLineButton
            lineIndex={line.lineIndex}
            keyboardConfirming={isPendingDelete}
            onKeyboardCancel={onCancelPendingDelete}
          />
        </LineBody>
      )}
    </LineRowOuter>
  );
};

// Two-stage delete button (B1): first click flips to a confirm row, second
// click on Delete actually deletes. Cancel aborts. Auto-resets to idle after
// 3 seconds of inactivity so a half-pressed confirm doesn't sit forever.
//
// Why inline (not a modal): cohtml's overlay positioning is fragile, and a
// modal blocks the rest of the panel for a destructive action that's already
// rare. Inline keeps the user's context (they can still see the line they're
// about to delete in the segment list above).
const DELETE_CONFIRM_TIMEOUT_MS = 3000;

const DeleteLineButton = ({
  lineIndex,
  keyboardConfirming,
  onKeyboardCancel,
}: {
  lineIndex: number;
  keyboardConfirming: boolean;
  onKeyboardCancel: () => void;
}) => {
  const t = useT();
  // Local confirming state for mouse interactions; the keyboard-driven flow
  // (B2) flips through the parent via keyboardConfirming. Either source puts
  // the button into the confirm row.
  const [mouseConfirming, setMouseConfirming] = useState(false);
  const confirming = mouseConfirming || keyboardConfirming;

  // Mouse-confirming auto-resets after the timeout. Keyboard-confirming is
  // owned by the parent and reset by its own timeout / Esc handler, so we
  // don't touch it here — just clear our local copy.
  useEffect(() => {
    if (!mouseConfirming) return;
    const id = window.setTimeout(() => setMouseConfirming(false), DELETE_CONFIRM_TIMEOUT_MS);
    return () => window.clearTimeout(id);
  }, [mouseConfirming]);

  const cancel = () => {
    setMouseConfirming(false);
    if (keyboardConfirming) onKeyboardCancel();
  };

  if (!confirming) {
    return (
      <Btn $danger $full onClick={() => setMouseConfirming(true)}>
        <Trash size={12} color={T.colorDanger} />
        <span>{t("line.delete")}</span>
      </Btn>
    );
  }

  return (
    <ConfirmRow>
      <Btn onClick={cancel}>
        <span>{t("line.delete.cancel")}</span>
      </Btn>
      <Btn $danger onClick={() => cmdDeleteLine(lineIndex)}>
        <Trash size={12} color={T.colorDanger} />
        <span>{t("line.delete.confirm.btn")}</span>
      </Btn>
    </ConfirmRow>
  );
};

// Custom cohtml-safe Dropdown (see components/Dropdown.tsx). Options re-build
// on each render so they pick up locale changes (cheap — 5 entries).
const StyleSelector = ({ line }: { line: LineVM }) => {
  const t = useT();
  const options: DropdownOption<StyleValue>[] = STYLE_VALUES.map((s) => ({
    value: s,
    label: styleLabel(t, s),
  }));
  return (
    <StyleRow>
      <Dropdown
        value={line.style as StyleValue}
        options={options}
        onChange={(s) => cmdSetLineStyle(line.lineIndex, s)}
      />
    </StyleRow>
  );
};

const SegmentRowComponent = ({ seg }: { seg: SegmentVM }) => {
  const t = useT();
  return (
    <SegmentRow
      $hidden={!seg.visible}
      onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
    >
      <SegmentInfo>
        {t("segment.label", { n: seg.segmentIndex })} · {t("segment.length", { m: seg.lengthM.toFixed(1) })}
      </SegmentInfo>
      <SegmentIndicator>
        {seg.visible ? <Eye size={12} /> : <EyeOff size={12} />}
      </SegmentIndicator>
    </SegmentRow>
  );
};
