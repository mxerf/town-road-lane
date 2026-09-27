// Entry point: the game imports this .mjs, calls the default-exported `register` and mounts
// the components into the requested slots.
import { ModRegistrar } from "cs2/modding";
import "./index.scss";
import { TownRoadLanePanel } from "mods/town-road-lane-panel";
import { ToolbarToggleButton } from "mods/toolbar-toggle-button";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ToolbarToggleButton);
  // The panel decides itself when to render (tool active and a node selected).
  moduleRegistry.append("GameTopRight", TownRoadLanePanel);
  console.log("TownRoadLane UI: registered (GameTopLeft button + GameTopRight panel)");
};

export default register;
