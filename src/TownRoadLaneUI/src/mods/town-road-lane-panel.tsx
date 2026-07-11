import { Component, ErrorInfo, ReactNode, useEffect, useState } from "react";
import { createPortal } from "react-dom";
import {
  useToolState,
  cmdToggleSegment,
  cmdSetLineStyle,
  cmdSetSegmentStyle,
  cmdDeleteLine,
  cmdSetHoveredLine,
  cmdSetHoveredSegment,
  cmdSetLineCurvature,
  cmdToggleVanillaMarkings,
  cmdActivateTool,
  cmdSetCurrentStyle,
  cmdSetCurrentAreaStyle,
  cmdToggleAreaMode,
  cmdSetAreaStyle,
  cmdToggleAreaVisible,
  cmdDeleteArea,
  cmdResetNode,
  TOOL_STATE,
  LineVM,
  SegmentVM,
  AreaVM,
} from "../hooks/useToolState";
import { ChevronRight, Cross, Eye, EyeOff, Trash, Cycle } from "../components/icons";
import { Dropdown, DropdownOption } from "../components/Dropdown";
import { TooltipProvider, Tooltip } from "../components/Tooltip";
import { useT } from "../i18n";
import type { StringKey } from "../i18n";
import { tokens as T } from "../styles/tokens";
import {
  Panel,
  PanelStickyChrome,
  PanelTitle,
  PanelHeaderRow,
  CloseBtn,
  PanelMeta,
  PanelMetaSep,
  PanelHint,
  PanelList,
  ModeRow,
  ModeBtn,
  DraftBox,
  DraftTitle,
  DraftProgress,
  DraftHint,
  FieldRow,
  FieldLabel,
  SectionTitle,
  HintsBox,
  HintRow,
  HintKey,
  LineRowOuter,
  LineHeader,
  LineChevron,
  LineTitle,
  LineStyleTag,
  LineSegCount,
  LineBody,
  StyleRow,
  CurvRow,
  CurvLabel,
  CurvBtn,
  CurvValue,
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

// Area fill styles — indexes match kStyleSurfaceNames in MarkingAreaEmissionSystem.
const AREA_STYLE_VALUES = [0, 1, 2, 3, 4, 5, 6] as const;

const areaStyleLabel = (t: ReturnType<typeof useT>, styleId: number): string => {
  const key = `areaStyle.${styleId}` as StringKey;
  return styleId >= 0 && styleId < AREA_STYLE_VALUES.length ? t(key) : t("style.unknown");
};

// Exported wrapper — boundary first, then real panel. moduleRegistry mounts
// this into GameTopRight, so the boundary protects the game UI from our bugs.
export const TownRoadLanePanel = () => (
  <PanelErrorBoundary>
    <TooltipProvider>
      <TownRoadLanePanelInner />
    </TooltipProvider>
  </PanelErrorBoundary>
);

// Floating in-world popover anchored at a segment's midpoint. Buttons mirror
// the accordion controls (toggle visibility, cycle style) plus a delete-line
// button (C6, two-press confirm to avoid mistaken loss of work). Hidden when
// screen coords are negative (segment behind camera / off-screen).
const POPOVER_DELETE_CONFIRM_MS = 2500;

const SegmentPopover = ({ seg }: { seg: SegmentVM }) => {
  const t = useT();
  // Two-press delete state, local to each popover. First click flips on; second
  // click within the window dispatches. Resets on timeout or when the segment
  // changes (popovers re-render with new keys when the line is rebuilt).
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  useEffect(() => {
    if (!confirmingDelete) return;
    const id = window.setTimeout(() => setConfirmingDelete(false), POPOVER_DELETE_CONFIRM_MS);
    return () => window.clearTimeout(id);
  }, [confirmingDelete]);

  if (seg.screenX < 0 || seg.screenY < 0) return null;
  // Portal into document.body — our panel mounts inside GameTopRight which
  // likely has CSS transform/will-change set up by CS2, creating a containing
  // block that pins our `position: fixed` to the slot instead of the viewport.
  // A body portal gives us the true viewport-relative coordinates the
  // Camera.WorldToScreenPoint values were computed against.
  return createPortal(
    <PopoverRoot
      style={{ left: seg.screenX, top: seg.screenY }}
      onMouseEnter={() => {
        // Per-segment hover (C3): light up THIS segment specifically, not the
        // whole line. The popover's anchored to one segment, so the UX should
        // narrow attention to that segment alone.
        cmdSetHoveredSegment(seg.lineIndex, seg.segmentIndex);
      }}
      onMouseLeave={() => {
        cmdSetHoveredSegment(-1, -1);
        // Cancel pending delete if the user moves away — avoids the confirm
        // state lingering after the user gave up.
        setConfirmingDelete(false);
      }}
    >
      <Tooltip
        content={seg.visible ? t("segment.hide.tooltip") : t("segment.show.tooltip")}
      >
        <PopoverBtn
          // $active when the segment is hidden — telegraphs the toggle state at
          // a glance without forcing the user to interpret the icon.
          $active={!seg.visible}
          onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
        >
          {seg.visible ? <Eye size={14} /> : <EyeOff size={14} />}
        </PopoverBtn>
      </Tooltip>
      <Tooltip
        content={t("segment.cycleStyle.tooltip", { style: styleLabel(t, seg.style) })}
      >
        <PopoverBtn
          onClick={() =>
            cmdSetSegmentStyle(seg.lineIndex, seg.segmentIndex, (seg.style + 1) % STYLE_VALUES.length)
          }
        >
          <Cycle size={14} />
        </PopoverBtn>
      </Tooltip>
      <Tooltip
        content={
          confirmingDelete ? t("line.delete.confirm.btn") : t("line.delete")
        }
      >
        <PopoverBtn
          // $active = red-tinted confirm state. Same visual language as the
          // panel's inline DeleteLineButton confirm row.
          $active={confirmingDelete}
          style={
            confirmingDelete
              ? {
                  background: T.colorDangerSoft,
                  borderColor: T.colorDanger,
                  color: T.colorDanger,
                }
              : undefined
          }
          onClick={() => {
            if (confirmingDelete) {
              cmdDeleteLine(seg.lineIndex);
              setConfirmingDelete(false);
            } else {
              setConfirmingDelete(true);
            }
          }}
        >
          <Trash size={14} color={confirmingDelete ? T.colorDanger : undefined} />
        </PopoverBtn>
      </Tooltip>
    </PopoverRoot>,
    document.body,
  );
};

// Static footer with the tool's keyboard map. The bindings are configurable in
// the mod settings, but the defaults cover ~all users; the settings screen is
// linked from the game's own options, not from here.
const HotkeyHints = () => {
  const t = useT();
  return (
    <HintsBox>
      <SectionTitle style={{ marginTop: 0 }}>{t("hotkeys.title")}</SectionTitle>
      <HintRow><HintKey>Ctrl+M</HintKey><span>{t("hotkeys.toggle")}</span></HintRow>
      <HintRow><HintKey>Y</HintKey><span>{t("hotkeys.cycleLine")}</span></HintRow>
      <HintRow><HintKey>A</HintKey><span>{t("hotkeys.areaMode")}</span></HintRow>
      <HintRow><HintKey>U</HintKey><span>{t("hotkeys.cycleArea")}</span></HintRow>
      <HintRow><HintKey>{t("hotkeys.rmb")}</HintKey><span>{t("hotkeys.rmb.desc")}</span></HintRow>
      <HintRow><HintKey>{t("hotkeys.esc")}</HintKey><span>{t("hotkeys.esc.desc")}</span></HintRow>
    </HintsBox>
  );
};

const TownRoadLanePanelInner = () => {
  const state = useToolState();
  const t = useT();
  // Index of the currently-expanded line row. -1 = all collapsed.
  // Auto-snaps to a single-line selection so a fresh node opens immediately.
  const [expandedLine, setExpandedLine] = useState<number>(-1);
  // Index of the currently-expanded area row. Independent of expandedLine —
  // lines and areas are separate lists and expanding one shouldn't collapse
  // the other.
  const [expandedArea, setExpandedArea] = useState<number>(-1);
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

  // Areas: clamp the expanded row when the list shrinks; collapse on node switch.
  useEffect(() => {
    if (expandedArea >= state.areas.length) setExpandedArea(-1);
  }, [state.selectedNodeIndex, state.areas.length]);

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

  if (!state.isActive) return null;

  const inAreaMode = state.toolState === TOOL_STATE.AreaSelecting;

  // Tool active, nothing selected yet: a compact "how to start" card. Without
  // this the tool felt OFF after activation (no visual change anywhere until
  // the first node click).
  if (state.selectedNodeIndex < 0) {
    return (
      <Panel>
        <PanelHeaderRow>
          <PanelTitle>{t("panel.appTitle")}</PanelTitle>
          <Tooltip content={t("panel.close.tooltip")}>
            <CloseBtn onClick={() => cmdActivateTool()}>
              <Cross size={10} />
            </CloseBtn>
          </Tooltip>
        </PanelHeaderRow>
        <PanelHint>{t("panel.hint.selectNode")}</PanelHint>
        <HotkeyHints />
      </Panel>
    );
  }

  const popoverLine =
    expandedLine >= 0 && expandedLine < state.lines.length ? state.lines[expandedLine] : null;

  const lineStyleOptions: DropdownOption<StyleValue>[] = STYLE_VALUES.map((s) => ({
    value: s,
    label: styleLabel(t, s),
  }));
  const areaStyleOptions: DropdownOption<number>[] = AREA_STYLE_VALUES.map((s) => ({
    value: s,
    label: areaStyleLabel(t, s),
  }));

  return (
    <>
      <Panel>
        <PanelStickyChrome>
          <PanelHeaderRow>
            <PanelTitle>{t("panel.appTitle")}</PanelTitle>
            <Tooltip content={t("panel.close.tooltip")}>
              <CloseBtn onClick={() => cmdActivateTool()}>
                <Cross size={10} />
              </CloseBtn>
            </Tooltip>
          </PanelHeaderRow>
          <PanelMeta>
            <span>{t("panel.title", { n: state.selectedNodeIndex })}</span>
            <PanelMetaSep />
            <span>{t("panel.meta.lines", { n: state.lines.length })}</span>
            <PanelMetaSep />
            <span>{t("panel.meta.areas", { n: state.areas.length })}</span>
          </PanelMeta>

          <ModeRow>
            <Tooltip content={t("mode.lines.tooltip")}>
              <ModeBtn
                $active={!inAreaMode}
                onClick={() => { if (inAreaMode) cmdToggleAreaMode(); }}
              >
                {t("mode.lines")}
              </ModeBtn>
            </Tooltip>
            <Tooltip content={t("mode.area.tooltip")}>
              <ModeBtn
                $active={inAreaMode}
                onClick={() => { if (!inAreaMode) cmdToggleAreaMode(); }}
              >
                {t("mode.area")}
              </ModeBtn>
            </Tooltip>
          </ModeRow>

          {inAreaMode ? (
            <>
              <FieldRow>
                <Tooltip content={t("next.areaStyle.tooltip")}>
                  <FieldLabel>{t("next.areaStyle")}</FieldLabel>
                </Tooltip>
                <Dropdown
                  value={state.currentAreaStyle}
                  options={areaStyleOptions}
                  onChange={(s) => cmdSetCurrentAreaStyle(s)}
                />
              </FieldRow>
              <DraftBox>
                <DraftTitle>{t("area.draft.title")}</DraftTitle>
                <DraftProgress>{t("area.draft.progress", { n: state.areaVertexCount })}</DraftProgress>
                <DraftHint>{t("area.draft.hint.add")}</DraftHint>
                <DraftHint>{t("area.draft.hint.undo")}</DraftHint>
                <DraftHint>{t("area.draft.hint.close")}</DraftHint>
                <Btn $full onClick={() => cmdToggleAreaMode()}>
                  <span>{t("area.draft.cancel")}</span>
                </Btn>
              </DraftBox>
            </>
          ) : (
            <FieldRow>
              <Tooltip content={t("next.lineStyle.tooltip")}>
                <FieldLabel>{t("next.lineStyle")}</FieldLabel>
              </Tooltip>
              <Dropdown
                value={state.currentStyle as StyleValue}
                options={lineStyleOptions}
                onChange={(s) => cmdSetCurrentStyle(s)}
              />
            </FieldRow>
          )}

          <Tooltip content={t("vanilla.tooltip")}>
            <Btn $full onClick={cmdToggleVanillaMarkings}>
              {state.vanillaHidden ? <EyeOff size={12} /> : <Eye size={12} />}
              <span>{state.vanillaHidden ? t("vanilla.show") : t("vanilla.hide")}</span>
            </Btn>
          </Tooltip>
        </PanelStickyChrome>

        {state.lines.length === 0 && state.areas.length === 0 && (
          <PanelHint>{t("panel.hint.empty")}</PanelHint>
        )}

        {state.lines.length > 0 && (
          <>
            <SectionTitle>{t("section.lines")}</SectionTitle>
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
          </>
        )}

        {state.areas.length > 0 && (
          <>
            <SectionTitle>{t("section.areas")}</SectionTitle>
            <PanelList>
              {state.areas.map((area) => (
                <AreaRow
                  key={area.areaIndex}
                  area={area}
                  isExpanded={expandedArea === area.areaIndex}
                  areaStyleOptions={areaStyleOptions}
                  onToggleExpand={() =>
                    setExpandedArea(expandedArea === area.areaIndex ? -1 : area.areaIndex)
                  }
                />
              ))}
            </PanelList>
          </>
        )}

        {(state.lines.length > 0 || state.areas.length > 0 || state.vanillaHidden) && (
          <ResetNodeButton />
        )}

        <HotkeyHints />
      </Panel>
      {!inAreaMode && popoverLine?.segments.map((seg) => (
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
      <LineBody $open={isExpanded}>
        <StyleSelector line={line} />
        <CurvatureStepper line={line} />
        {line.segments.map((seg) => (
          <SegmentRowComponent key={`${seg.lineIndex}-${seg.segmentIndex}`} seg={seg} />
        ))}
        <DeleteLineButton
          lineIndex={line.lineIndex}
          keyboardConfirming={isPendingDelete}
          onKeyboardCancel={onCancelPendingDelete}
        />
      </LineBody>
    </LineRowOuter>
  );
};

// Area accordion row — mirrors LineRow's layout so the two lists read as one
// visual system. Body controls: fill-style dropdown, visibility toggle, and a
// two-stage delete (same confirm pattern as lines).
const AreaRow = ({
  area,
  isExpanded,
  areaStyleOptions,
  onToggleExpand,
}: {
  area: AreaVM;
  isExpanded: boolean;
  areaStyleOptions: DropdownOption<number>[];
  onToggleExpand: () => void;
}) => {
  const t = useT();
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  useEffect(() => {
    if (!confirmingDelete) return;
    const id = window.setTimeout(() => setConfirmingDelete(false), 3000);
    return () => window.clearTimeout(id);
  }, [confirmingDelete]);

  return (
    <LineRowOuter $expanded={isExpanded}>
      <LineHeader onClick={onToggleExpand}>
        <LineChevron $open={isExpanded}>
          <ChevronRight size={10} />
        </LineChevron>
        <LineTitle>{t("area.title", { n: area.areaIndex })}</LineTitle>
        <LineStyleTag>{areaStyleLabel(t, area.styleId)}</LineStyleTag>
        <LineSegCount>
          {area.pieceCount > 1
            ? t("area.pieces", { visible: area.visiblePieces, total: area.pieceCount })
            : t("area.meta.vertices", { n: area.vertexCount })}
        </LineSegCount>
      </LineHeader>
      <LineBody $open={isExpanded}>
        <FieldRow style={{ marginTop: 0 }}>
          <FieldLabel>{t("area.style")}</FieldLabel>
          <Dropdown
            value={area.styleId}
            options={areaStyleOptions}
            onChange={(s) => cmdSetAreaStyle(area.areaIndex, s)}
          />
        </FieldRow>
        <Tooltip content={area.visible ? t("area.hide.tooltip") : t("area.show.tooltip")}>
          <Btn $full onClick={() => cmdToggleAreaVisible(area.areaIndex)}>
            {area.visible ? <Eye size={12} /> : <EyeOff size={12} />}
            <span>{area.visible ? t("area.hide.tooltip") : t("area.show.tooltip")}</span>
          </Btn>
        </Tooltip>
        {!confirmingDelete ? (
          <Btn $danger $full onClick={() => setConfirmingDelete(true)}>
            <Trash size={12} color={T.colorDanger} />
            <span>{t("area.delete")}</span>
          </Btn>
        ) : (
          <ConfirmRow>
            <Btn onClick={() => setConfirmingDelete(false)}>
              <span>{t("line.delete.cancel")}</span>
            </Btn>
            <Btn $danger onClick={() => { cmdDeleteArea(area.areaIndex); setConfirmingDelete(false); }}>
              <Trash size={12} color={T.colorDanger} />
              <span>{t("line.delete.confirm.btn")}</span>
            </Btn>
          </ConfirmRow>
        )}
      </LineBody>
    </LineRowOuter>
  );
};

// Full node reset — wipes every line, area and the vanilla-hide override on
// the selected node, restoring stock markings. Destructive and node-wide, so
// it gets the same two-stage confirm as deletes and hides at the very bottom
// of the panel (rendered only when there is actually something to reset).
const ResetNodeButton = () => {
  const t = useT();
  const [confirming, setConfirming] = useState(false);
  useEffect(() => {
    if (!confirming) return;
    const id = window.setTimeout(() => setConfirming(false), 3000);
    return () => window.clearTimeout(id);
  }, [confirming]);

  if (!confirming) {
    return (
      <Tooltip content={t("node.reset.tooltip")}>
        <Btn $danger $full onClick={() => setConfirming(true)}>
          <Trash size={12} color={T.colorDanger} />
          <span>{t("node.reset")}</span>
        </Btn>
      </Tooltip>
    );
  }
  return (
    <ConfirmRow>
      <Btn onClick={() => setConfirming(false)}>
        <span>{t("line.delete.cancel")}</span>
      </Btn>
      <Btn $danger onClick={() => { cmdResetNode(); setConfirming(false); }}>
        <Trash size={12} color={T.colorDanger} />
        <span>{t("line.delete.confirm.btn")}</span>
      </Btn>
    </ConfirmRow>
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

// Curvature stepper — 10% steps over the C#-side pull-factor range [0, 0.8].
// 0% = straight chord, 50% = default arc (0.4), 100% = maximum roundness.
// Stepper buttons (not a slider): cohtml's <input type=range> styling is
// unreliable, and 11 discrete positions are plenty for this control.
const CURV_STEP = 10;

const CurvatureStepper = ({ line }: { line: LineVM }) => {
  const t = useT();
  const set = (v: number) =>
    cmdSetLineCurvature(line.lineIndex, Math.max(0, Math.min(100, v)));
  return (
    <CurvRow>
      <Tooltip content={t("line.curvature.tooltip")}>
        <CurvLabel>{t("line.curvature")}</CurvLabel>
      </Tooltip>
      <CurvBtn disabled={line.curv <= 0} onClick={() => set(line.curv - CURV_STEP)}>
        -
      </CurvBtn>
      <CurvValue>{line.curv}%</CurvValue>
      <CurvBtn disabled={line.curv >= 100} onClick={() => set(line.curv + CURV_STEP)}>
        +
      </CurvBtn>
    </CurvRow>
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
