// Entry — registers React components into CS2's UI module registry. The game
// imports this .mjs as an ES module, calls the default-exported `register`
// function, and mounts our components into the requested slots.
//
// Replaces the earlier ExecuteScript-based body-mount hack (carried over from
// the SystemTimeMod template) that triggered a NullReferenceException in
// UIModuleAsset.PostCreate — the official pipeline doesn't have that issue.
import { ModRegistrar } from "cs2/modding";
import "./index.scss";
import { TownRoadLanePanel } from "mods/town-road-lane-panel";
import { ToolbarToggleButton } from "mods/toolbar-toggle-button";

const register: ModRegistrar = (moduleRegistry) => {
  // Toolbar button — sits in the top-left cluster alongside vanilla tool toggles.
  moduleRegistry.append("GameTopLeft", ToolbarToggleButton);
  // Tool panel — anchored top-right; renders only when our tool is active +
  // has a selected node (gated inside the component itself).
  moduleRegistry.append("GameTopRight", TownRoadLanePanel);
  console.log("TownRoadLane UI: registered (GameTopLeft button + GameTopRight panel)");
};

export default register;
