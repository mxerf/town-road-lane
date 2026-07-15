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
  "panel.appTitle":           "Road Markings",
  "panel.title":              "Node #{n}",
  "panel.close.tooltip":      "Close the marking tool",
  "panel.hint.selectNode":    "Click an intersection on the map to edit its markings.",
  "panel.error.title":        "Panel error",
  "panel.error.retry":        "Retry",

  // Live tool status (mirrors MarkingNodeToolSystem.State)
  "status.line.first":        "Click the first point of a line",
  "status.line.second":       "Click the second point to finish the line",
  "status.area":              "Area mode — points placed: {n}",

  // Section headers
  "section.drawing":          "Drawing",
  "section.node":             "Intersection",

  // Node block
  "node.vanilla.label":       "Vanilla markings",

  // Mode switch
  "mode.lines":               "Lines",
  "mode.area":                "Area",
  "mode.lines.tooltip":       "Draw marking lines between endpoint dots",
  "mode.area.tooltip":        "Fill a polygon between anchor dots (safety islands, hatched zones)",

  // Area draft (AreaSelecting)
  "area.draft.hint.add":      "LMB — add a point",
  "area.draft.hint.undo":     "RMB — remove the last point",
  "area.draft.hint.close":    "Click the first point again to close (needs 3+)",
  "area.draft.cancel":        "Cancel area",

  // Next-object style pickers
  "next.lineStyle":           "New line style",
  "next.lineStyle.tooltip":   "Style applied to the NEXT line you draw (hotkey Y cycles it)",
  "next.areaStyle":           "New area fill",
  "next.areaStyle.tooltip":   "Fill applied to the NEXT area you close (hotkey U cycles it). Fills marked (G87) need the G87 Road Markings packs; Asphalt needs the separate 'G87 Vanilla Asphalt Pavement' mod. Missing fills fall back to concrete.",

  // Sections
  "section.lines":            "Lines",
  "section.areas":            "Areas",

  // Area rows
  "area.title":               "Area #{n}",
  "area.pieces":              "{visible}/{total}",
  "area.meta.vertices":       "{n} pts",
  "area.hide.tooltip":        "Hide area",
  "area.show.tooltip":        "Show area",
  "area.delete":              "Delete area",
  "area.style":               "Fill style",

  // Area fill styles (indexes match kStyleSurfaceNames on the C# side)
  "areaStyle.0":              "Concrete",
  "areaStyle.1":              "Junction box (G87)",
  "areaStyle.2":              "White stripes (G87)",
  "areaStyle.3":              "White stripes, sparse (G87)",
  "areaStyle.4":              "Yellow stripes (G87)",
  "areaStyle.5":              "Green bike lane (G87)",
  "areaStyle.6":              "Red bus lane (G87)",
  "areaStyle.14":             "Asphalt (G87 VA)",

  // Full node reset
  "node.reset":               "Reset all markings",
  "node.reset.tooltip":       "Delete every line and area on this node and bring back the game's own markings.",

  // Hotkey hints footer
  "hotkeys.title":            "Hotkeys",
  "hotkeys.toggle":           "toggle tool",
  "hotkeys.cycleLine":        "next line style",
  "hotkeys.cycleArea":        "next area fill",
  "hotkeys.areaMode":         "area mode",
  "hotkeys.rmb":              "RMB",
  "hotkeys.rmb.desc":         "undo point / cancel",
  "hotkeys.esc":              "Esc",
  "hotkeys.esc.desc":         "step back",

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
  "line.curvature.reset":     "Reset to the default 50%",

  // Segment row
  "segment.label":            "Segment {n}",
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
  "style.dashedDense":        "Dashed, short",
  "style.dashedLong":         "Dashed, long",
  "style.g87Yellow":          "G87 Yellow",
  "style.g87YellowDashed":    "G87 Yellow Dashed",
  "style.unknown":            "?",
} as const;

type LocaleDict = { readonly [K in keyof typeof enUS]: string };

