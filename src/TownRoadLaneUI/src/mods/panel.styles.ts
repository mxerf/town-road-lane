// Styled components for the panel and popovers.
//
// cohtml constraints that apply to every rule below:
//   - No `gap`: cohtml's flexbox does not always honour it, especially when nested. Use
//     margins.
//   - No `calc()`.
//   - Popovers use `position: fixed` and are portalled to document.body. TTE avoids fixed
//     positioning, so this is the first thing to check if popovers break.
//   - var(--foo) only for variables the game defines (through tokens.ts), as Traffic, Road
//     Builder and TTE do. Custom props we declare ourselves resolve empty, and var()
//     fallback values are ignored.
//   - var() only in longhands (color, background-color). In shorthands (background, border,
//     border-color, border-radius) the whole declaration is dropped, with "Custom CSS
//     expressions are not supported in shorthand declaration" in Player.log.
//   - No `position: sticky`: Player.log reports "Unable to parse declaration".
//   - Icons draw with `stroke: currentColor` (IconBase), so a custom tint needs an explicit
//     `color` on the parent; otherwise the colour comes from the panel root.

import { styled } from "../styles/styled";
import { tokens as T } from "../styles/tokens";

// Main panel (GameTopRight).

export const Panel = styled.div`
  position: absolute;
  // 56rem clears the game's top-right buttons (advisor, settings), which sit outside the
  // GameTopRight slot.
  top: 56rem;
  right: ${T.space2};
  width: ${T.panelWidth};
  max-height: ${T.panelMaxHeight};
  overflow-y: auto;
  background: ${T.colorPanelBg};
  backdrop-filter: ${T.backdropBlur};
  color: ${T.colorTextPrimary};
  border: 1rem solid ${T.colorBorderSoft};
  border-radius: ${T.radiusLg};
  padding: ${T.space3} ${T.space3} ${T.space2};
  font-size: ${T.fontSizeMd};
  line-height: ${T.lineHeightBase};
  pointer-events: auto;
  box-shadow: ${T.shadowMd};
`;

// Header, status and drawing controls, divided from the lists below. Despite the name it
// is not sticky (cohtml can't parse position: sticky); the whole panel scrolls as one.
export const PanelStickyChrome = styled.div`
  padding-bottom: ${T.space2};
  margin-bottom: ${T.space2};
  border-bottom: 1rem solid ${T.colorBorderSoft};
`;

export const PanelTitle = styled.h3`
  font-size: ${T.fontSizeXl};
  font-weight: ${T.fontWeightBold};
  margin: 0 0 ${T.space1} 0;
`;

// Title on the left, close button on the right.
export const PanelHeaderRow = styled.div`
  display: flex;
  align-items: center;

  > h3 {
    flex: 1;
    margin-bottom: 0;
  }
`;

export const CloseBtn = styled.button`
  width: 22rem;
  height: 22rem;
  display: flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: ${T.colorTextMuted};
  border: 1rem solid transparent;
  border-radius: ${T.radiusSm};
  cursor: pointer;
  pointer-events: auto;
  padding: 0;
  transition: background ${T.transitionFast}, color ${T.transitionFast}, border-color ${T.transitionFast};

  &:hover {
    background: ${T.colorRowBgHover};
    border-color: ${T.colorBorderMid};
    color: ${T.colorTextPrimary};
  }
`;

// Mode switch (Lines / Area).

export const ModeRow = styled.div`
  display: flex;
  margin: ${T.space2} 0 0;

  > * {
    flex: 1;
    margin-right: ${T.space1};
  }
  > *:last-child {
    margin-right: 0;
  }
`;

// The active mode gets an accent fill with dark text, as the game marks selected toggles.
export const ModeBtn = styled.button<{ $active?: boolean }>`
  display: flex;
  align-items: center;
  justify-content: center;
  padding: ${T.space1} ${T.space2};
  // background-color (longhand) — the active fill is a var()-based token.
  background-color: ${(p) => (p.$active ? T.colorAccent : T.colorBtnBg)};
  color: ${(p) => (p.$active ? T.colorTextOnAccent : T.colorTextMuted)};
  border: 1rem solid transparent;
  border-radius: ${T.radiusSm};
  font-size: ${T.fontSizeSm};
  font-weight: ${T.fontWeightMedium};
  cursor: pointer;
  pointer-events: auto;
  transition: background-color ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  > svg {
    margin-right: ${T.space1};
    flex-shrink: 0;
  }

  &:hover {
    background-color: ${(p) => (p.$active ? "var(--accentColorNormal-hover)" : T.colorBtnBgHover)};
    color: ${(p) => (p.$active ? T.colorTextOnAccent : T.colorTextPrimary)};
  }
`;

