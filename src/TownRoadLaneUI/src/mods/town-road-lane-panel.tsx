// React's KeyboardEvent is aliased so the bare name stays the DOM type, which the
// document-level hotkey handler below uses.
import { ChangeEvent, Component, ErrorInfo, KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent, ReactNode, useEffect, useState } from "react";
import { createPortal } from "react-dom";
import {
  useToolState,
  cmdToggleSegment,
  cmdSetLineStyle,
  cmdSetSegmentStyle,
  cmdDeleteLine,
  cmdSetHoveredLine,
  cmdSetHoveredSegment,
  cmdSetHoveredArea,
  cmdClearHoveredArea,
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
import {
  usePinnedStyles,
  cmdTogglePinLineStyle,
  cmdTogglePinAreaStyle,
} from "../hooks/usePinnedStyles";
import { registerSegmentAnchor, setAnchorExpanded, segKey, areaKey } from "../hooks/positionRegistry";
import { useConfirm, useKeyedConfirm, Confirm } from "../hooks/useConfirm";
import { ChevronRight, Cross, Eye, EyeOff, Trash, Cycle } from "../components/icons";
import { LineStylePreview, AreaStylePreview, isG87LineStyle } from "../components/stylePreviews";
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
  StatusRow,
  StatusDot,
  ToggleRow,
  IconToggleBtn,
  FoldoutHeader,
  NodeIdText,
  PanelHint,
  PanelList,
  ModeRow,
  ModeBtn,
  DraftBox,
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
  SwatchWrap,
  G87Mark,
  LineSegCount,
  LineBody,
  StyleRow,
  CurvRow,
  CurvLabel,
  CurvInput,
  CurvUnit,
  CurvResetBtn,
  CurvStepBtn,
  PopoverRoot,
  PopoverBtn,
  PopoverMarker,
  PopoverDropdownWrap,
  SegmentRow,
  SegmentInfo,
  SegmentLen,
  SegmentIndicator,
  Btn,
  ConfirmRow,
} from "./panel.styles";

// An exception inside the panel must not reach the game's React root, where it would take
// down the whole HUD. The error is logged and a small error panel with a retry button is
// shown instead.
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

// MarkingStyle values from C#, in dropdown order.
const STYLE_VALUES = [0, 1, 5, 8, 4, 10, 11, 13, 12, 2, 3, 6, 7, 9] as const;
type StyleValue = typeof STYLE_VALUES[number];

// STYLE_VALUES groups related looks for the dropdown (vanilla white, vanilla yellow, G87,
// curb). The enum values themselves only ever get appended, so their numeric order means
// nothing for display.
const STYLE_KEYS: Record<number, StringKey> = {
  0: "style.solid",
  1: "style.dashed",
  2: "style.g87Solid",
  3: "style.g87Dashed",
  4: "style.doubleSolid",
  5: "style.dashedDense",
  6: "style.g87Yellow",
  7: "style.g87YellowDashed",
  8: "style.dashedLong",
  9: "style.curb",
  10: "style.yellowSolid",
  11: "style.yellowDashed",
  12: "style.yellowDoubleSolid",
  13: "style.yellowSolidDashed",
};

const styleLabel = (t: ReturnType<typeof useT>, style: number): string =>
  t(STYLE_KEYS[style] ?? "style.unknown");

// Area fill styles; ids match kStyleSurfaceNames in MarkingAreaEmissionSystem. Ids are
// saved with each area, so they only get appended and their order means nothing for
// display. 7-13 and 16 are unused slots and stay hidden; 15 and 17+ are the vanilla surfaces
// built by VanillaSurfaceLateClone.
const AREA_STYLE_VALUES = [0, 14, 1, 2, 3, 4, 5, 6, 15, 17, 18, 19, 20, 21, 22] as const;
const AREA_STYLE_ID_SET: ReadonlySet<number> = new Set(AREA_STYLE_VALUES);

const areaStyleLabel = (t: ReturnType<typeof useT>, styleId: number): string => {
  const key = `areaStyle.${styleId}` as StringKey;
  return AREA_STYLE_ID_SET.has(styleId) ? t(key) : t("style.unknown");
};

