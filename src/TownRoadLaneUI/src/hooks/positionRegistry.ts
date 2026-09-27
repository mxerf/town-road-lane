import { bindValue } from "cs2/api";
import { BINDING_GROUP } from "../bindingGroup";

// World-to-screen popover positioning outside React (same approach as TrafficToolEssentials'
// positionRegistry). C# publishes `GetScreenPoints` on every tick the camera moves, which is
// every frame during a pan; routing that through React state would re-render the whole panel
// each frame. Popovers register their root DOM node here by anchor key instead, and one
// module-level subscription writes style.left/top/transform directly. React re-renders only
// when GetPanelState changes.

export interface SegmentPointVM {
  lineIndex: number;
  segmentIndex: number;
  // >= 0 when the point is an area centroid (lineIndex/segmentIndex are -1 then). Segments
  // and areas share one binding so a camera push stays a single serialized array.
  areaIndex: number;
  x: number; // CSS px, origin top-left (Y already flipped on the C# side)
  y: number;
  // Popover scale by camera distance, about [0.65, 1]: full size near the intersection,
  // smaller as the camera pulls away so a zoomed-out view isn't covered in popovers.
  scale: number;
}

export const segKey = (lineIndex: number, segmentIndex: number): string =>
  `${lineIndex}:${segmentIndex}`;

export const areaKey = (areaIndex: number): string => `area:${areaIndex}`;

const pointKey = (p: SegmentPointVM): string =>
  p.areaIndex >= 0 ? areaKey(p.areaIndex) : segKey(p.lineIndex, p.segmentIndex);

const POINTS_BINDING = bindValue<SegmentPointVM[]>(BINDING_GROUP, "GetScreenPoints", []);

const anchors = new Map<string, HTMLElement>();

// Anchors whose popover is hover-expanded. Expanded popovers scale back up to at least 1:
// a small distant marker is fine, small buttons the user is about to click are not.
const expandedKeys = new Set<string>();

// Latest points, so an anchor that registers after the last push (a line expanded while
// the camera is still, with no new push coming) is positioned right away.
let lastPoints = new Map<string, SegmentPointVM>();

const position = (key: string, el: HTMLElement, point: SegmentPointVM | undefined): void => {
  if (!point) {
    // No point means the anchor is off-screen or behind the camera. Setting display back
    // to "" later restores the stylesheet value.
    el.style.display = "none";
    return;
  }
  const base = point.scale > 0 ? point.scale : 1;
  const scale = expandedKeys.has(key) ? Math.max(1, base) : base;
  el.style.display = "";
  el.style.left = `${point.x}px`;
  el.style.top = `${point.y}px`;
  // The inline transform replaces the stylesheet's, so it has to repeat the anchoring
  // translate.
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

/** Ref callback target: registers a popover root under its anchor key, or unregisters it
 * when `el` is null. Applies the cached position immediately so a new popover doesn't flash
 * at 0,0. */
export const registerSegmentAnchor = (key: string, el: HTMLElement | null): void => {
  if (el) {
    anchors.set(key, el);
    position(key, el, lastPoints.get(key));
  } else {
    anchors.delete(key);
    expandedKeys.delete(key);
  }
};

/** Marks an anchor's popover as hover-expanded or collapsed and reapplies its position at
 * once: a still camera pushes nothing, so the scale change can't wait for the next sync. */
export const setAnchorExpanded = (key: string, expanded: boolean): void => {
  if (expanded) expandedKeys.add(key);
  else expandedKeys.delete(key);
  const el = anchors.get(key);
  if (el) position(key, el, lastPoints.get(key));
};
