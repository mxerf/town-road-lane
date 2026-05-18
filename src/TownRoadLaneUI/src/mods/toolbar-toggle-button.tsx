import { useToolState, cmdActivateTool } from "../hooks/useToolState";

// Small round button in the top-left game cluster. Click toggles our marking
// tool active/inactive — same effect as Ctrl+M or the settings button, but
// always visible on screen.
export const ToolbarToggleButton = () => {
  const state = useToolState();
  return (
    <button
      className={`trl-toolbar-btn${state.isActive ? " trl-toolbar-btn--active" : ""}`}
      onClick={() => cmdActivateTool()}
      title="Town Road Lane marking tool (Ctrl+M)"
    >
      TRL
    </button>
  );
};