// Option lists for every style picker (panel rows and popovers). Pinned favourites
// (usePinnedStyles) come first, both groups in catalogue order.
const sortPinnedFirst = <V,>(opts: DropdownOption<V>[]): DropdownOption<V>[] =>
  [...opts.filter((o) => o.pinned), ...opts.filter((o) => !o.pinned)];

const makeLineStyleOptions = (t: ReturnType<typeof useT>, pinned: number[]): DropdownOption<StyleValue>[] =>
  sortPinnedFirst(
    STYLE_VALUES.map((s) => ({
      value: s,
      label: styleLabel(t, s),
      preview: <LineStylePreview style={s} width={28} height={8} />,
      pinned: pinned.includes(s),
    })),
  );

const makeAreaStyleOptions = (t: ReturnType<typeof useT>, pinned: number[]): DropdownOption<number>[] =>
  sortPinnedFirst(
    AREA_STYLE_VALUES.map((s) => ({
      value: s,
      label: areaStyleLabel(t, s),
      preview: <AreaStylePreview styleId={s} size={12} />,
      pinned: pinned.includes(s),
    })),
  );

// Mounted into GameTopRight by moduleRegistry.
export const TownRoadLanePanel = () => (
  <PanelErrorBoundary>
    <TooltipProvider>
      <TownRoadLanePanelInner />
    </TooltipProvider>
  </PanelErrorBoundary>
);

// Hover expansion shared by the in-world popovers. Collapsed, a popover is a small marker so
// a line with many segments doesn't fill the view with buttons; hovering shows its buttons.
// `hover` and `unhover` drive the in-world highlight.
const usePopoverHover = (anchorKey: string, hover: () => void, unhover: () => void) => {
  const [expanded, setExpanded] = useState(false);
  // The style menu is portalled to document.body, so while it is open the cursor is outside
  // PopoverRoot. Collapsing then would unmount the dropdown mid-pick.
  const [styleOpen, setStyleOpen] = useState(false);
  const deleteConfirm = useConfirm();
  const showButtons = expanded || styleOpen;

  // Lets the registry keep the popover at full size while the buttons are shown. The
  // flag is cleared on unmount when the ref callback unregisters the anchor.
  useEffect(() => {
    setAnchorExpanded(anchorKey, showButtons);
  }, [anchorKey, showButtons]);

  return {
    showButtons,
    deleteConfirm,
    onMouseEnter: () => {
      setExpanded(true);
      hover();
    },
    onMouseLeave: () => {
      deleteConfirm.disarm();
      // Moving into the portalled style menu also leaves the root. Keep the buttons
      // while the menu is open; onStyleOpenChange collapses them when it closes.
      if (styleOpen) return;
      setExpanded(false);
      unhover();
    },
    onStyleOpenChange: (open: boolean) => {
      setStyleOpen(open);
      // The cursor may be over the portalled menu, outside the root, so no mouseleave will
      // come. Collapse here; hovering again re-expands.
      if (!open) {
        setExpanded(false);
        unhover();
      }
    },
  };
};

const ARMED_DELETE_STYLE = {
  background: T.colorDangerSoft,
  borderColor: T.colorDanger,
  color: T.colorDanger,
};

// Two-press delete in a popover: one button, red while armed like the panel's confirm row.
const PopoverDeleteButton = ({
  confirm,
  label,
  onDelete,
}: {
  confirm: Confirm;
  label: string;
  onDelete: () => void;
}) => {
  const t = useT();
  return (
    <Tooltip content={confirm.armed ? t("line.delete.confirm.btn") : label}>
      <PopoverBtn
        $active={confirm.armed}
        style={confirm.armed ? ARMED_DELETE_STYLE : undefined}
        onClick={() => confirm.press(onDelete)}
      >
        <Trash size={14} color={confirm.armed ? T.colorDanger : undefined} />
      </PopoverBtn>
    </Tooltip>
  );
};

