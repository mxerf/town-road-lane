// Design tokens — single source of truth for colors, spacing, typography, and
// motion across the TownRoadLane UI. Mirrors the token pattern used by TTE's
// submenu-tokens.ts: TS object so React components and styled inline can
// import the same values that SCSS reads via CSS custom properties.
//
// Pattern: each token has a CSS variable name (the runtime contract) and a
// fallback value (the default we ship). SCSS reads var(--trl-foo, fallback);
// TS reads tokens.foo for inline styles. Future themeing (light mode, user
// overrides) just needs to swap the :root values — no recompile.

export const tokens = {
  // ── Colors ────────────────────────────────────────────────────────────
  // Surfaces. Panel uses CS2's dark glass aesthetic; rows are tinted by
  // alpha-overlaying white so they pick up the background hue automatically.
  colorPanelBg:        "rgba(18, 22, 30, 0.94)",
  colorPanelBgRaised:  "rgba(28, 34, 44, 0.95)",
  colorRowBg:          "rgba(255, 255, 255, 0.035)",
  colorRowBgHover:     "rgba(255, 255, 255, 0.08)",
  colorRowBgActive:    "rgba(70, 140, 255, 0.22)",

  // Borders. Soft = idle separators. Mid = interactive elements. Strong = focus.
  colorBorderSoft:     "rgba(255, 255, 255, 0.10)",
  colorBorderMid:      "rgba(255, 255, 255, 0.18)",
  colorBorderStrong:   "rgba(255, 255, 255, 0.35)",

  // Text. Primary on dark surfaces, muted for secondary info, dim for hints.
  colorTextPrimary:    "#e8eaed",
  colorTextMuted:      "#8c93a0",
  colorTextDim:        "#5d6470",

  // Accents. Blue is the canonical CS2 accent; danger red for destructive
  // actions; success green and warning amber kept for future states.
  colorAccent:         "#5aaaff",
  colorAccentSoft:     "rgba(90, 170, 255, 0.55)",
  colorAccentDim:      "rgba(90, 170, 255, 0.18)",
  colorDanger:         "#f47373",
  colorDangerSoft:     "rgba(244, 115, 115, 0.22)",
  colorSuccess:        "#7be07b",
  colorWarning:        "#ffb84d",

  // ── Spacing ───────────────────────────────────────────────────────────
  // cohtml's rem unit is the game-coordinate pixel, not 1/16 root font size.
  // 1rem == 1px when the UI is at default scale; the game scales rem up/down
  // based on display DPI + the user's UI scale setting. Using rem everywhere
  // means our panel respects that setting automatically (px is fixed and ends
  // up wrong on hi-DPI / 4K).
  space1:  "4rem",
  space2:  "8rem",
  space3:  "12rem",
  space4:  "16rem",
  space5:  "20rem",
  space6:  "24rem",

  // ── Border radii ──────────────────────────────────────────────────────
  radiusSm: "3rem",
  radiusMd: "4rem",
  radiusLg: "6rem",
  radiusXl: "8rem",

  // ── Typography ────────────────────────────────────────────────────────
  // DO NOT set font-family on any of our styled-components. cohtml does not
  // have Arial / sans-serif / monospace / Roboto bundled — the ONLY available
  // font is the one CS2 itself loads (the game's SDF font, which contains
  // Cyrillic / CJK / etc.). Any concrete family override — even the generic
  // "monospace" — falls through to empty squares.
  //
  // Leaving font-family unset means cohtml inherits the CS2 root font
  // automatically. For aligned-digit columns (segment lengths, counts) use
  // `font-variant-numeric: tabular-nums` on the styled component instead of
  // switching to monospace.
  //
  // If a future polish pass really needs a custom face, the path is to bundle
  // a .ttf via webpack + @font-face — but for now inheriting wins.
  fontSizeXs:  "10rem",
  fontSizeSm:  "11rem",
  fontSizeMd:  "12rem",
  fontSizeLg:  "13rem",
  fontSizeXl:  "15rem",
  fontWeightRegular: "400",
  fontWeightMedium:  "500",
  fontWeightBold:    "600",
  lineHeightTight: "1.2",
  lineHeightBase:  "1.4",

  // ── Motion ────────────────────────────────────────────────────────────
  // Two speeds: fast for micro-interactions (hover, focus), normal for state
  // changes (accordion expand, panel mount). cohtml supports basic CSS
  // transitions but not all easing curves — sticking to ease/ease-out.
  transitionFast:   "0.1s ease",
  transitionNormal: "0.18s ease-out",

  // ── Elevation ─────────────────────────────────────────────────────────
  shadowSm: "0 2rem 8rem rgba(0, 0, 0, 0.5)",
  shadowMd: "0 4rem 20rem rgba(0, 0, 0, 0.5)",
  shadowLg: "0 8rem 32rem rgba(0, 0, 0, 0.6)",

  // ── Layout constants ──────────────────────────────────────────────────
  // Panel uses a fixed max-height in viewport units; calc() doesn't work in
  // cohtml so we use a hard pixel value sized to leave the bottom toolbar
  // visible. 800rem ≈ enough for most screens; the panel scrolls internally
  // past that.
  panelWidth:     "300rem",
  panelMaxHeight: "800rem",

  // ── Icon sizing ───────────────────────────────────────────────────────
  iconSizeXs: "10rem",
  iconSizeSm: "12rem",
  iconSizeMd: "14rem",
  iconSizeLg: "18rem",
} as const;

export type Token = keyof typeof tokens;
