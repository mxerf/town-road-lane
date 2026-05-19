// Re-export styled-components with the same import surface used across the
// project. Centralizing the import means a future engine swap (e.g. emotion,
// goober) only touches this file.
//
// Why styled-components and not SCSS modules:
//   - cohtml's CSS engine has subtle differences from a real browser. Nested
//     var(--foo) inside SCSS variables resolved as empty strings, which made
//     half the panel render with transparent backgrounds. Inlining values via
//     template literals dodges the indirection entirely.
//   - Theming flows naturally — pass the tokens object to template literals
//     and you get strongly-typed access with no global state.
//   - TTE uses this exact pattern in production, so it's been hardened against
//     the cohtml quirks list (no `gap`, no `position:fixed`, etc — see comments
//     on individual styled components).

export { default as styled, css, keyframes } from "styled-components";
export type { DefaultTheme } from "styled-components";
