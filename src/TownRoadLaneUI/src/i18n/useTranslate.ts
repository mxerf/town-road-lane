// Translation hook + standalone helper. Two surfaces because some callers
// can't use hooks (outside React tree, in modules that wrap calls before
// mount). The hook resolves locale via a C#-published binding so React stays
// in sync with the game's active language without polling.
//
// Why not useLocalization() from cs2/l10n: that hook returns a Localization
// object whose translate() looks up game string IDs — it does not expose the
// current locale code, so it doesn't help us pick between en-US/ru-RU in
// our own dictionary. We publish the locale from C# instead (mirrors TTE's
// "C2VM.TLE/GetLocale" binding pattern).

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

// Pure translator — given a locale, key, and params, return the localized
// string. Falls back to en-US if the key is missing in the requested locale,
// and to the key itself if missing in en-US too (catches typos at runtime
// when TS type-checking is bypassed, e.g. dynamic keys).
export const translate = (
  locale: Locale,
  key: StringKey,
  params?: Record<string, string | number>,
): string => {
  const dict = STRINGS[locale] ?? STRINGS[DEFAULT_LOCALE];
  const raw  = dict[key] ?? STRINGS[DEFAULT_LOCALE][key] ?? key;
  return interpolate(raw, params);
};

// React hook — bind translation to the current game locale. Returns a `t`
// function so call sites stay terse: const t = useT(); t("panel.title", { n }).
export const useT = (): ((key: StringKey, params?: Record<string, string | number>) => string) => {
  const rawLocale = useValue(LOCALE_BINDING);
  const resolved = resolveLocale(rawLocale);
  return (key, params) => translate(resolved, key, params);
};