// Area draft box, shown while AreaSelecting. Accent-tinted so the mode is hard to miss; it
// lists the click hints for adding, undoing and closing.
export const DraftBox = styled.div`
  background: ${T.colorAccentDim};
  border: 1rem solid ${T.colorAccentSoft};
  border-radius: ${T.radiusMd};
  padding: ${T.space2};
  margin: ${T.space2} 0 0;
`;

export const DraftHint = styled.div`
  font-size: ${T.fontSizeXs};
  color: ${T.colorTextMuted};
  margin-bottom: 2rem;
`;

// Label on the left, control on the right taking the remaining width. Used by the next line
// style and next area fill pickers.
export const FieldRow = styled.div`
  display: flex;
  align-items: center;
  margin: ${T.space2} 0 0;

  > *:last-child {
    flex: 1;
  }
`;

export const FieldLabel = styled.span`
  font-size: ${T.fontSizeSm};
  color: ${T.colorTextMuted};
  margin-right: ${T.space2};
  flex-shrink: 0;
`;

// Section titles (Lines / Areas).
export const SectionTitle = styled.div`
  font-size: ${T.fontSizeXs};
  font-weight: ${T.fontWeightBold};
  color: ${T.colorTextDim};
  text-transform: uppercase;
  letter-spacing: 0.6rem;
  margin: ${T.space3} 0 ${T.space1};
`;

// Status line under the header: an accent dot and the next step for the current
// MarkingNodeToolSystem.State.
export const StatusRow = styled.div`
  display: flex;
  align-items: center;
  margin-top: ${T.space1};
  font-size: ${T.fontSizeSm};
  color: ${T.colorTextPrimary};
`;

export const StatusDot = styled.span`
  display: block;
  flex-shrink: 0;
  width: 6rem;
  height: 6rem;
  border-radius: 3rem;
  // background-color, NOT background: the accent token is a var(), and cohtml
  // drops var() inside shorthand declarations.
  background-color: ${T.colorAccent};
  margin-right: ${T.space2};
`;

// Label and toggle row for node settings (hide vanilla markings).
export const ToggleRow = styled.div`
  display: flex;
  align-items: center;
  margin-top: ${T.space1};

  > *:first-child {
    flex: 1;
  }
`;

// Square icon toggle; $active is accent-tinted.
export const IconToggleBtn = styled.button<{ $active?: boolean }>`
  width: 26rem;
  height: 26rem;
  display: flex;
  align-items: center;
  justify-content: center;
  background: ${(p) => (p.$active ? T.colorAccentDim : T.colorBtnBg)};
  color: ${(p) => (p.$active ? T.colorAccent : T.colorTextPrimary)};
  border: 1rem solid ${(p) => (p.$active ? T.colorAccentSoft : T.colorBorderSoft)};
  border-radius: ${T.radiusSm};
  cursor: pointer;
  pointer-events: auto;
  padding: 0;
  transition: background ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  &:hover {
    background: ${(p) => (p.$active ? T.colorAccentDim : T.colorBtnBgHover)};
    border-color: ${T.colorBorderMid};
  }
`;

// Header of the collapsible hotkeys list: SectionTitle look plus a chevron.
export const FoldoutHeader = styled.div`
  display: flex;
  align-items: center;
  cursor: pointer;
  user-select: none;
  font-size: ${T.fontSizeXs};
  font-weight: ${T.fontWeightBold};
  color: ${T.colorTextDim};
  text-transform: uppercase;
  letter-spacing: 0.6rem;
  margin: ${T.space3} 0 ${T.space1};

  > *:first-child {
    margin-right: ${T.space1};
  }

  &:hover {
    color: ${T.colorTextMuted};
  }
`;

// Entity id at the bottom of the node block, for bug reports. Deliberately the least
// prominent text on the panel.
export const NodeIdText = styled.div`
  margin-top: ${T.space2};
  font-size: ${T.fontSizeXs};
  color: ${T.colorTextDim};
  font-variant-numeric: tabular-nums;
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

// Line accordion row. $gameHovered (cursor over the line in the world) looks the same as a
// panel hover; the expanded state wins with its accent border.
export const LineRowOuter = styled.div<{ $expanded?: boolean; $gameHovered?: boolean }>`
  background: ${(p) =>
    p.$expanded ? T.colorRowBgActive : p.$gameHovered ? T.colorRowBgHover : T.colorRowBg};
  border: 1rem solid ${(p) =>
    p.$expanded ? T.colorAccentSoft : p.$gameHovered ? T.colorBorderSoft : "transparent"};
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
  display: flex;
  align-items: center;
  transition: transform 0.15s ease;
  transform-origin: center;
  transform: ${(p) => (p.$open ? "rotate(90deg)" : "rotate(0deg)")};
`;

export const LineTitle = styled.span`
  flex: 1;
  font-weight: ${T.fontWeightMedium};
