import { bindValue, useValue, trigger } from "cs2/api";

// Pinned style ids, published by the C# UI system and stored in the mod settings. Pinned
// options go to the top of every style dropdown, in pin order.

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
