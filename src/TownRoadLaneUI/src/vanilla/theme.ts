// Vanilla CS2 SCSS theme modules, loaded dynamically. These export CSS class
// names (compiled from CS2's own .module.scss bundles) that we pass to the
// matching cs2/ui components as their `theme` prop. Using the vanilla theme
// gives us correct typography, borders, hover/active states, focus rings,
// and dropdown chevron art — all for free, and the look stays in sync if
// the game restyles its own UI.

import { getModule } from "cs2/modding";

// `as any` because the module exports an opaque map of generated class names
// (the SCSS module hash format) — we never inspect the values, we just pass
// the whole object to <Dropdown theme={...} /> so cs2/ui can attach them.
export const vanillaDropdownTheme = getModule(
  "game-ui/menu/themes/dropdown.module.scss",
  "classes",
) as any;