`;

// Slot for the style preview (LineStylePreview / AreaStylePreview). display: block on the
// svg keeps the inline baseline gap from skewing vertical centering.
export const SwatchWrap = styled.span`
  display: flex;
  align-items: center;

  > svg {
    display: block;
  }
`;

// "G87" mark after a line swatch: G87 and vanilla variants have the same preview.
export const G87Mark = styled.span`
  margin-left: 3rem;
  font-size: 9rem;
  color: ${T.colorTextMuted};
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

// Always mounted so max-height can transition. The open max-height is a generous
// overshoot (content rarely exceeds ~1500rem, 50+ segments). Border and padding are drawn
// only when open, so no 1rem strip shows through when collapsed.
export const LineBody = styled.div<{ $open?: boolean }>`
  overflow: hidden;
  max-height: ${(p) => (p.$open ? "3000rem" : "0")};
  padding: ${(p) => (p.$open ? `${T.space1} ${T.space2} ${T.space2}` : `0 ${T.space2}`)};
  border-top: ${(p) => (p.$open ? `1rem solid ${T.colorBorderSoft}` : "0 solid transparent")};
  transition: max-height ${T.transitionNormal}, padding ${T.transitionNormal}, border-top-width ${T.transitionNormal};
`;

export const StyleRow = styled.div`
  display: flex;
  align-items: center;
  margin: ${T.space1} 0 ${T.space2};

  > * {
    flex: 1;
  }
`;

// Curvature stepper, below the style dropdown in an expanded line.

export const CurvRow = styled.div`
  display: flex;
  align-items: center;
  margin: 0 0 ${T.space2};
`;

export const CurvLabel = styled.span`
  flex: 1;
  font-size: ${T.fontSizeSm};
  color: ${T.colorTextMuted};
`;

// Text field for the percent, committed on Enter or blur. <input type=range> doesn't work in
// cohtml (the thumb never moves), and type="number" spinners are unreliable too, so it is
// type="text" with a digit filter in JS.
export const CurvInput = styled.input`
  width: 44rem;
  flex-shrink: 0;
  padding: ${T.space1};
  text-align: right;
  background: ${T.colorBtnBg};
  color: ${T.colorTextPrimary};
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusSm};
  font-size: ${T.fontSizeSm};
  font-variant-numeric: tabular-nums;
  pointer-events: auto;
`;

export const CurvUnit = styled.span`
  flex-shrink: 0;
  margin-left: 2rem;
  color: ${T.colorTextMuted};
  font-size: ${T.fontSizeSm};
`;

// −/+ buttons around the input: ±1, Shift ±10, Ctrl ±5.
export const CurvStepBtn = styled.button`
  width: 20rem;
  height: 22rem;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  margin: 0 3rem;
  background: ${T.colorBtnBg};
  color: ${T.colorTextMuted};
  border: 1rem solid ${T.colorBorderSoft};
  border-radius: ${T.radiusSm};
  cursor: pointer;
  pointer-events: auto;
  padding: 0;
  font-size: ${T.fontSizeSm};
  line-height: 1;
  transition: background ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  &:hover {
    background: ${T.colorBtnBgHover};
    border-color: ${T.colorBorderMid};
    color: ${T.colorTextPrimary};
  }
`;

// Shown only while the value differs from the 50% default.
export const CurvResetBtn = styled.button`
  width: 22rem;
  height: 22rem;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  margin-left: ${T.space1};
  background: ${T.colorBtnBg};
  color: ${T.colorTextMuted};
  border: 1rem solid ${T.colorBorderSoft};
  border-radius: ${T.radiusSm};
  cursor: pointer;
  pointer-events: auto;
  padding: 0;
  transition: background ${T.transitionFast}, border-color ${T.transitionFast}, color ${T.transitionFast};

  &:hover {
    background: ${T.colorBtnBgHover};
    border-color: ${T.colorBorderMid};
    color: ${T.colorTextPrimary};
  }
`;


// World-space popovers, portalled to document.body.

// positionRegistry also writes the transform inline (translate plus distance scale); this
// declaration only covers the frame before the first sync. Keep the translate in both.
export const PopoverRoot = styled.div`
  position: fixed;
  transform: translate(-50%, -120%);
  display: flex;
  align-items: center;
  padding: 4rem;
  background: ${T.colorPanelBg};
  backdrop-filter: ${T.backdropBlur};
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusMd};
  pointer-events: auto;
  cursor: pointer;
  box-shadow: ${T.shadowMd};
  z-index: 999998;
`;