// In-world popover at a segment's midpoint. The collapsed marker is white when the segment
// is visible and red when hidden; the buttons are the visibility toggle, style picker and a
// two-press delete of the whole line.
//
// Position is not React state: the root registers with positionRegistry, which writes
// left/top/transform on each GetScreenPoints push, hides the popover while its segment is
// off-screen and scales it with camera distance.
const SegmentPopover = ({ seg }: { seg: SegmentVM }) => {
  const t = useT();
  const pinned = usePinnedStyles();
  const key = segKey(seg.lineIndex, seg.segmentIndex);
  const popover = usePopoverHover(
    key,
    // Highlight just this segment, not the whole line.
    () => cmdSetHoveredSegment(seg.lineIndex, seg.segmentIndex),
    () => cmdSetHoveredSegment(-1, -1),
  );

  // Portalled to document.body: inside GameTopRight, `position: fixed` would be relative to
  // the slot rather than the viewport (the slot probably has a transform), while the screen
  // points from Camera.WorldToScreenPoint are viewport coordinates.
  return createPortal(
    <PopoverRoot
      ref={(el: HTMLElement | null) => registerSegmentAnchor(key, el)}
      onMouseEnter={popover.onMouseEnter}
      onMouseLeave={popover.onMouseLeave}
    >
      {!popover.showButtons ? (
        <PopoverMarker $hidden={!seg.visible} />
      ) : (
        <>
          <Tooltip
            content={seg.visible ? t("segment.hide.tooltip") : t("segment.show.tooltip")}
          >
            <PopoverBtn
              // Highlighted while the segment is hidden.
              $active={!seg.visible}
              onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
            >
              {seg.visible ? <Eye size={14} /> : <EyeOff size={14} />}
            </PopoverBtn>
          </Tooltip>
          <PopoverDropdownWrap>
            <Dropdown
              value={seg.style as StyleValue}
              options={makeLineStyleOptions(t, pinned.lineStyles)}
              onChange={(s) => cmdSetSegmentStyle(seg.lineIndex, seg.segmentIndex, s)}
              onTogglePin={cmdTogglePinLineStyle}
              onOpenChange={popover.onStyleOpenChange}
            />
          </PopoverDropdownWrap>
          <PopoverDeleteButton
            confirm={popover.deleteConfirm}
            label={t("line.delete")}
            onDelete={() => cmdDeleteLine(seg.lineIndex)}
          />
        </>
      )}
    </PopoverRoot>,
    document.body,
  );
};

// In-world popover at an area's centroid (sent on GetScreenPoints under an `area:` key).
// Works like SegmentPopover, but the collapsed face is the area's fill swatch.
const AreaPopover = ({ area }: { area: AreaVM }) => {
  const t = useT();
  const pinned = usePinnedStyles();
  const key = areaKey(area.areaIndex);
  const popover = usePopoverHover(
    key,
    () => cmdSetHoveredArea(area.areaIndex),
    () => cmdClearHoveredArea(area.areaIndex),
  );

  return createPortal(
    <PopoverRoot
      ref={(el: HTMLElement | null) => registerSegmentAnchor(key, el)}
      onMouseEnter={popover.onMouseEnter}
      onMouseLeave={popover.onMouseLeave}
    >
      {!popover.showButtons ? (
        area.visible ? (
          <AreaStylePreview styleId={area.styleId} size={12} />
        ) : (
          <PopoverMarker $hidden />
        )
      ) : (
        <>
          <Tooltip content={area.visible ? t("area.hide.tooltip") : t("area.show.tooltip")}>
            <PopoverBtn
              $active={!area.visible}
              onClick={() => cmdToggleAreaVisible(area.areaIndex)}
            >
              {area.visible ? <Eye size={14} /> : <EyeOff size={14} />}
            </PopoverBtn>
          </Tooltip>
          <PopoverDropdownWrap>
            <Dropdown
              value={area.styleId}
              options={makeAreaStyleOptions(t, pinned.areaStyles)}
              onChange={(s) => cmdSetAreaStyle(area.areaIndex, s)}
              onTogglePin={cmdTogglePinAreaStyle}
              onOpenChange={popover.onStyleOpenChange}
            />
          </PopoverDropdownWrap>
          <PopoverDeleteButton
            confirm={popover.deleteConfirm}
            label={t("area.delete")}
            onDelete={() => cmdDeleteArea(area.areaIndex)}
          />
        </>
      )}
    </PopoverRoot>,
    document.body,
  );
};

