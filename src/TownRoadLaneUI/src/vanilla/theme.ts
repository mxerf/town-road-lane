// Class-name maps from the game's own SCSS modules, passed to cs2/ui components as their
// `theme` prop so they look like the vanilla controls and follow game restyles.

import { getModule } from "cs2/modding";

// An opaque map of generated class names; it is only handed to cs2/ui, never read.
export const vanillaDropdownTheme = getModule(
  "game-ui/menu/themes/dropdown.module.scss",
  "classes",
) as any;
