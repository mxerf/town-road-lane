import { FloatingButton, Tooltip } from "cs2/ui";
import { useToolState, cmdActivateTool } from "../hooks/useToolState";
import { useT } from "../i18n";
import iconSrc from "../assets/marking-tool.svg";

// Toolbar toggle built on the vanilla FloatingButton, the component the game uses for tool
// toggles. The icon ships as an SVG next to the bundle (coui://ui-mods/images/).
//
// The tooltip wraps the button: FloatingButton ignores its own tooltipLabel prop here.
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
