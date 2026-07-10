// Typed i18n dictionary. The shape of en-US is the canonical key set — TS
// will surface any missing keys in other locales as a compile error (via the
// LocaleDict constraint below), so adding a string in EN and forgetting RU
// fails the build instead of silently falling through to a default at runtime.
//
// Interpolation: write placeholders as {name} in the string and pass them
// through the params object to t(): t("panel.title", { n: 5 }).
//
// To add a new language: clone the en-US block, translate values, and add it
// to the STRINGS object. TS will require every key to be present.

const enUS = {
  // Toolbar
  "toolbar.toggle.label":     "Town Road Lane",
  "toolbar.toggle.tooltip":   "Toggle TownRoadLane marking tool (Ctrl+M)",

  // Panel chrome
  "panel.title":              "Node #{n}",
  "panel.meta.lines":         "{n} line(s)",
  "panel.meta.defaultStyle":  "default style",
  "panel.hint.empty":         "Click two endpoint dots to draw a line.",
  "panel.error.title":        "Panel error",
  "panel.error.retry":        "Retry",

  // Vanilla markings toggle
  "vanilla.hide":             "Hide vanilla markings",
  "vanilla.show":             "Show vanilla markings",
  "vanilla.tooltip":          "Suppress the game's own markings on this intersection. Nodes with drawn lines hide them automatically; this switch works without any lines.",

  // Line row
  "line.title":               "Line #{n}",
  "line.segCount":            "{visible}/{total}",
  "line.delete":              "Delete line",
  "line.delete.confirm":      "Delete line #{n}? This cannot be undone.",
  "line.delete.cancel":       "Cancel",
  "line.delete.confirm.btn":  "Delete",
  "line.curvature":           "Curvature",
  "line.curvature.tooltip":   "Bend of the line: 0% = straight, 50% = default arc, 100% = maximum roundness",

  // Segment row
  "segment.label":            "seg {n}",
  "segment.length":           "{m}m",
  "segment.hide.tooltip":     "Hide segment",
  "segment.show.tooltip":     "Show segment",
  "segment.cycleStyle.tooltip": "Cycle style (current: {style})",

  // Styles
  "style.solid":              "Solid",
  "style.dashed":             "Dashed",
  "style.g87Solid":           "G87 Solid",
  "style.g87Dashed":          "G87 Dashed",
  "style.doubleSolid":        "Double Solid",
  "style.unknown":            "?",
} as const;

type LocaleDict = { readonly [K in keyof typeof enUS]: string };

const ruRU: LocaleDict = {
  // Toolbar
  "toolbar.toggle.label":     "Дорожная разметка",
  "toolbar.toggle.tooltip":   "Включить инструмент разметки TownRoadLane (Ctrl+M)",

  // Panel chrome
  "panel.title":              "Узел #{n}",
  "panel.meta.lines":         "линий: {n}",
  "panel.meta.defaultStyle":  "стиль по умолчанию",
  "panel.hint.empty":         "Кликни две точки на краях узла, чтобы провести линию.",
  "panel.error.title":        "Ошибка панели",
  "panel.error.retry":        "Повторить",

  // Vanilla markings toggle
  "vanilla.hide":             "Скрыть ванильную разметку",
  "vanilla.show":             "Показать ванильную разметку",
  "vanilla.tooltip":          "Убирает штатную разметку игры на этом перекрёстке. Узлы с нарисованными линиями скрывают её автоматически; этот переключатель работает и без линий.",

  // Line row
  "line.title":               "Линия #{n}",
  "line.segCount":            "{visible}/{total}",
  "line.delete":              "Удалить линию",
  "line.delete.confirm":      "Удалить линию #{n}? Действие нельзя отменить.",
  "line.delete.cancel":       "Отмена",
  "line.delete.confirm.btn":  "Удалить",
  "line.curvature":           "Кривизна",
  "line.curvature.tooltip":   "Изгиб линии: 0% — прямая, 50% — стандартная дуга, 100% — максимальное скругление",

  // Segment row
  "segment.label":            "сегм. {n}",
  "segment.length":           "{m} м",
  "segment.hide.tooltip":     "Скрыть сегмент",
  "segment.show.tooltip":     "Показать сегмент",
  "segment.cycleStyle.tooltip": "Сменить стиль (сейчас: {style})",

  // Styles
  "style.solid":              "Сплошная",
  "style.dashed":             "Пунктир",
  "style.g87Solid":           "G87 Сплошная",
  "style.g87Dashed":          "G87 Пунктир",
  "style.doubleSolid":        "Двойная сплошная",
  "style.unknown":            "?",
};

export const STRINGS = {
  "en-US": enUS,
  "ru-RU": ruRU,
} as const;

export type Locale = keyof typeof STRINGS;
export type StringKey = keyof typeof enUS;

export const DEFAULT_LOCALE: Locale = "en-US";

// Map an arbitrary CS2 locale code to one we ship. CS2 uses BCP-47-ish codes
// (en-US, ru-RU, de-DE, ja-JP, etc); we collapse unsupported regions to the
// base language and ultimately fall back to en-US.
export const resolveLocale = (raw: string | null | undefined): Locale => {
  if (!raw) return DEFAULT_LOCALE;
  if (raw in STRINGS) return raw as Locale;
  const base = raw.split("-")[0];
  for (const code of Object.keys(STRINGS) as Locale[]) {
    if (code.startsWith(base + "-")) return code;
  }
  return DEFAULT_LOCALE;
};