// Hotkey list, collapsed by default so it doesn't take a third of the panel. It starts
// open on the "select a node" card, where the panel is otherwise empty.
const HotkeysFoldout = ({ defaultOpen = false }: { defaultOpen?: boolean }) => {
  const t = useT();
  const [open, setOpen] = useState(defaultOpen);
  return (
    <HintsBox>
      <FoldoutHeader onClick={() => setOpen(!open)}>
        <LineChevron $open={open}>
          <ChevronRight size={10} />
        </LineChevron>
        <span>{t("hotkeys.title")}</span>
      </FoldoutHeader>
      {open && (
        <>
          <HintRow><HintKey>Ctrl+M</HintKey><span>{t("hotkeys.toggle")}</span></HintRow>
          <HintRow><HintKey>Y</HintKey><span>{t("hotkeys.cycleLine")}</span></HintRow>
          <HintRow><HintKey>A</HintKey><span>{t("hotkeys.areaMode")}</span></HintRow>
          <HintRow><HintKey>U</HintKey><span>{t("hotkeys.cycleArea")}</span></HintRow>
          <HintRow><HintKey>{t("hotkeys.rmb")}</HintKey><span>{t("hotkeys.rmb.desc")}</span></HintRow>
          <HintRow><HintKey>{t("hotkeys.esc")}</HintKey><span>{t("hotkeys.esc.desc")}</span></HintRow>
        </>
      )}
    </HintsBox>
  );
};

// Title and close button, the same on both panel states.
const PanelHeader = () => {
  const t = useT();
  return (
    <PanelHeaderRow>
      <PanelTitle>{t("panel.appTitle")}</PanelTitle>
      <Tooltip content={t("panel.close.tooltip")}>
        <CloseBtn onClick={() => cmdActivateTool()}>
          <Cross size={10} />
        </CloseBtn>
      </Tooltip>
    </PanelHeaderRow>
  );
};

// The next step for the user in the current tool state.
const toolStatus = (t: ReturnType<typeof useT>, state: { toolState: number; areaVertexCount: number }): string => {
  if (state.toolState === TOOL_STATE.AreaSelecting) {
    return t("status.area", { n: state.areaVertexCount });
  }
  if (state.toolState === TOOL_STATE.SourceSelected) {
    return t("status.line.second");
  }
  return t("status.line.first");
};

