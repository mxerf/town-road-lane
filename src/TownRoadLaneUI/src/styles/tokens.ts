// Design tokens shared by the React components and the styled templates.
//
// Text, accent and blur tokens point at the game's own CSS custom properties (defined in
// Cities2_Data/Content/Game/UI/index.css), so they follow the player's theme and accent
// setting. Game-defined vars work from styled-components values, as in Traffic, Road
// Builder and TTE; custom props declared in our own SCSS resolve empty.
// cohtml ignores var(--x, fallback) fallbacks, so every var used here must exist in the
// game stylesheet. The remaining literals either have no stable game counterpart (alpha
// overlays) or feed SVG presentation attributes (Icon stroke), where var() does not
// resolve; those copy the game's palette values.

export const tokens = {
  // Colors.
  // The panel background is a near-opaque literal in the game's navy family rather than
  // var(--panelColorDark): the game runs that at ~0.7 opacity, which washes out a dense
  // tool panel over bright terrain (TTE works around the same thing by overriding
  // --panelOpacityDark). Rows are tinted with white overlays so they pick up the
  // background hue.
  colorPanelBg:        "rgba(20, 26, 36, 0.96)",
  // Solid surface for floating layers (dropdown menus, tooltips) that overlap other UI:
  // glass with blur smears the content behind them into unreadable blotches.
  colorSurfaceSolid:   "rgba(24, 30, 40, 0.98)",
  colorRowBg:          "rgba(255, 255, 255, 0.035)",
  colorRowBgHover:     "rgba(255, 255, 255, 0.08)",
  colorRowBgActive:    "rgba(70, 140, 255, 0.22)",

  // Filled rather than outlined buttons, so they read as native CS2 controls.
  colorBtnBg:          "rgba(255, 255, 255, 0.07)",
  colorBtnBgHover:     "rgba(255, 255, 255, 0.14)",
  // Dark text for accent-filled controls (game --focusedTextColorDark).
  colorTextOnAccent:   "#141B22",

  // The blur the game applies to its own panels; use as `backdrop-filter` wherever
  // colorPanelBg is the surface.
  backdropBlur:        "var(--panelBlur)",

  // Borders: soft for separators, mid for interactive elements, strong for focus.
  colorBorderSoft:     "rgba(255, 255, 255, 0.10)",
  colorBorderMid:      "rgba(255, 255, 255, 0.18)",
  colorBorderStrong:   "rgba(255, 255, 255, 0.35)",

  // Text. Primary/muted follow the game's own text roles (#F0FBFF and its
  // 60%-alpha secondary); dim is the same tone at 40% (no stable game var).
  colorTextPrimary:    "var(--normalTextColor)",
  colorTextMuted:      "var(--menuText2Normal)",
  colorTextDim:        "rgba(240, 251, 255, 0.4)",

  // Accents. accentColorNormal follows the player's accent-color setting.
  // cohtml rejects var() inside shorthand declarations (background, border, border-color,
  // border-radius; Player.log says "Custom CSS expressions are not supported in shorthand
  // declaration"), so colorAccent may only go into longhands such as color and
  // background-color. colorAccentSoft is used in border shorthands, so it is the literal
  // value of the game's --accentColorLightHighlight (#9ee2fc80). Status colors are
  // literals because they feed Icon stroke attributes.
  colorAccent:         "var(--accentColorNormal)",
  colorAccentSoft:     "rgba(158, 226, 252, 0.5)",
  colorAccentDim:      "rgba(90, 170, 255, 0.18)",
  colorDanger:         "#e95f4a",
  colorDangerSoft:     "rgba(233, 95, 74, 0.22)",
  colorSuccess:        "#8bdb46",
  colorWarning:        "#ffa42d",

  // Spacing.
  // In cohtml 1rem is one game-coordinate pixel, not 1/16 of the root font size. The
  // game scales rem with display DPI and the UI scale setting, so rem everywhere keeps
  // the panel in step with that setting; px stays fixed and is wrong on 4K.
  space1:  "4rem",
  space2:  "8rem",
  space3:  "12rem",
  space4:  "16rem",
  space5:  "20rem",
  space6:  "24rem",

  // Border radii. Literals, because border-radius is a shorthand and cohtml rejects var()
  // there; following --panelRadius would take four corner longhands.
  radiusSm: "3rem",
  radiusMd: "4rem",
  radiusLg: "6rem",
  radiusXl: "8rem",

  // Typography.
  // Never set font-family. cohtml has no Arial, sans-serif, monospace or Roboto; the
  // only font is the game's own SDF font (which covers Cyrillic, CJK and so on), and any
  // family override, even the generic "monospace", renders as empty squares. For aligned
  // digit columns use `font-variant-numeric: tabular-nums` instead of monospace. A custom
  // face would have to be bundled as a .ttf through webpack and @font-face.
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

  // Motion. Fast for hover and focus, normal for state changes. cohtml does not support
  // every easing curve, so only ease and ease-out are used.
  transitionFast:   "0.1s ease",
  transitionNormal: "0.18s ease-out",

  // Elevation.
  shadowSm: "0 2rem 8rem rgba(0, 0, 0, 0.5)",
  shadowMd: "0 4rem 20rem rgba(0, 0, 0, 0.5)",
  shadowLg: "0 8rem 32rem rgba(0, 0, 0, 0.6)",

  // Layout. calc() does not work in cohtml, so the panel's max height is a fixed value
  // that leaves the bottom toolbar visible on most screens; the panel scrolls past it.
  panelWidth:     "300rem",
  panelMaxHeight: "800rem",

  // Icon sizes.
  iconSizeXs: "10rem",
  iconSizeSm: "12rem",
  iconSizeMd: "14rem",
  iconSizeLg: "18rem",
} as const;

export type Token = keyof typeof tokens;
