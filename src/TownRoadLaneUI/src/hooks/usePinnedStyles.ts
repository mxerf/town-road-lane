import { useMemo } from "react";
import { bindValue, useValue, trigger } from "cs2/api";
import { BINDING_GROUP } from "../bindingGroup";

// Pinned style ids, published by the C# UI system and stored in the mod settings. Pinned
// options go to the top of every style dropdown, in pin order.

export interface PinnedStylesVM {
  lineStyles: number[];
  areaStyles: number[];
}

const EMPTY: PinnedStylesVM = { lineStyles: [], areaStyles: [] };

const PINNED_BINDING = bindValue<PinnedStylesVM>(BINDING_GROUP, "GetPinnedStyles", EMPTY);

const normalizePinned = (v: PinnedStylesVM | null | undefined): PinnedStylesVM =>
  v
    ? {
        lineStyles: Array.isArray(v.lineStyles) ? v.lineStyles : [],
        areaStyles: Array.isArray(v.areaStyles) ? v.areaStyles : [],
      }
    : EMPTY;

// Memoized on the binding value, which only changes on a C# push, as in useToolState.
export const usePinnedStyles = (): PinnedStylesVM => {
  const v = useValue(PINNED_BINDING);
  return useMemo(() => normalizePinned(v), [v]);
};

export const cmdTogglePinLineStyle = (style: number) => {
  trigger(BINDING_GROUP, "TogglePinLineStyle", style);
};

export const cmdTogglePinAreaStyle = (styleId: number) => {
  trigger(BINDING_GROUP, "TogglePinAreaStyle", styleId);
};
