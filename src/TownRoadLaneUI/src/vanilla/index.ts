// VanillaComponentResolver — single import surface for CS2's built-in UI
// modules loaded dynamically via getModule(). This is the "pro" pattern used
// by RoadBuilder and many other production CS2 mods: instead of fighting
// cohtml with our own re-implementations of Dropdown/Toggle/etc, we reach
// into the game's own bundled modules and reuse the exact components the
// vanilla UI uses. That guarantees pixel-perfect "native look" + correct
// keyboard focus behaviour + sounds + theme switching.
//
// Caveat: paths into game-ui/* are not a public API and can break on game
// patches. We isolate every getModule() call here so a single update only
// touches this file. If a path 404s in a future game version, the symptom
// will be a typed null + console error — not a hard crash — because we
// always pair the call with a fallback (see SafeDropdown).

export { vanillaDropdownTheme } from "./theme";
export { VanillaDropdown } from "./Dropdown";
export type { VanillaDropdownOption } from "./Dropdown";
