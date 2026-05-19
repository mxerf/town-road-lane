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
        title={seg.visible ? t("segment.hide.tooltip") : t("segment.show.tooltip")}
        onClick={() => cmdToggleSegment(seg.lineIndex, seg.segmentIndex)}
      >
        {seg.visible ? <Eye size={12} /> : <EyeOff size={12} />}
      </PopoverBtn>
      <PopoverBtn
        title={t("segment.cycleStyle.tooltip", { style: styleLabel(t, seg.style) })}
        onClick={() =>
          cmdSetSegmentStyle(seg.lineIndex, seg.segmentIndex, (seg.style + 1) % STYLE_VALUES.length)
        }
      >
        <Cycle size={12} />
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

  if (!state.isActive || state.selectedNodeIndex < 0) return null;

  const popoverLine =
    expandedLine >= 0 && expandedLine < state.lines.length ? state.lines[expandedLine] : null;

  return (
    <>
      <Panel>
        <PanelTitle>{t("panel.title", { n: state.selectedNodeIndex })}</PanelTitle>
        <PanelMeta>
          <span>{t("panel.meta.lines", { n: state.lines.length })}</span>
          <PanelMetaSep />
          <span>
            {t("panel.meta.defaultStyle")} <PanelMetaValue>{styleLabel(t, state.currentStyle)}</PanelMetaValue>
          </span>
        </PanelMeta>

        {state.lines.length === 0 && (
          <PanelHint>{t("panel.hint.empty")}</PanelHint>
        )}

        <PanelList>
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
  onToggleExpand,
}: {
  line: LineVM;
  isExpanded: boolean;
  onToggleExpand: () => void;
}) => {
  const t = useT();
  const visibleCount = line.segments.filter((s) => s.visible).length;
  return (
    <LineRowOuter
      $expanded={isExpanded}
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
          <Btn $danger $full onClick={() => cmdDeleteLine(line.lineIndex)}>
            <Trash size={12} color={T.colorDanger} />
            <span>{t("line.delete")}</span>
          </Btn>
        </LineBody>
      )}
    </LineRowOuter>
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