const TownRoadLanePanelInner = () => {
  const state = useToolState();
  const t = useT();
  const pinned = usePinnedStyles();
  // Expanded line row, -1 for none.
  const [expandedLine, setExpandedLine] = useState<number>(-1);
  // Expanded area row, independent of expandedLine.
  const [expandedArea, setExpandedArea] = useState<number>(-1);
  // Line armed for deletion by the Delete key; a second press in time deletes it.
  // DeleteLineButton shows its confirm row while this matches its line.
  const keyDelete = useKeyedConfirm<number>();
  // Folded lists keep only the header (with count) and the row that is expanded or hovered
  // in the world, so a busy junction doesn't turn the panel into a wall. Kept across node
  // switches on purpose.
  const [linesFolded, setLinesFolded] = useState(false);
  const [areasFolded, setAreasFolded] = useState(false);

  // The expanded rows react to changes pushed by C#. They are adjusted while rendering, on
  // the render that first sees the change, instead of in effects that would commit a frame
  // with the stale row first. The seen values start out null so the first render applies
  // them too.
  const lineListKey = `${state.selectedNodeIndex}:${state.lines.length}`;
  const areaListKey = `${state.selectedNodeIndex}:${state.areas.length}`;
  const [seenLineListKey, setSeenLineListKey] = useState<string | null>(null);
  const [seenAreaListKey, setSeenAreaListKey] = useState<string | null>(null);
  const [seenClickTick, setSeenClickTick] = useState<number | null>(null);

  if (lineListKey !== seenLineListKey || state.lastClickedTick !== seenClickTick) {
    let next = expandedLine;
    // Another node or line count: a node with a single line opens with that line expanded,
    // and a row past the end of the list is dropped.
    if (lineListKey !== seenLineListKey) {
      setSeenLineListKey(lineListKey);
      if (state.lines.length === 1) next = 0;
      else if (next >= state.lines.length) next = -1;
    }
    // A click on a line in the world expands its row, a click that hit no line collapses.
    // The tick changes on every click, so clicking the same line again still counts.
    if (state.lastClickedTick !== seenClickTick) {
      setSeenClickTick(state.lastClickedTick);
      if (state.lastClickedLine >= 0 && state.lastClickedLine < state.lines.length) {
        next = state.lastClickedLine;
      } else if (state.lastClickedTick > 0 && state.lastClickedLine === -1) {
        next = -1;
      }
    }
    if (next !== expandedLine) setExpandedLine(next);
  }

  // Drop the expanded area row when the list shrinks below it.
  if (areaListKey !== seenAreaListKey) {
    setSeenAreaListKey(areaListKey);
    if (expandedArea >= state.areas.length) setExpandedArea(-1);
  }

  // Panel shortcuts while a node is selected. Keys typed into text fields and combos with
  // Ctrl/Meta/Alt are left alone; those belong to the input or the game.
  useEffect(() => {
    if (!state.isActive || state.selectedNodeIndex < 0) return;

    const handler = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const tag = (e.target as HTMLElement | null)?.tagName?.toUpperCase();
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;

      const lineCount = state.lines.length;

      // Tab / Shift+Tab: cycle the expanded line.
      if (e.key === "Tab" && lineCount > 0) {
        e.preventDefault();
        const cur = expandedLine < 0 ? -1 : expandedLine;
        const step = e.shiftKey ? -1 : 1;
        const next = ((cur + step) % lineCount + lineCount) % lineCount;
        setExpandedLine(next);
        keyDelete.disarm();
        return;
      }

      // Esc: cancel a pending delete first, then collapse.
      if (e.key === "Escape") {
        if (keyDelete.armed !== null) {
          keyDelete.disarm();
        } else if (expandedLine >= 0) {
          setExpandedLine(-1);
        }
        return;
      }

      // Enter: expand the first line when nothing is expanded.
      if (e.key === "Enter") {
        if (expandedLine < 0 && lineCount > 0) setExpandedLine(0);
        return;
      }

      // Delete: the first press arms, the second deletes, as with the button.
      if (e.key === "Delete" && expandedLine >= 0) {
        keyDelete.press(expandedLine, () => cmdDeleteLine(expandedLine));
        return;
      }

      // 1..5: pick one of the first five dropdown styles for the expanded line.
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
  }, [state.isActive, state.selectedNodeIndex, state.lines.length, expandedLine, keyDelete]);

  if (!state.isActive) return null;

  const inAreaMode = state.toolState === TOOL_STATE.AreaSelecting;

  // No node selected yet: a short "how to start" card, so activating the tool gives
  // visible feedback before the first node click.
  if (state.selectedNodeIndex < 0) {
    return (
      <Panel>
        <PanelHeader />
        <StatusRow>
          <StatusDot />
          <span>{t("panel.hint.selectNode")}</span>
        </StatusRow>
        <HotkeysFoldout defaultOpen />
      </Panel>
    );
  }

  const popoverLine =
    expandedLine >= 0 && expandedLine < state.lines.length ? state.lines[expandedLine] : null;
  const popoverArea = state.areas.find((a) => a.areaIndex === expandedArea) ?? null;

  const lineStyleOptions = makeLineStyleOptions(t, pinned.lineStyles);
  const areaStyleOptions = makeAreaStyleOptions(t, pinned.areaStyles);

  return (
    <>
      <Panel>
        <PanelStickyChrome>
          <PanelHeader />
          <StatusRow>
            <StatusDot />
            <span>{toolStatus(t, state)}</span>
          </StatusRow>

          <SectionTitle>{t("section.drawing")}</SectionTitle>
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
                  onTogglePin={cmdTogglePinAreaStyle}
                />
              </FieldRow>
              <DraftBox>
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
                onTogglePin={cmdTogglePinLineStyle}
              />
            </FieldRow>
          )}
        </PanelStickyChrome>

        {state.lines.length > 0 && (
          <>
            <FoldoutHeader onClick={() => setLinesFolded(!linesFolded)}>
              <LineChevron $open={!linesFolded}>
                <ChevronRight size={10} />
              </LineChevron>
              <span>{`${t("section.lines")} · ${state.lines.length}`}</span>
            </FoldoutHeader>
            <PanelList>
              {state.lines.map((line) => {
                if (
                  linesFolded &&
                  expandedLine !== line.lineIndex &&
                  state.hoveredLineInGame !== line.lineIndex
                )
                  return null;
                return (
                  <LineRow
                    key={line.lineIndex}
                    line={line}
                    isExpanded={expandedLine === line.lineIndex}
                    isGameHovered={state.hoveredLineInGame === line.lineIndex}
                    isPendingDelete={keyDelete.armed === line.lineIndex}
                    onToggleExpand={() =>
                      setExpandedLine(expandedLine === line.lineIndex ? -1 : line.lineIndex)
                    }
                    onCancelPendingDelete={keyDelete.disarm}
                  />
                );
              })}
            </PanelList>
          </>
        )}

        {state.areas.length > 0 && (
          <>
            <FoldoutHeader onClick={() => setAreasFolded(!areasFolded)}>
              <LineChevron $open={!areasFolded}>
                <ChevronRight size={10} />
              </LineChevron>
              <span>{`${t("section.areas")} · ${state.areas.length}`}</span>
            </FoldoutHeader>
            <PanelList>
              {state.areas.map((area) => {
                if (
                  areasFolded &&
                  expandedArea !== area.areaIndex &&
                  state.hoveredAreaInGame !== area.areaIndex
                )
                  return null;
                return (
                  <AreaRow
                    key={area.areaIndex}
                    area={area}
                    isExpanded={expandedArea === area.areaIndex}
                    isGameHovered={state.hoveredAreaInGame === area.areaIndex}
                    areaStyleOptions={areaStyleOptions}
                    onToggleExpand={() =>
                      setExpandedArea(expandedArea === area.areaIndex ? -1 : area.areaIndex)
                    }
                  />
                );
              })}
            </PanelList>
          </>
        )}

        {/* Node settings sit at the bottom: the vanilla toggle is a setting, not an action,
            and the full reset is rare and destructive. */}
        <SectionTitle>{t("section.node")}</SectionTitle>
        <ToggleRow>
          <FieldLabel>{t("node.vanilla.label")}</FieldLabel>
          <Tooltip content={t("vanilla.tooltip")}>
            <IconToggleBtn
              $active={state.vanillaHidden}
              onClick={cmdToggleVanillaMarkings}
            >
              {state.vanillaHidden ? <EyeOff size={14} /> : <Eye size={14} />}
            </IconToggleBtn>
          </Tooltip>
        </ToggleRow>
        {(state.lines.length > 0 || state.areas.length > 0 || state.vanillaHidden) && (
          <ResetNodeButton />
        )}
        <NodeIdText>{t("panel.title", { n: state.selectedNodeIndex })}</NodeIdText>

        <HotkeysFoldout />
      </Panel>
      {!inAreaMode && popoverLine?.segments.map((seg) => (
        <SegmentPopover
          key={`pop-${seg.lineIndex}-${seg.segmentIndex}`}
          seg={seg}
        />
      ))}
      {!inAreaMode && popoverArea && (
        <AreaPopover key={`pop-area-${popoverArea.areaIndex}`} area={popoverArea} />
      )}
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
        {/* 1-based for humans; commands keep the raw index. */}
        <LineTitle>{t("line.title", { n: line.lineIndex + 1 })}</LineTitle>
        <Tooltip content={styleLabel(t, line.style)}>
          <SwatchWrap>
            <LineStylePreview style={line.style} width={28} height={8} />
            {isG87LineStyle(line.style) && <G87Mark>G87</G87Mark>}
          </SwatchWrap>
        </Tooltip>
        <LineSegCount>
          {t("line.segCount", { visible: visibleCount, total: line.segments.length })}
        </LineSegCount>
      </LineHeader>
      <LineBody $open={isExpanded}>
        <StyleSelector line={line} />
        <CurvatureInput line={line} />
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

