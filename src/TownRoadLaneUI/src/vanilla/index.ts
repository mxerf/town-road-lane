// Game UI components and themes reused through getModule() (the approach Road Builder and
// other mods take), so our controls get the native look, focus handling, sounds and theme.
//
// Paths into game-ui/* are not a public API and can change with a game patch; every
// getModule() call lives in this folder so an update only touches it.

export { vanillaDropdownTheme } from "./theme";
export { VanillaDropdown } from "./Dropdown";
export type { VanillaDropdownOption } from "./Dropdown";
