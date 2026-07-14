import { bindValue } from "cs2/api";

// World→screen popover positioning, decoupled from React (pattern from
// TrafficToolEssentials' positionRegistry). C# publishes `GetScreenPoints`
// every tick the camera actually moved — that's every frame during a pan, so
// routing it through React state would re-render the whole panel per frame.
// Instead, popover components register their root DOM node here by anchor
// key, and a single module-level subscription writes style.left/top/transform
// directly. React only re-renders when the panel STRUCTURE changes
// (GetPanelState).

export interface SegmentPointVM {
  lineIndex: number;
  segmentIndex: number;
  // >= 0 → this point is an AREA centroid anchor (lineIndex/segmentIndex are
  // -1 then). Segments and areas share one binding so a camera push stays a
  // single serialized array.
  areaIndex: number;
  x: number; // CSS px, origin top-left (Y already flipped on the C# side)
  y: number;
  // Camera-distance popover scale, ~[0.65, 1]: full size while the camera is
  // near the intersection, gently shrinking as it pulls away so a zoomed-out
  // view isn't wallpapered with full-size chrome.
  scale: number;
}

export const segKey = (lineIndex: number, segmentIndex: number): string =>
  `${lineIndex}:${segmentIndex}`;

export const areaKey = (areaIndex: number): string => `area:${areaIndex}`;

const pointKey = (p: SegmentPointVM): string =>
  p.areaIndex >= 0 ? areaKey(p.areaIndex) : segKey(p.lineIndex, p.segmentIndex);

const POINTS_BINDING = bindValue<SegmentPointVM[]>("TownRoadLane", "GetScreenPoints", []);

const anchors = new Map<string, HTMLElement>();

// Anchors whose popover is hover-expanded right now. Expanded popovers snap
// their scale back up to ≥1 — a shrunken far-away marker is fine, shrunken
// BUTTONS the user is about to click are not.
const expandedKeys = new Set<string>();

// Latest points snapshot, kept so an anchor registering AFTER the last push
// (e.g. the user expands a line while the camera is static — no new push
// coming) is positioned immediately from cached data.
let lastPoints = new Map<string, SegmentPointVM>();

const position = (key: string, el: HTMLElement, point: SegmentPointVM | undefined): void => {
  if (!point) {
    // No point this sync = anchor off-screen / behind camera → hide. Restoring
    // display to "" falls back to the stylesheet value when the point returns.
    el.style.display = "none";
    return;
  }
  const base = point.scale > 0 ? point.scale : 1;
  const scale = expandedKeys.has(key) ? Math.max(1, base) : base;
  el.style.display = "";
  el.style.left = `${point.x}px`;
  el.style.top = `${point.y}px`;
  // Inline transform overrides the stylesheet's — must restate the anchoring
  // translate. Scale composes after it, growing/shrinking around the box.
  el.style.transform = `translate(-50%, -120%) scale(${scale})`;
};

POINTS_BINDING.subscribe((points) => {
  lastPoints = new Map<string, SegmentPointVM>();
  for (const p of points ?? []) {
    lastPoints.set(pointKey(p), p);
  }
  for (const [key, el] of anchors) {
    position(key, el, lastPoints.get(key));
  }
});

/** Ref callback target: register a popover root under its anchor key. Pass
 * null (React unmount) to unregister. Applies the cached position synchronously
 * so freshly mounted popovers don't flash at 0,0. */
export const registerSegmentAnchor = (key: string, el: HTMLElement | null): void => {
  if (el) {
    anchors.set(key, el);
    position(key, el, lastPoints.get(key));
  } else {
    anchors.delete(key);
    expandedKeys.delete(key);
  }
};

/** Mark an anchor's popover as hover-expanded (or collapsed again) and
 * reapply its cached position immediately — a static camera pushes nothing,
 * so the scale snap must not wait for the next binding sync. */
export const setAnchorExpanded = (key: string, expanded: boolean): void => {
  if (expanded) expandedKeys.add(key);
  else expandedKeys.delete(key);
  const el = anchors.get(key);
  if (el) position(key, el, lastPoints.get(key));
};