const ruRU: LocaleDict = {
  // Toolbar
  "toolbar.toggle.label":     "Дорожная разметка",
  "toolbar.toggle.tooltip":   "Включить инструмент разметки TownRoadLane (Ctrl+M)",

  // Panel chrome
  "panel.appTitle":           "Дорожная разметка",
  "panel.title":              "Узел #{n}",
  "panel.close.tooltip":      "Закрыть инструмент разметки",
  "panel.hint.selectNode":    "Кликните по перекрёстку на карте, чтобы редактировать его разметку.",
  "panel.error.title":        "Ошибка панели",
  "panel.error.retry":        "Повторить",

  // Live tool status (mirrors MarkingNodeToolSystem.State)
  "status.line.first":        "Кликните первую точку линии",
  "status.line.second":       "Кликните вторую точку — линия завершится",
  "status.area":              "Режим области — точек: {n}",

  // Section headers
  "section.drawing":          "Рисование",
  "section.node":             "Перекрёсток",

  // Node block
  "node.vanilla.label":       "Ванильная разметка",

  // Mode switch
  "mode.lines":               "Линии",
  "mode.area":                "Область",
  "mode.lines.tooltip":       "Рисование линий разметки между точками",
  "mode.area.tooltip":        "Заливка полигона по опорным точкам (островки безопасности, штриховка)",

  // Area draft (AreaSelecting)
  "area.draft.hint.add":      "ЛКМ — добавить точку",
  "area.draft.hint.undo":     "ПКМ — убрать последнюю точку",
  "area.draft.hint.close":    "Клик по первой точке — замкнуть (нужно 3+)",
  "area.draft.cancel":        "Отменить область",

  // Next-object style pickers
  "next.lineStyle":           "Стиль новой линии",
  "next.lineStyle.tooltip":   "Стиль СЛЕДУЮЩЕЙ линии, которую вы проведёте (хоткей Y листает по кругу)",
  "next.areaStyle":           "Заливка новой области",
  "next.areaStyle.tooltip":   "Заливка СЛЕДУЮЩЕЙ замкнутой области (хоткей U листает по кругу). Стили с пометкой (G87) требуют паки G87 Road Markings; асфальт — отдельный мод «G87 Vanilla Asphalt Pavement». Недоступные стили откатываются к бетону.",

  // Sections
  "section.lines":            "Линии",
  "section.areas":            "Области",

  // Area rows
  "area.title":               "Область #{n}",
  "area.pieces":              "{visible}/{total}",
  "area.meta.vertices":       "{n} тчк",
  "area.hide.tooltip":        "Скрыть область",
  "area.show.tooltip":        "Показать область",
  "area.delete":              "Удалить область",
  "area.style":               "Стиль заливки",

  // Area fill styles (indexes match kStyleSurfaceNames on the C# side)
  "areaStyle.0":              "Бетон",
  "areaStyle.1":              "Вафельная разметка (G87)",
  "areaStyle.2":              "Белая штриховка (G87)",
  "areaStyle.3":              "Белая штриховка, редкая (G87)",
  "areaStyle.4":              "Жёлтая штриховка (G87)",
  "areaStyle.5":              "Зелёная велополоса (G87)",
  "areaStyle.6":              "Красная автобусная полоса (G87)",
  "areaStyle.14":             "Асфальт (G87 VA)",

  // Full node reset
  "node.reset":               "Сбросить всю разметку",
  "node.reset.tooltip":       "Удаляет все линии и области на этом узле и возвращает штатную разметку игры.",

  // Hotkey hints footer
  "hotkeys.title":            "Горячие клавиши",
  "hotkeys.toggle":           "вкл/выкл инструмент",
  "hotkeys.cycleLine":        "стиль новой линии",
  "hotkeys.cycleArea":        "заливка новой области",
  "hotkeys.areaMode":         "режим области",
  "hotkeys.rmb":              "ПКМ",
  "hotkeys.rmb.desc":         "убрать точку / отмена",
  "hotkeys.esc":              "Esc",
  "hotkeys.esc.desc":         "шаг назад",

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
  "line.curvature.reset":     "Вернуть стандартные 50%",

  // Segment row
  "segment.label":            "Сегмент {n}",
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
  "style.dashedDense":        "Пунктир, короткий",
  "style.dashedLong":         "Пунктир, длинный",
  "style.g87Yellow":          "G87 Жёлтая",
  "style.g87YellowDashed":    "G87 Жёлтый пунктир",
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
