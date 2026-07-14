// Design tokens — single source of truth for colors, spacing, typography, and
// motion across the TownRoadLane UI. TS object so React components and styled
// templates import the same values.
//
// Stage 5e (native look): surface/text/accent tokens now resolve to the GAME's
// CSS custom properties (var(--panelColorDark) etc., defined by CS2's root
// stylesheet and swapped by the game per theme/accent setting). Referencing
// game-defined vars from styled-components values is the proven pattern from
// Traffic / RoadBuilder / TTE — the earlier "no var() indirection" rule was
// about OUR OWN custom props declared in SCSS (those did resolve empty).
// cohtml quirk: var(--x, fallback) fallbacks are IGNORED — every var used here
// must exist in the game stylesheet (verified against Cities2_Data/Content/
// Game/UI/index.css). Tokens kept as literals either have no stable game
// counterpart (alpha overlays) or are fed into SVG presentation attributes
// (Icon stroke=...), where var() does not resolve — those use the game's
// palette values verbatim.

export const tokens = {
  // ── Colors ────────────────────────────────────────────────────────────
  // Surfaces. Panel bg is a near-opaque literal in the game's navy family —
  // NOT var(--panelColorDark): the game runs that at ~0.7 opacity, which reads
  // fine on sparse info panels but washes out a dense tool panel over bright
  // terrain (user feedback 2026-07-14; TTE fights the same issue by locally
  // overriding --panelOpacityDark to 0.85+). Theme-following stays in accent
  // and text tokens. Rows are tinted by alpha-overlaying white so they pick
  // up the background hue automatically.
  colorPanelBg:        "rgba(20, 26, 36, 0.96)",
  // Near-opaque dark surface for floating layers (dropdown menus, tooltips)
  // that can overlap OTHER UI — glass + blur there smears the content behind
  // into unreadable colour blotches, so they get a solid card instead.
  colorSurfaceSolid:   "rgba(24, 30, 40, 0.98)",
  colorRowBg:          "rgba(255, 255, 255, 0.035)",
  colorRowBgHover:     "rgba(255, 255, 255, 0.08)",
  colorRowBgActive:    "rgba(70, 140, 255, 0.22)",

  // Button fills — subtle solid fill idle → brighter on hover. Filled (not
  // ghost/outline) is what makes controls read as native CS2 buttons.
  colorBtnBg:          "rgba(255, 255, 255, 0.07)",
  colorBtnBgHover:     "rgba(255, 255, 255, 0.14)",
  // Dark text for accent-filled controls (game --focusedTextColorDark).
  colorTextOnAccent:   "#141B22",

  // Glassmorphism blur the game applies to its own panels — attach as
  // `backdrop-filter` wherever colorPanelBg is the surface.
  backdropBlur:        "var(--panelBlur)",

  // Borders. Soft = idle separators. Mid = interactive elements. Strong = focus.
  colorBorderSoft:     "rgba(255, 255, 255, 0.10)",
  colorBorderMid:      "rgba(255, 255, 255, 0.18)",
  colorBorderStrong:   "rgba(255, 255, 255, 0.35)",

  // Text. Primary/muted follow the game's own text roles (#F0FBFF and its
  // 60%-alpha secondary); dim is the same tone at 40% (no stable game var).
  colorTextPrimary:    "var(--normalTextColor)",
  colorTextMuted:      "var(--menuText2Normal)",
  colorTextDim:        "rgba(240, 251, 255, 0.4)",

  // Accents. accentColorNormal follows the player's accent-color setting;
  // LightHighlight is the game's half-alpha highlight of the same hue.
  // Status colors are the game's literal values (single-valued in index.css,
  // and they must stay literals — they feed Icon stroke attributes).
  colorAccent:         "var(--accentColorNormal)",
  colorAccentSoft:     "var(--accentColorLightHighlight)",
  colorAccentDim:      "rgba(90, 170, 255, 0.18)",
  colorDanger:         "#e95f4a",
  colorDangerSoft:     "rgba(233, 95, 74, 0.22)",
  colorSuccess:        "#8bdb46",
  colorWarning:        "#ffa42d",

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
  // Panel-level rounding follows the game theme (--panelRadius is 4–14rem
  // depending on theme, 0 in the square theme). Sm/Md stay literal — inner
  // radii up to 12rem would look bloated on our 22–30rem buttons.
  radiusSm: "3rem",
  radiusMd: "4rem",
  radiusLg: "var(--panelRadius)",
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
