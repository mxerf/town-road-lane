import { bindValue, useValue, trigger } from "cs2/api";

// Pinned "favourite" style ids for the style dropdowns (panel + popovers).
// Mirrors PinnedStylesVM published by C# TownRoadLaneUISystem; persisted in the
// mod settings as CSV so pins survive game restarts. Pinned options float to
// the top of every style dropdown, in the order they were pinned.

export interface PinnedStylesVM {
  lineStyles: number[];
  areaStyles: number[];
}

const EMPTY: PinnedStylesVM = { lineStyles: [], areaStyles: [] };

const PINNED_BINDING = bindValue<PinnedStylesVM>("TownRoadLane", "GetPinnedStyles", EMPTY);

export const usePinnedStyles = (): PinnedStylesVM => {
  const v = useValue(PINNED_BINDING);
  if (!v) return EMPTY;
  return {
    lineStyles: Array.isArray(v.lineStyles) ? v.lineStyles : [],
    areaStyles: Array.isArray(v.areaStyles) ? v.areaStyles : [],
  };
};

export const cmdTogglePinLineStyle = (style: number) => {
  trigger("TownRoadLane", "TogglePinLineStyle", style);
};

export const cmdTogglePinAreaStyle = (styleId: number) => {
  trigger("TownRoadLane", "TogglePinAreaStyle", styleId);
};