// Same layout as LineRow. The body has the fill style, a visibility toggle and a two-step
// delete.
const AreaRow = ({
  area,
  isExpanded,
  isGameHovered,
  areaStyleOptions,
  onToggleExpand,
}: {
  area: AreaVM;
  isExpanded: boolean;
  isGameHovered: boolean;
  areaStyleOptions: DropdownOption<number>[];
  onToggleExpand: () => void;
}) => {
  const t = useT();
  const deleteConfirm = useConfirm();

  return (
    <LineRowOuter
      $expanded={isExpanded}
      $gameHovered={isGameHovered}
      onMouseEnter={() => cmdSetHoveredArea(area.areaIndex)}
      onMouseLeave={() => cmdClearHoveredArea(area.areaIndex)}
    >
      <LineHeader onClick={onToggleExpand}>
        <LineChevron $open={isExpanded}>
          <ChevronRight size={10} />
        </LineChevron>
        {/* 1-based for humans; commands keep the raw index. */}
        <LineTitle>{t("area.title", { n: area.areaIndex + 1 })}</LineTitle>
        <Tooltip content={areaStyleLabel(t, area.styleId)}>
          <SwatchWrap>
            <AreaStylePreview styleId={area.styleId} size={12} />
          </SwatchWrap>
        </Tooltip>
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
            onTogglePin={cmdTogglePinAreaStyle}
          />
        </FieldRow>
        <Tooltip content={area.visible ? t("area.hide.tooltip") : t("area.show.tooltip")}>
          <Btn $full onClick={() => cmdToggleAreaVisible(area.areaIndex)}>
            {area.visible ? <Eye size={12} /> : <EyeOff size={12} />}
            <span>{area.visible ? t("area.hide.tooltip") : t("area.show.tooltip")}</span>
          </Btn>
        </Tooltip>
        {!deleteConfirm.armed ? (
          <Btn $danger $full onClick={deleteConfirm.arm}>
            <Trash size={12} color={T.colorDanger} />
            <span>{t("area.delete")}</span>
          </Btn>
        ) : (
          <ConfirmDeleteRow
            onCancel={deleteConfirm.disarm}
            onConfirm={() => deleteConfirm.confirm(() => cmdDeleteArea(area.areaIndex))}
          />
        )}
      </LineBody>
    </LineRowOuter>
  );
};

