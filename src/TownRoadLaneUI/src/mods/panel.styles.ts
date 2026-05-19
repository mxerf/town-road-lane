// Styled-components for the TownRoadLane UI surface.
//
// cohtml-safe checklist applied across every rule below:
//   - No `gap` — use margin-right / margin-bottom instead. cohtml's flex
//     impl doesn't always honour `gap`, especially nested.
//   - No `calc()` — pre-compute values in JS / hard-code.
//   - No `position: fixed` for popovers (TTE workaround) — but we DO use it
//     on .trl-popover via createPortal to document.body, which worked in
//     prior testing. Watch this if popovers regress.
//   - Inline values from tokens directly (no var(--foo) indirection).
//   - SVG children inherit `color` via `fill: none; stroke: currentColor;`
//     in IconBase — make sure parent has explicit `color` set if you want
//     a custom tint. Default color cascades from the panel root.

import { styled } from "../styles/styled";
import { tokens as T } from "../styles/tokens";

// ── Toolbar button (GameTopLeft) ───────────────────────────────────────
// Sized to roughly match vanilla CS2 floating toggle buttons. Icon is the
// only child; we don't show a text label here — the tooltip handles that.

export const ToolbarBtn = styled.button<{ $active?: boolean }>`
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 36rem;
  height: 36rem;
  margin: 6rem;
  padding: 0;
  background: ${(p) => (p.$active ? T.colorAccentSoft : T.colorPanelBg)};
  color: ${(p) => (p.$active ? "#fff" : T.colorTextPrimary)};
  border: 1rem solid ${(p) => (p.$active ? T.colorAccent : T.colorBorderMid)};
  border-radius: ${T.radiusMd};
  cursor: pointer;
  pointer-events: auto;
  transition: background ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  &:hover {
    background: ${(p) => (p.$active ? T.colorAccentSoft : "rgba(40, 48, 60, 0.95)")};
    border-color: ${T.colorBorderStrong};
    color: ${(p) => (p.$active ? "#fff" : T.colorAccent)};
  }
`;

// ── Main panel (GameTopRight) ──────────────────────────────────────────

export const Panel = styled.div`
  position: absolute;
  top: ${T.space2};
  right: ${T.space2};
  width: ${T.panelWidth};
  max-height: ${T.panelMaxHeight};
  overflow-y: auto;
  background: ${T.colorPanelBg};
  color: ${T.colorTextPrimary};
  border: 1rem solid ${T.colorBorderSoft};
  border-radius: ${T.radiusLg};
  padding: ${T.space3} ${T.space3} ${T.space2};
  font-size: ${T.fontSizeMd};
  line-height: ${T.lineHeightBase};
  pointer-events: auto;
  box-shadow: ${T.shadowMd};
`;

export const PanelTitle = styled.h3`
  font-size: ${T.fontSizeLg};
  font-weight: ${T.fontWeightBold};
  margin: 0 0 ${T.space1} 0;
  padding-bottom: ${T.space1};
  border-bottom: 1rem solid ${T.colorBorderSoft};
`;

export const PanelMeta = styled.div`
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  color: ${T.colorTextMuted};
  font-size: ${T.fontSizeSm};
  margin-bottom: ${T.space2};

  > * {
    margin-right: ${T.space1};
  }
  > *:last-child {
    margin-right: 0;
  }
`;

// Emphasized inline value inside PanelMeta — used for the current-style name
// next to "default style:". Plain <span> instead of <b>/<strong> so cohtml's
// font cascade doesn't promote it to a heavier face we don't have.
export const PanelMetaValue = styled.span`
  color: ${T.colorTextPrimary};
  font-weight: ${T.fontWeightMedium};
`;

// Thin vertical separator between PanelMeta items. Pure CSS — no glyph that
// might fall back to a missing-character square (looking at you, U+00B7).
export const PanelMetaSep = styled.span`
  display: inline-block;
  width: 1rem;
  height: 10rem;
  background: ${T.colorBorderMid};
  margin: 0 ${T.space1};
`;

export const PanelHint = styled.div`
  color: ${T.colorTextMuted};
  font-size: ${T.fontSizeSm};
  padding: ${T.space2} ${T.space1};
  // No font-style: italic — cohtml has no italic variant of the CS2 font,
  // so it falls back to empty squares. Visual distinction is carried by the
  // muted colour + slightly smaller size instead.
`;

export const PanelList = styled.div`
  display: flex;
  flex-direction: column;
`;

// ── Line accordion row ─────────────────────────────────────────────────

export const LineRowOuter = styled.div<{ $expanded?: boolean }>`
  background: ${(p) => (p.$expanded ? T.colorRowBgActive : T.colorRowBg)};
  border: 1rem solid ${(p) => (p.$expanded ? T.colorAccentSoft : "transparent")};
  border-radius: ${T.radiusMd};
  overflow: hidden;
  margin-bottom: ${T.space1};
  transition: background ${T.transitionFast}, border-color ${T.transitionFast};

  &:hover {
    background: ${(p) => (p.$expanded ? T.colorRowBgActive : T.colorRowBgHover)};
    border-color: ${(p) => (p.$expanded ? T.colorAccentSoft : T.colorBorderSoft)};
  }
`;

