import { useToolState, cmdActivateTool } from "../hooks/useToolState";
import { Road } from "../components/icons";
import { useT } from "../i18n";
import { ToolbarBtn } from "./panel.styles";

// Small round button in the top-left game cluster. Click toggles our marking
// tool active/inactive — same effect as Ctrl+M or the settings button, but
// always visible on screen. Icon-only for compactness; full label sits in
// the native tooltip.
export const ToolbarToggleButton = () => {
  const state = useToolState();
  const t = useT();
  return (
    <ToolbarBtn
      $active={state.isActive}
      onClick={() => cmdActivateTool()}
      title={t("toolbar.toggle.tooltip")}
      aria-label={t("toolbar.toggle.label")}
    >
      <Road size={18} />
    </ToolbarBtn>
  );
};