// Removes every line and area on the node and the vanilla-hide override. Node-wide and
// destructive, so it needs the same two-step confirm as deletes.
const ResetNodeButton = () => {
  const t = useT();
  const resetConfirm = useConfirm();

  if (!resetConfirm.armed) {
    return (
      <Tooltip content={t("node.reset.tooltip")}>
        <Btn $danger $full onClick={resetConfirm.arm}>
          <Trash size={12} color={T.colorDanger} />
          <span>{t("node.reset")}</span>
        </Btn>
      </Tooltip>
    );
  }
  return (
    <ConfirmDeleteRow
      onCancel={resetConfirm.disarm}
      onConfirm={() => resetConfirm.confirm(cmdResetNode)}
    />
  );
};

// Second step of a panel delete: the first click swaps the button for this row, which goes
// back after CONFIRM_TIMEOUT_MS. Inline rather than a modal, because overlay positioning in
// cohtml is fragile and the item stays visible above while confirming.
const ConfirmDeleteRow = ({ onCancel, onConfirm }: { onCancel: () => void; onConfirm: () => void }) => {
  const t = useT();
  return (
    <ConfirmRow>
      <Btn onClick={onCancel}>
        <span>{t("line.delete.cancel")}</span>
      </Btn>
      <Btn $danger onClick={onConfirm}>
        <Trash size={12} color={T.colorDanger} />
        <span>{t("line.delete.confirm.btn")}</span>
      </Btn>
    </ConfirmRow>
  );
};

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
  // Armed either by a click here or by the Delete key through keyboardConfirming. Only the
  // click arm lives here; the parent owns and times out the keyboard one.
  const clickConfirm = useConfirm();

  // Clears both arms. After a delete the next line takes over this row's index, and must
  // not inherit the armed state.
  const disarm = () => {
    clickConfirm.disarm();
    if (keyboardConfirming) onKeyboardCancel();
  };

  if (!clickConfirm.armed && !keyboardConfirming) {
    return (
      <Btn $danger $full onClick={clickConfirm.arm}>
        <Trash size={12} color={T.colorDanger} />
        <span>{t("line.delete")}</span>
      </Btn>
    );
  }

  return (
    <ConfirmDeleteRow
      onCancel={disarm}
      onConfirm={() => {
        disarm();
        cmdDeleteLine(lineIndex);
      }}
    />
  );
};