// Collapsed popover: a dot, white when visible and red when hidden (like the red ghost the
// overlay draws for hidden segments). Hovering the popover swaps it for the button row.
export const PopoverMarker = styled.span<{ $hidden?: boolean }>`
  display: block;
  width: 12rem;
  height: 12rem;
  border-radius: 6rem;
  background-color: ${(p) => (p.$hidden ? T.colorDanger : "rgba(240, 251, 255, 0.92)")};
  border: 1rem solid rgba(10, 14, 20, 0.85);
`;

// Padding lives on PopoverRoot, not the buttons, so the buttons sit close together.
export const PopoverBtn = styled.button<{ $active?: boolean }>`
  width: 30rem;
  height: 30rem;
  display: flex;
  align-items: center;
  justify-content: center;
  background: ${(p) => (p.$active ? T.colorAccentDim : "transparent")};
  color: ${(p) => (p.$active ? T.colorAccent : T.colorTextPrimary)};
  border: 1rem solid ${(p) => (p.$active ? T.colorAccentSoft : "transparent")};
  border-radius: ${T.radiusSm};
  cursor: pointer;
  pointer-events: auto;
  margin-right: 3rem;
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

// Fixed-width slot for a Dropdown in the popover row. Dropdown is width: 100%, and in a flex
// row it would otherwise collapse to its content or stretch the row.
export const PopoverDropdownWrap = styled.div`
  width: 170rem;
  margin-right: 3rem;
`;

// Segment row in an expanded line. Hidden segments get a red left border and tint as well as
// dimming, which alone is easy to miss in a long list. Only the left border, so hidden and
// visible rows stay aligned.
export const SegmentRow = styled.div<{ $hidden?: boolean }>`
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: ${T.space1} ${T.space2};
  margin: 2rem 0;
  border-radius: ${T.radiusSm};
  border-left: 3rem solid ${(p) => (p.$hidden ? T.colorDanger : "transparent")};
  background: ${(p) => (p.$hidden ? T.colorDangerSoft : "transparent")};
  cursor: pointer;
  font-size: ${T.fontSizeSm};
  opacity: ${(p) => (p.$hidden ? 0.7 : 1)};
  transition: background ${T.transitionFast}, opacity ${T.transitionFast}, border-color ${T.transitionFast};

  &:hover {
    background: ${(p) => (p.$hidden ? T.colorDangerSoft : T.colorRowBgHover)};
  }
`;

export const SegmentInfo = styled.span`
  flex: 1;
`;

// Segment length, right-aligned and separate from the name so the two don't run together.
export const SegmentLen = styled.span`
  flex-shrink: 0;
  margin-right: ${T.space2};
  color: ${T.colorTextMuted};
  font-size: ${T.fontSizeXs};
  font-variant-numeric: tabular-nums;
`;

export const SegmentIndicator = styled.span`
  width: 18rem;
  display: flex;
  align-items: center;
  justify-content: center;
  color: ${T.colorTextMuted};
`;

// Buttons.

// Cancel and confirm for inline delete confirmation. Inline rather than a modal dialog
// because overlay positioning in cohtml is fiddly.
export const ConfirmRow = styled.div`
  display: flex;
  margin-top: ${T.space2};

  > * {
    flex: 1;
    margin-right: ${T.space1};
  }
  > *:last-child {
    margin-right: 0;
  }
`;

// Hotkey hints.

export const HintsBox = styled.div`
  margin-top: ${T.space3};
  padding-top: ${T.space2};
  border-top: 1rem solid ${T.colorBorderSoft};
`;

export const HintRow = styled.div`
  display: flex;
  align-items: center;
  margin-bottom: 3rem;
  font-size: ${T.fontSizeXs};
  color: ${T.colorTextMuted};
`;

// Key chip. min-width keeps the descriptions aligned across rows. display: flex because
// cohtml can't parse inline-block.
export const HintKey = styled.span`
  display: flex;
  align-items: center;
  justify-content: center;
  min-width: 40rem;
  padding: 1rem 5rem;
  margin-right: ${T.space2};
  background: ${T.colorBtnBg};
  border: 1rem solid ${T.colorBorderSoft};
  border-radius: ${T.radiusSm};
  color: ${T.colorTextMuted};
  font-variant-numeric: tabular-nums;
`;

export const Btn = styled.button<{ $danger?: boolean; $full?: boolean }>`
  display: flex;
  align-items: center;
  justify-content: center;
  // Filled, not ghost — outline-only buttons read as wireframes next to the
  // game's own (filled) buttons.
  background: ${T.colorBtnBg};
  // Explicit colour: cohtml cannot parse color inherit and falls back to black text.
  color: ${(p) => (p.$danger ? T.colorDanger : T.colorTextPrimary)};
  border: 1rem solid ${T.colorBorderSoft};
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
    background: ${(p) => (p.$danger ? T.colorDangerSoft : T.colorBtnBgHover)};
    border-color: ${(p) => (p.$danger ? T.colorDanger : T.colorBorderMid)};
  }
`;