export const LineHeader = styled.div`
  display: flex;
  align-items: center;
  padding: ${T.space1} ${T.space2};
  cursor: pointer;
  user-select: none;

  > * {
    margin-right: ${T.space2};
  }
  > *:last-child {
    margin-right: 0;
  }
`;

export const LineChevron = styled.span<{ $open?: boolean }>`
  color: ${T.colorTextMuted};
  display: inline-flex;
  align-items: center;
  transition: transform 0.15s ease;
  transform-origin: center;
  transform: ${(p) => (p.$open ? "rotate(90deg)" : "rotate(0deg)")};
`;

export const LineTitle = styled.span`
  flex: 1;
  font-weight: ${T.fontWeightMedium};
`;

export const LineStyleTag = styled.span`
  font-size: ${T.fontSizeXs};
  color: ${T.colorTextMuted};
  text-transform: uppercase;
  letter-spacing: 0.4rem;
`;

export const LineSegCount = styled.span`
  font-size: ${T.fontSizeXs};
  color: ${T.colorTextMuted};
  min-width: 28rem;
  text-align: right;
  // Tabular-style numerals via font-variant — keeps "5/12" alignment without
  // requiring a monospace family (cohtml has no monospace font, setting one
  // would fall back to squares).
  font-variant-numeric: tabular-nums;
`;

export const LineBody = styled.div`
  padding: ${T.space1} ${T.space2} ${T.space2};
  border-top: 1rem solid ${T.colorBorderSoft};
`;

export const StyleRow = styled.div`
  display: flex;
  align-items: center;
  margin: ${T.space1} 0 ${T.space2};

  > * {
    flex: 1;
  }
`;

// ── Segment popover (floats in world space via portal) ─────────────────

export const PopoverRoot = styled.div`
  position: fixed;
  transform: translate(-50%, -120%);
  display: flex;
  padding: 3rem;
  background: rgba(18, 22, 30, 0.92);
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusMd};
  pointer-events: auto;
  box-shadow: ${T.shadowSm};
  z-index: 999998;
`;

export const PopoverBtn = styled.button`
  width: 24rem;
  height: 24rem;
  display: flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: ${T.colorTextPrimary};
  border: 1rem solid transparent;
  border-radius: ${T.radiusSm};
  cursor: pointer;
  pointer-events: auto;
  margin-right: 2rem;
  padding: 0;
  transition: background ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  &:last-child {
    margin-right: 0;
  }

  &:hover {
    background: ${T.colorRowBgHover};
    border-color: ${T.colorBorderMid};
    color: ${T.colorAccent};
  }
`;

// ── Segment row inside expanded line ───────────────────────────────────

export const SegmentRow = styled.div<{ $hidden?: boolean }>`
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: ${T.space1} ${T.space2};
  margin: 2rem 0;
  border-radius: ${T.radiusSm};
  cursor: pointer;
  font-size: ${T.fontSizeSm};
  opacity: ${(p) => (p.$hidden ? 0.45 : 1)};
  transition: background ${T.transitionFast}, opacity ${T.transitionFast};

  &:hover {
    background: ${T.colorRowBgHover};
  }
`;

export const SegmentInfo = styled.span`
  // tabular-nums for aligned digits without a monospace family — see LineSegCount.
  font-variant-numeric: tabular-nums;
`;

export const SegmentIndicator = styled.span`
  width: 18rem;
  display: flex;
  align-items: center;
  justify-content: center;
  color: ${T.colorTextMuted};
`;

// ── Buttons (generic) ──────────────────────────────────────────────────

export const Btn = styled.button<{ $danger?: boolean; $full?: boolean }>`
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: ${(p) => (p.$danger ? T.colorDanger : "inherit")};
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusSm};
  padding: ${T.space1} ${T.space2};
  font-size: ${T.fontSizeSm};
  cursor: pointer;
  pointer-events: auto;
  ${(p) => p.$full && "width: 100%; margin-top: " + T.space2 + ";"}
  transition: background ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  // Icon inside the button: align to text baseline (cohtml's default inline
  // baseline put SVG flush to the top of the button box otherwise), give it a
  // small right margin when followed by a label.
  > svg {
    flex-shrink: 0;
    display: block;
    margin-right: ${T.space1};
  }
  > svg:only-child {
    margin-right: 0;
  }

  &:hover {
    background: ${(p) => (p.$danger ? T.colorDangerSoft : T.colorRowBgHover)};
    border-color: ${(p) => (p.$danger ? T.colorDanger : T.colorBorderStrong)};
  }
`;
