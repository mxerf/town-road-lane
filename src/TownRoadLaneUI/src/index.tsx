// Entry point — mounts the panel into game DOM via ExecuteScript, matching the
// SystemTimeMod pattern. Panel itself renders only when the mod tool is active +
// has a selected node (see useToolState hook).
import { createRoot } from "react-dom/client";
import { ModRegistrar } from "cs2/modding";
import "./index.scss";
import { TownRoadLanePanel } from "mods/town-road-lane-panel";

console.log("TownRoadLane UI: initialising");

const mount = () => {
  const container = document.createElement("div");
  container.id = "town-road-lane-ui-root";
  // Full-screen overlay container; the panel inside positions itself absolutely.
  // pointerEvents none on the container, then on inside the panel re-enables
  // pointer events so clicks inside the panel work but the rest of the game UI
  // (toolbars, map) keeps receiving input.
  container.style.position = "fixed";
  container.style.top = "0";
  container.style.left = "0";
  container.style.width = "100%";
  container.style.height = "100%";
  container.style.pointerEvents = "none";
  container.style.zIndex = "999999";
  document.body.appendChild(container);
  createRoot(container).render(<TownRoadLanePanel />);
  console.log("TownRoadLane UI: mounted");
};

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", mount);
} else {
  mount();
}

// Unused but required for ModRegistrar type validation.
const register: ModRegistrar = () => {};
export default register;
