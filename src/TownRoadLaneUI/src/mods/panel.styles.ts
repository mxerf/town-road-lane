// Styled-components for the TownRoadLane UI surface.
//
// cohtml-safe checklist applied across every rule below:
//   - No `gap` — use margin-right / margin-bottom instead. cohtml's flex
//     impl doesn't always honour `gap`, especially nested.
//   - No `calc()` — pre-compute values in JS / hard-code.
//   - No `position: fixed` for popovers (TTE workaround) — but we DO use it
//     on .trl-popover via createPortal to document.body, which worked in
//     prior testing. Watch this if popovers regress.
//   - var(--foo) only for GAME-defined variables (via tokens.ts) — those are
//     set on CS2's root and resolve fine (Traffic/RoadBuilder/TTE pattern).
//     Never declare our own custom props (resolved empty in past testing),
//     and never rely on var() fallback values (cohtml ignores them).
//   - var() ONLY in true longhands (color, background-color). Shorthands
//     (background, border, border-color, border-radius) silently drop the
//     whole declaration: "Custom CSS expressions are not supported in
//     shorthand declaration" in Player.log.
//   - No `position: sticky` — "Unable to parse declaration" in Player.log;
//     it never worked, the panel scrolls as one block.
//   - SVG children inherit `color` via `fill: none; stroke: currentColor;`
//     in IconBase — make sure parent has explicit `color` set if you want
//     a custom tint. Default color cascades from the panel root.

import { styled } from "../styles/styled";
import { tokens as T } from "../styles/tokens";

// ── Main panel (GameTopRight) ──────────────────────────────────────────
// (The old custom ToolbarBtn is gone — the toolbar toggle now uses the
// vanilla cs2/ui FloatingButton, see toolbar-toggle-button.tsx.)

export const Panel = styled.div`
  position: absolute;
  // 56rem clears the game's own top-right button cluster (advisor + settings,
  // ~48rem tall) — they live outside the GameTopRight slot and our panel was
  // overlapping them at top: 8rem.
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

// Panel chrome — header, status and the drawing cluster, visually separated
// from the scrolling lists below. NOT sticky: cohtml can't parse
// position:sticky at all (Player.log "Unable to parse declaration"), so the
// panel has always scrolled as one block; this just keeps the divider.
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

// Header row: app title on the left, close button on the right. The title is
// the mod name (stable), the node id lives in PanelMeta below — users think
// "markings panel", not "node panel".
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

// ── Mode switch (Lines / Area segmented control) ───────────────────────

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

// Segmented control, vanilla-toggle style: the active mode is an ACCENT FILL
// with dark text (how the game marks selected toggles), the inactive one a
// subtle filled chip — no ghost outlines.
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

// ── Area draft box (visible while AreaSelecting) ───────────────────────

// Accent-tinted card so the "you are in a special mode" state is impossible
// to miss. Lists click hints because the polygon gesture has three distinct
// actions (add / undo / close) that are invisible otherwise.
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

// ── Labeled field row (label left, control right) ──────────────────────

// Shared shape for the "next line style" / "next area fill" pickers in the
// sticky chrome. The dropdown gets the remaining width.
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

// ── Section title (Lines / Areas list headers) ─────────────────────────

export const SectionTitle = styled.div`
  font-size: ${T.fontSizeXs};
  font-weight: ${T.fontWeightBold};
  color: ${T.colorTextDim};
  text-transform: uppercase;
  letter-spacing: 0.6rem;
  margin: ${T.space3} 0 ${T.space1};
`;

// Live tool-status line under the header: accent dot + one instruction that
// tracks MarkingNodeToolSystem.State. This is the panel's "what do I do now"
// voice — the recomposition's replacement for the old debug-flavoured meta
// row (node id + counters).
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

// Label-left / control-right row for stateful node settings (vanilla toggle).
export const ToggleRow = styled.div`
  display: flex;
  align-items: center;
  margin-top: ${T.space1};

  > *:first-child {
    flex: 1;
  }
`;

// Square icon toggle — the stateful cousin of PopoverBtn ($active = the
// override is engaged, accent-tinted).
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

// Clickable header for the collapsed hotkeys reference — SectionTitle's
// visual voice plus a chevron and a pointer cursor.
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

// Quiet entity-id line at the very bottom of the node block — power-user /
// bug-report material, deliberately the least prominent text on the panel.
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

// ── Line accordion row ─────────────────────────────────────────────────

// Game-hover state ($gameHovered) lights up the row when the cursor is over
// the line in the world — same visual weight as the panel-hover state, so the
// bridge feels symmetric. Expanded still wins visually (a stronger accent
// border) so it stays distinct from a casual hover.
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

// LineBody is always mounted to allow max-height transition (you can't animate
// from "absent" to "present"). $open toggles the collapsed state. max-height
// is set to a generous overshoot — content rarely exceeds ~1500rem (50+
// segments) and overshoot doesn't visually matter when the body is open.
// border-top + padding are only drawn when open to avoid a 1rem strip showing
// through when collapsed.
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

// ── Curvature stepper (inside expanded line, below the style dropdown) ─

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

// Plain numeric text field — <input type=range> turned out non-functional in
// CS2's cohtml build (thumb never moved), so: type the exact percent, commit
// on Enter/blur. type="text" + JS digit filter, not type="number" (spinner
// affordances are similarly untrustworthy here).
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

// The "%" unit after the input.
export const CurvUnit = styled.span`
  flex-shrink: 0;
  margin-left: 2rem;
  color: ${T.colorTextMuted};
  font-size: ${T.fontSizeSm};
`;

// Reset-to-default affordance next to the slider; rendered only while the
// value differs from the 50% default.
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


// ── Segment popover (floats in world space via portal) ─────────────────

export const PopoverRoot = styled.div`
  position: fixed;
  transform: translate(-50%, -120%);
  display: flex;
  padding: 4rem;
  background: ${T.colorPanelBg};
  backdrop-filter: ${T.backdropBlur};
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusMd};
  pointer-events: auto;
  box-shadow: ${T.shadowMd};
  z-index: 999998;
`;

// Bigger hit target (24 → 30rem) + clearer hover (background + border swap +
// accent colour on the icon). The icon size in the JSX call site bumps too
// (12 → 14rem). Padding lives on PopoverRoot, not the btn, so the buttons sit
// flush against each other for a tighter look.
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

// ── Segment row inside expanded line ───────────────────────────────────

// Hidden state (C5): in addition to dimming, paint a red left border + tint so
// the "this segment is suppressed" signal is unambiguous — opacity alone is
// easy to miss in a long list. Using border-left instead of full border keeps
// the row alignment with visible siblings.
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

// Right-aligned segment length, visually separated from the name — "Сегмент 1
// · 1.5м" in one run of text read as an unparseable jumble.
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

// ── Buttons (generic) ──────────────────────────────────────────────────

// Two-button row used by the inline delete-confirm pattern (B1). Cancel + the
// confirm button share width 50/50 with a small gap. Keeps users from losing a
// line to a single misclick — they have to confirm in the same gesture, but no
// modal dialog (cohtml's overlay positioning is fiddly).
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

// ── Hotkey hints footer ────────────────────────────────────────────────

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

// kbd-style chip for the key name. Fixed min-width keeps the description
// column aligned across rows. flex (not inline-block — unparseable in cohtml)
// with centered content; the parent HintRow is a flex row so this behaves as
// a fixed-min-width item.
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
  // Explicit colour, NOT "inherit" — cohtml can't parse color:inherit (see
  // Player.log "Unable to parse declaration"), so the button fell back to the
  // UA-default BLACK text and blended into the dark panel.
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
