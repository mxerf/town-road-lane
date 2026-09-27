// Single import point for styled-components.
//
// styled-components rather than SCSS modules: in cohtml, a var(--foo) nested inside an SCSS
// variable resolves to an empty string (transparent backgrounds all over the panel).
// Template literals inline the values instead. The pattern follows TrafficToolEssentials.
// Other cohtml gaps (no `gap`, no `position: fixed`) are noted on the individual styled
// components.

export { default as styled, css, keyframes } from "styled-components";
export type { DefaultTheme } from "styled-components";