// Curvature in percent of the C# pull factor range 0..0.8 (50% is the default 0.4 arc).
// A text field, since range sliders don't work in cohtml: digits only, committed on Enter
// or blur and clamped to 0..100. While the user types, the draft owns the field; otherwise
// it shows the C# value.
const CURV_DEFAULT = 50;

const CurvatureInput = ({ line }: { line: LineVM }) => {
  const t = useT();
  const [draft, setDraft] = useState<string | null>(null);

  const commit = () => {
    if (draft === null) return;
    const v = parseInt(draft, 10);
    if (!isNaN(v)) {
      cmdSetLineCurvature(line.lineIndex, Math.max(0, Math.min(100, v)));
    }
    setDraft(null);
  };

  // −/+: ±1, Shift ±10, Ctrl ±5. Steps from an uncommitted draft when there is one, since
  // that is what the user sees.
  const step = (dir: 1 | -1, e: ReactMouseEvent<HTMLButtonElement>) => {
    const mag = e.shiftKey ? 10 : e.ctrlKey ? 5 : 1;
    const parsed = draft !== null ? parseInt(draft, 10) : NaN;
    const base = !isNaN(parsed) ? parsed : line.curv;
    setDraft(null);
    cmdSetLineCurvature(line.lineIndex, Math.max(0, Math.min(100, base + dir * mag)));
  };

  return (
    <CurvRow>
      <Tooltip content={t("line.curvature.tooltip")}>
        <CurvLabel>{t("line.curvature")}</CurvLabel>
      </Tooltip>
      <Tooltip content={t("line.curvature.step")}>
        <CurvStepBtn onClick={(e: ReactMouseEvent<HTMLButtonElement>) => step(-1, e)}>−</CurvStepBtn>
      </Tooltip>
      <CurvInput
        type="text"
        value={draft ?? String(line.curv)}
        onChange={(e: ChangeEvent<HTMLInputElement>) =>
          setDraft(e.target.value.replace(/[^0-9]/g, "").slice(0, 3))
        }
        onBlur={commit}
        onKeyDown={(e: ReactKeyboardEvent<HTMLInputElement>) => {
          if (e.key === "Enter") commit();
        }}
      />
      <Tooltip content={t("line.curvature.step")}>
        <CurvStepBtn onClick={(e: ReactMouseEvent<HTMLButtonElement>) => step(1, e)}>+</CurvStepBtn>
      </Tooltip>
      <CurvUnit>%</CurvUnit>
      {line.curv !== CURV_DEFAULT && (
        <Tooltip content={t("line.curvature.reset")}>
          <CurvResetBtn
            onClick={() => cmdSetLineCurvature(line.lineIndex, CURV_DEFAULT)}
          >
            <Cycle size={12} />
          </CurvResetBtn>
        </Tooltip>
      )}
    </CurvRow>
  );
};

// Options are rebuilt on each render so they follow locale changes.
const StyleSelector = ({ line }: { line: LineVM }) => {
  const t = useT();
  const pinned = usePinnedStyles();
  return (
    <StyleRow>
      <Dropdown
        value={line.style as StyleValue}
        options={makeLineStyleOptions(t, pinned.lineStyles)}
        onChange={(s) => cmdSetLineStyle(line.lineIndex, s)}
        onTogglePin={cmdTogglePinLineStyle}
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
      {/* 1-based for humans; commands keep the raw index. */}
      <SegmentInfo>{t("segment.label", { n: seg.segmentIndex + 1 })}</SegmentInfo>
      <SegmentLen>{t("segment.length", { m: seg.lengthM.toFixed(1) })}</SegmentLen>
      <SegmentIndicator>
        {seg.visible ? <Eye size={12} /> : <EyeOff size={12} />}
      </SegmentIndicator>
    </SegmentRow>
  );
};
