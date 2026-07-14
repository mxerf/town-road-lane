import { bindValue } from "cs2/api";

// World→screen popover positioning, decoupled from React (pattern from
// TrafficToolEssentials' positionRegistry). C# publishes `GetScreenPoints`
// every tick the camera actually moved — that's every frame during a pan, so
// routing it through React state would re-render the whole panel per frame.
// Instead, popover components register their root DOM node here by segment
// key, and a single module-level subscription writes style.left/top directly.
// React only re-renders when the panel STRUCTURE changes (GetPanelState).

export interface SegmentPointVM {
  lineIndex: number;
  segmentIndex: number;
  x: number; // CSS px, origin top-left (Y already flipped on the C# side)
  y: number;
}

export const segKey = (lineIndex: number, segmentIndex: number): string =>
  `${lineIndex}:${segmentIndex}`;

const POINTS_BINDING = bindValue<SegmentPointVM[]>("TownRoadLane", "GetScreenPoints", []);

const anchors = new Map<string, HTMLElement>();

// Latest points snapshot, kept so an anchor registering AFTER the last push
// (e.g. the user expands a line while the camera is static — no new push
// coming) is positioned immediately from cached data.
let lastPoints = new Map<string, SegmentPointVM>();

const position = (el: HTMLElement, point: SegmentPointVM | undefined): void => {
  if (!point) {
    // No point this sync = segment off-screen / behind camera → hide. Restoring
    // display to "" falls back to the stylesheet value when the point returns.
    el.style.display = "none";
    return;
  }
  el.style.display = "";
  el.style.left = `${point.x}px`;
  el.style.top = `${point.y}px`;
};

POINTS_BINDING.subscribe((points) => {
  lastPoints = new Map<string, SegmentPointVM>();
  for (const p of points ?? []) {
    lastPoints.set(segKey(p.lineIndex, p.segmentIndex), p);
  }
  for (const [key, el] of anchors) {
    position(el, lastPoints.get(key));
  }
});

/** Ref callback target: register a popover root under its segment key. Pass
 * null (React unmount) to unregister. Applies the cached position synchronously
 * so freshly mounted popovers don't flash at 0,0. */
export const registerSegmentAnchor = (key: string, el: HTMLElement | null): void => {
  if (el) {
    anchors.set(key, el);
    position(el, lastPoints.get(key));
  } else {
    anchors.delete(key);
  }
};
