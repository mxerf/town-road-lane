// Translation hook plus a plain function for callers outside the React tree.
// The locale comes from a binding published by C#: useLocalization() from cs2/l10n only
// translates game string ids and does not expose the current locale code, which our own
// dictionary needs. Same approach as TrafficToolEssentials' "C2VM.TLE/GetLocale" binding.

import { bindValue, useValue } from "cs2/api";
import { STRINGS, StringKey, Locale, DEFAULT_LOCALE, resolveLocale } from "./strings";

const LOCALE_BINDING = bindValue<string>("TownRoadLane", "GetLocale", DEFAULT_LOCALE);

const interpolate = (template: string, params?: Record<string, string | number>): string => {
  if (!params) return template;
  return template.replace(/\{(\w+)\}/g, (_, k) => {
    const v = params[k];
    return v === undefined || v === null ? `{${k}}` : String(v);
  });
};

// Falls back to en-US for a key missing in the requested locale, then to the key itself,
// so a dynamic key that bypassed type checking still shows up visibly.
export const translate = (
  locale: Locale,
  key: StringKey,
  params?: Record<string, string | number>,
): string => {
  const dict = STRINGS[locale] ?? STRINGS[DEFAULT_LOCALE];
  const raw  = dict[key] ?? STRINGS[DEFAULT_LOCALE][key] ?? key;
  return interpolate(raw, params);
};

export const useT = (): ((key: StringKey, params?: Record<string, string | number>) => string) => {
  const rawLocale = useValue(LOCALE_BINDING);
  const resolved = resolveLocale(rawLocale);
  return (key, params) => translate(resolved, key, params);
};
