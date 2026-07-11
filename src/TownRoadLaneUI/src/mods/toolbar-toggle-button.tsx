import { FloatingButton, Tooltip } from "cs2/ui";
import { useToolState, cmdActivateTool } from "../hooks/useToolState";
import { useT } from "../i18n";
import iconSrc from "../assets/marking-tool.svg";

// Toolbar toggle in the top-left game cluster. Uses the vanilla cs2/ui
// FloatingButton — the exact component the game (and mods like Traffic) use
// for tool toggles — so we inherit the native round look, hover/press sounds,
// the selected ring, and theme changes for free. The icon ships as an SVG
// asset next to the bundle (coui://ui-mods/images/, see webpack asset rule).
//
// Tooltip is the vanilla cs2/ui one wrapped AROUND the button —
// FloatingButton's own tooltipLabel prop is ignored by this variant, so
// wrapping is the pattern every mod uses for native tooltips here.
export const ToolbarToggleButton = () => {
  const state = useToolState();
  const t = useT();
  return (
    <Tooltip tooltip={t("toolbar.toggle.tooltip")}>
      <FloatingButton
        src={iconSrc}
        selected={state.isActive}
        onSelect={() => cmdActivateTool()}
      />
    </Tooltip>
  );
};
