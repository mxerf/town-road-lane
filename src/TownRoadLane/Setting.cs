using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;

namespace TownRoadLane
{
    /// <summary>
    /// v2 phase 1 settings. Per-feature on/off + per-feature mesh style (vanilla or G87).
    /// Each feature's style is read by the corresponding clone system in PrefabUpdate phase
    /// and resolved against the loaded mesh prefab set; "G87 …" options gracefully fall back
    /// to vanilla if the G87 Road Markings mod isn't installed (see K7 in IMPLEMENTATION_PLAN.md).
    ///
    /// All changes take effect on the next save load (see the note above the keybind group on
    /// why there is no runtime reapply).
    /// </summary>
    [FileLocation(nameof(TownRoadLane))]
    [SettingsUIGroupOrder(kEdgeGroup, kParkingGroup, kSegmentGroup, kSegmentDevGroup, kKeybindGroup)]
    [SettingsUIShowGroupName(kEdgeGroup, kParkingGroup, kSegmentGroup, kSegmentDevGroup, kKeybindGroup)]
    [SettingsUIKeyboardAction(ToggleMarkingTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(CycleMarkingStyle, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(EnterAreaMode, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(CycleAreaStyle, Usages.kToolUsage)]
    // The class name MUST be globally unique among installed mods: Setting.ApplyAndSave() saves via
    // AssetDatabase.SaveSpecificSetting(GetType().Name), which matches settings by bare type name
    // across ALL mods and takes the first hit. With the template name "Setting" and another
    // template-derived mod installed, every checkbox change saved the OTHER mod's file and ours
    // never persisted (forum report: parking toggle back on after every game restart).
    public class TownRoadLaneSetting : ModSetting
    {
        public const string kSection = "Main";
        public const string kEdgeGroup = "EdgeLine";
        public const string kParkingGroup = "ParkingMarkings";
        public const string kSegmentGroup = "SegmentSplit";
        public const string kSegmentDevGroup = "SegmentSplitDev";
        public const string kKeybindGroup = "Keybinds";

        // Action name used by MarkingToolHotkeySystem to resolve the ProxyAction. Must match the
        // attribute name on this class and the binding property below.
        public const string ToggleMarkingTool = "ToggleMarkingTool";

        // Stage 5c: cycle MarkingStyle (Solid → Dashed → Solid → ...) inside the marking tool.
        // Single key (default Y), kept under Usages.kToolUsage so the binding only listens while
        // the tool is the active tool — won't interfere with vanilla shortcuts otherwise.
        public const string CycleMarkingStyle = "CycleMarkingStyle";

        // Phase 6b: enter polygon-area selection mode from NodeSelected. Default A. Tool-scoped
        // so the key doesn't conflict outside the marking tool.
        public const string EnterAreaMode = "EnterAreaMode";

        // Phase 6d: cycle the style of the NEXT area the user closes. Mirrors CycleMarkingStyle
        // for line drawing. Default U (close to Y but different finger so the two cycles don't
        // get tangled during AreaSelecting).
        public const string CycleAreaStyle = "CycleAreaStyle";

        public TownRoadLaneSetting(IMod mod) : base(mod) { }

        // --- Edge line (curb-side line on city 3 m roads) ---

        [SettingsUISection(kSection, kEdgeGroup)]
        public bool EdgeLineEnabled { get; set; } = true;

        [SettingsUISection(kSection, kEdgeGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsEdgeDisabled))]
        public EdgeLineStyleEnum EdgeLineStyle { get; set; } = EdgeLineStyleEnum.WhiteSolid;

        // --- Parallel street-parking markings ---

        [SettingsUISection(kSection, kParkingGroup)]
        public bool ParkingMarkingsEnabled { get; set; } = true;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsParkingDisabled))]
        // Default is the G87 dashed decal (best-looking option; G87 ships as a hard dependency
        // anyway). PickMesh in ParkingLineCloneSystem falls back to the vanilla dense dashed
        // mesh when G87 isn't loaded, so the default is safe without it.
        public ParkingLineStyleEnum ParkingLineStyle { get; set; } = ParkingLineStyleEnum.WhiteDashed_G87;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsParkingDisabled))]
        public ParkingEndStyleEnum ParkingEndStyle { get; set; } = ParkingEndStyleEnum.WhiteSolid;

        // ── Segment splitting thresholds (marking editor) ──
        // Feed MarkingTopologySystem's split filters. Unlike the style prefabs above, this is
        // pure runtime data — no game restart needed: a junction re-segments the next time its
        // lines change, and every junction re-segments on save load (topology hash isn't saved).
        [SettingsUISlider(min = 0.2f, max = 3f, step = 0.1f, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kSegmentGroup)]
        public float SegmentMinLengthM { get; set; } = MarkingTopologySystem.kDefaultMinSegmentLengthM;

        [SettingsUISlider(min = 0f, max = 4f, step = 0.1f, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kSegmentGroup)]
        public float SegmentAnchorDeadZoneM { get; set; } = MarkingTopologySystem.kDefaultEndpointMarginM;

        [SettingsUISlider(min = 0, max = 20, step = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kSegmentDevGroup)]
        public int SegmentMinCrossingAngleDeg { get; set; } = MarkingTopologySystem.kDefaultMinCrossingAngleDeg;

        [SettingsUISlider(min = 0.25f, max = 3f, step = 0.25f, unit = Unit.kFloatSingleFraction)]
        [SettingsUISection(kSection, kSegmentDevGroup)]
        public float SegmentHitClusterM { get; set; } = MarkingTopologySystem.kDefaultHitClusterM;

        // NOTE: there is deliberately NO runtime "reapply" button. Refreshing the clone prefabs
        // (UpdatePrefab) while a world is live leaves existing sublanes with stale PrefabRefs;
        // the next SecondaryLane rebuild (road edit, even a bulldozer hover creating Temp roads)
        // then dies natively in a Burst job — three crashes on 2026-07-17 before the feature was
        // cut. Settings changes apply after a game restart (observed: a save reload within the
        // same game process is not enough — the clone prefabs persist per process), where
        // ApplyOrUpdate runs in a world with no live references yet. Safe, exercised every boot.

        // --- Phase 4 tool: settings button (always works) + keybind (may conflict with other mods) ---

        [SettingsUIButton]
        [SettingsUISection(kSection, kKeybindGroup)]
        public bool ActivateMarkingTool
        {
            set
            {
                Mod.log.Info("settings button: activate marking tool");
                MarkingToolHotkeySystem.RequestToggle();
            }
        }

        [SettingsUISection(kSection, kKeybindGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.M, ToggleMarkingTool, ctrl: true)]
        public ProxyBinding ToggleMarkingToolBinding { get; set; }

        [SettingsUISection(kSection, kKeybindGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Y, CycleMarkingStyle)]
        public ProxyBinding CycleMarkingStyleBinding { get; set; }

        [SettingsUISection(kSection, kKeybindGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.A, EnterAreaMode)]
        public ProxyBinding EnterAreaModeBinding { get; set; }

        [SettingsUISection(kSection, kKeybindGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.U, CycleAreaStyle)]
        public ProxyBinding CycleAreaStyleBinding { get; set; }

        public bool IsEdgeDisabled() => !EdgeLineEnabled;
        public bool IsParkingDisabled() => !ParkingMarkingsEnabled;

        // Pinned "favourite" styles for the in-game dropdowns (panel + popovers): CSV of the
        // numeric style ids, e.g. "2,6". Managed by the pin buttons in the UI (see
        // TownRoadLaneUISystem TogglePin* triggers), hidden from the options screen — these
        // live here only so they persist like every other setting.
        [SettingsUIHidden]
        public string PinnedLineStylesCsv { get; set; } = "";

        [SettingsUIHidden]
        public string PinnedAreaStylesCsv { get; set; } = "";

        public override void SetDefaults()
        {
            EdgeLineEnabled = true;
            EdgeLineStyle = EdgeLineStyleEnum.WhiteSolid;
            ParkingMarkingsEnabled = true;
            ParkingLineStyle = ParkingLineStyleEnum.WhiteDashed_G87;
            ParkingEndStyle = ParkingEndStyleEnum.WhiteSolid;
            SegmentMinLengthM = MarkingTopologySystem.kDefaultMinSegmentLengthM;
            SegmentAnchorDeadZoneM = MarkingTopologySystem.kDefaultEndpointMarginM;
            SegmentMinCrossingAngleDeg = MarkingTopologySystem.kDefaultMinCrossingAngleDeg;
            SegmentHitClusterM = MarkingTopologySystem.kDefaultHitClusterM;
            PinnedLineStylesCsv = "";
            PinnedAreaStylesCsv = "";
        }

        // G87 RenderPrefab name prefixes — full names are very long; build them once.
        private const string kG87 = "G87 UK Road Markings RoadMarking G87 ";
        private const string kG87Dec = "G87 UK Road Markings RoadMarkings G87 ";

        /// <summary>Resolves the chosen edge-line style to a render-prefab name (vanilla or G87).</summary>
        public string EdgeLineMeshName() => EdgeLineStyle switch
        {
            EdgeLineStyleEnum.WhiteSolid          => "White Solid Line Mesh",
            EdgeLineStyleEnum.WhiteSolidThick     => "White Solid Line Mesh - Thick",
            EdgeLineStyleEnum.WhiteDashed         => "White Dashed Line Mesh",
            EdgeLineStyleEnum.YellowSolid         => "Yellow Solid Line Mesh",
            EdgeLineStyleEnum.WhiteSolid_G87      => kG87 + "UK Carriageway Line White NetLaneDecal_RenderPrefab",
            EdgeLineStyleEnum.WhiteDashed_G87     => kG87 + "UK Carriageway Line White Dashed NetLaneDecal_RenderPrefab",
            EdgeLineStyleEnum.YellowSolid_G87     => kG87 + "UK Carriageway Line Yellow NetLaneDecal_RenderPrefab",
            _ => "White Solid Line Mesh",
        };

        /// <summary>Resolves the chosen longitudinal parking-line style to a render-prefab name (vanilla or G87).</summary>
        public string ParkingLineMeshName() => ParkingLineStyle switch
        {
            ParkingLineStyleEnum.WhiteDashedDense   => "White Dashed Line Mesh - Dense",
            ParkingLineStyleEnum.WhiteDashed        => "White Dashed Line Mesh",
            ParkingLineStyleEnum.WhiteSolid         => "White Solid Line Mesh",
            ParkingLineStyleEnum.YellowDashed       => "Yellow Dashed Line Mesh - Long",
            ParkingLineStyleEnum.YellowSolid        => "Yellow Solid Line Mesh",
            ParkingLineStyleEnum.WhiteSolid_G87     => kG87 + "UK Carriageway Line White NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.WhiteDashed_G87    => kG87 + "UK Carriageway Line White Dashed NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.YellowSolid_G87    => kG87 + "UK Carriageway Line Yellow NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.YellowDashed_G87   => kG87 + "UK Carriageway Line Yellow Dashed NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.BlueSolid_G87      => kG87 + "RM Line Blue NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.BlueDashed_G87     => kG87 + "RM Line Blue Dashed NetLaneDecal_RenderPrefab",
            _ => "White Dashed Line Mesh - Dense",
        };

        /// <summary>Resolves the chosen end-tick style to a render-prefab name (vanilla or G87). Null => no end ticks.</summary>
        public string ParkingEndMeshName() => ParkingEndStyle switch
        {
            ParkingEndStyleEnum.None                => null,
            ParkingEndStyleEnum.WhiteSolid          => "White Solid Line Mesh",
            ParkingEndStyleEnum.WhiteSolidThick     => "White Solid Line Mesh - Thick",
            ParkingEndStyleEnum.WhiteTerminal_G87   => kG87Dec + "UK Terminal Line White Decal_RenderPrefab",
            ParkingEndStyleEnum.YellowTerminal_G87  => kG87Dec + "UK Terminal Line Yellow Decal_RenderPrefab",
            ParkingEndStyleEnum.BlueSolid_G87       => kG87 + "RM Line Blue NetLaneDecal_RenderPrefab",
            _ => "White Solid Line Mesh",
        };

        public enum EdgeLineStyleEnum
        {
            WhiteSolid,
            WhiteSolidThick,
            WhiteDashed,
            YellowSolid,
            WhiteSolid_G87,
            WhiteDashed_G87,
            YellowSolid_G87,
        }

        public enum ParkingLineStyleEnum
        {
            WhiteDashedDense,
            WhiteDashed,
            WhiteSolid,
            YellowDashed,
            YellowSolid,
            WhiteSolid_G87,
            WhiteDashed_G87,
            YellowSolid_G87,
            YellowDashed_G87,
            BlueSolid_G87,
            BlueDashed_G87,
        }

        public enum ParkingEndStyleEnum
        {
            None,
            WhiteSolid,
            WhiteSolidThick,
            WhiteTerminal_G87,
            YellowTerminal_G87,
            BlueSolid_G87,
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly TownRoadLaneSetting m_Setting;
        public LocaleEN(TownRoadLaneSetting setting) { m_Setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Town Road Lane" },
                { m_Setting.GetOptionTabLocaleID(TownRoadLaneSetting.kSection), "Main" },

                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kEdgeGroup), "Curb-side edge line" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kParkingGroup), "Parallel parking markings" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentGroup), "Marking editor — segment splitting" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentDevGroup), "Segment splitting — fine tuning" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kKeybindGroup), "Keybinds" },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)), "Edge line on city roads" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)),
                    "Adds the curb-side edge line to ordinary city roads (3 m car lanes), the way highway roads have it. Changes take effect after the game is restarted." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)), "Edge line style" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)),
                    "The mesh style used for the curb-side edge line. \"G87\" options require the [G87] Road Markings mod; if it isn't installed they fall back to vanilla." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)), "Mark parallel parking zones" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)),
                    "Draws a line along parallel street-parking zones with a cross tick at each end of the block. Roads without a Parking Lane 2 sublane (oneway 3-lane, asymmetric variants) remain unmarked — same coverage as v1.1." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)), "Parking line style" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)),
                    "The longitudinal line drawn along the parking zone. \"G87\" options require the [G87] Road Markings mod." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)), "Parking end-tick style" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)),
                    "The short perpendicular tick at the start and end of a parking block. \"None\" disables the ticks. \"G87\" options require the [G87] Road Markings mod." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)), "Minimum segment length (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)),
                    "When drawn lines cross, they are split into segments (each can be hidden or restyled). Segments shorter than this merge into their neighbour. Lower = finer segments on densely packed markings; higher = fewer slivers from lines that merely graze each other. Default: 1.0 m. Applies to a junction the next time its lines are edited, and everywhere after reloading the save." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)), "Dead zone around anchor dots (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)),
                    "Crossings closer than this to a line's endpoint don't split the line. Lines that leave the same anchor dot overlap for the first metre or two — without the dead zone that overlap spawns phantom micro-segments. Lower = splits allowed closer to the dots; higher = calmer behaviour around anchors. Default: 2.0 m. Applies to a junction the next time its lines are edited, and everywhere after reloading the save." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)), "Minimum crossing angle (°)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)),
                    "Two lines meeting at less than this angle count as a graze, not a crossing — no split. At 0° every touch splits, and near-parallel lines can produce clusters of micro-segments. Default: 8°. Applies to a junction the next time its lines are edited, and everywhere after reloading the save." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)), "Crossing cluster radius (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)),
                    "A shallow crossing is reported as several near-identical intersection points; points within this radius collapse into a single split. Lower = more of those near-duplicates survive as separate splits. Default: 1.5 m. Applies to a junction the next time its lines are edited, and everywhere after reloading the save." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)), "Activate marking tool" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)),
                    "Toggles the per-node marking customisation tool. Same as the keyboard shortcut below, but always works (button cannot be intercepted by other mods)." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)), "Toggle marking tool (hotkey)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)),
                    "Activates or deactivates the per-node marking customisation tool. Default Ctrl+M. If the hotkey doesn't work (other mod intercepts), use the button above instead." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)), "Cycle marking style (hotkey)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)),
                    "While the marking tool is active, cycles through Solid → Dashed → … The chosen style is used for the NEXT line you draw. Default Y. The colour of the endpoint dots reflects the current style." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)), "Start area polygon (hotkey)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)),
                    "With a node selected, starts the polygon-area mode: click anchor dots to build a filled region. Press the same key again or Esc to cancel. Default A." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)), "Cycle area style (hotkey)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)),
                    "While the marking tool is active, cycles the fill style for the NEXT area you close (Solid → Junction Box → White Stripes → Yellow Stripes → Green Bike → Red Bus → back). Default U. G87 styles fall back to Solid when G87 isn't installed." },

                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid), "White solid" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolidThick), "White solid (thick)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed), "White dashed" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid), "Yellow solid" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid_G87), "White solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed_G87), "White dashed (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid_G87), "Yellow solid (G87)" },

                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashedDense), "White dashed (dense)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed), "White dashed" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid), "White solid" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed), "Yellow dashed" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid), "Yellow solid" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid_G87), "White solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed_G87), "White dashed (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid_G87), "Yellow solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed_G87), "Yellow dashed (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueSolid_G87), "Blue solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueDashed_G87), "Blue dashed (G87)" },

                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.None), "None" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolid), "White solid" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolidThick), "White solid (thick)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteTerminal_G87), "White terminal line (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.YellowTerminal_G87), "Yellow terminal line (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.BlueSolid_G87), "Blue solid (G87)" },
            };
        }

        public void Unload() { }
    }

    public class LocaleRU : IDictionarySource
    {
        private readonly TownRoadLaneSetting m_Setting;
        public LocaleRU(TownRoadLaneSetting setting) { m_Setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Town Road Lane" },
                { m_Setting.GetOptionTabLocaleID(TownRoadLaneSetting.kSection), "Основное" },

                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kEdgeGroup), "Краевая линия у бордюра" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kParkingGroup), "Разметка параллельной парковки" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentGroup), "Редактор разметки — разрезание на сегменты" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentDevGroup), "Разрезание сегментов — тонкая настройка" },
                { m_Setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kKeybindGroup), "Горячие клавиши" },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)), "Краевая линия на городских дорогах" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)),
                    "Добавляет краевую линию у бордюра обычным городским дорогам (полосы 3 м) — так же, как на шоссе. Изменения вступают в силу после перезапуска игры." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)), "Стиль краевой линии" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)),
                    "Стиль меша краевой линии. Варианты «G87» требуют мод [G87] Road Markings; без него используется ванильный стиль." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)), "Размечать зоны параллельной парковки" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)),
                    "Рисует линию вдоль зон параллельной уличной парковки с поперечной чертой на концах квартала. Дороги без сублейна Parking Lane 2 (односторонние трёхполосные, асимметричные варианты) остаются без разметки — то же покрытие, что и в v1.1." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)), "Стиль линии парковки" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)),
                    "Продольная линия вдоль парковочной зоны. Варианты «G87» требуют мод [G87] Road Markings." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)), "Стиль концевой черты парковки" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)),
                    "Короткая поперечная черта в начале и конце парковочного квартала. «Нет» отключает черты. Варианты «G87» требуют мод [G87] Road Markings." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)), "Минимальная длина сегмента (м)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)),
                    "Пересекаясь, нарисованные линии режутся на сегменты (каждый можно скрыть или перекрасить). Сегменты короче этого значения сливаются с соседним. Меньше — более дробные сегменты на плотной разметке; больше — меньше «щепок» от линий, которые лишь слегка задевают друг друга. По умолчанию: 1,0 м. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)), "Мёртвая зона у точек-якорей (м)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)),
                    "Пересечения ближе этого расстояния к концу линии не режут её. Линии, выходящие из одной точки-якоря, первые метр-два идут внахлёст — без мёртвой зоны этот нахлёст порождает фантомные микросегменты. Меньше — резы разрешены ближе к точкам; больше — спокойнее возле якорей. По умолчанию: 2,0 м. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)), "Минимальный угол пересечения (°)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)),
                    "Линии, встречающиеся под углом меньше этого, считаются скользящим касанием, а не пересечением — без реза. При 0° режет любое касание, и почти параллельные линии могут дать пачку микросегментов. По умолчанию: 8°. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)), "Радиус склейки пересечений (м)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)),
                    "Пологое пересечение распознаётся как несколько почти совпадающих точек; точки в этом радиусе склеиваются в один рез. Меньше — больше таких почти-дублей выживает отдельными резами. По умолчанию: 1,5 м. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)), "Активировать инструмент разметки" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)),
                    "Включает/выключает инструмент настройки разметки перекрёстков. То же, что горячая клавиша ниже, но работает всегда (кнопку не может перехватить другой мод)." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)), "Инструмент разметки (клавиша)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)),
                    "Включает или выключает инструмент настройки разметки перекрёстков. По умолчанию Ctrl+M. Если клавиша не срабатывает (перехвачена другим модом), используйте кнопку выше." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)), "Стиль линии по кругу (клавиша)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)),
                    "При активном инструменте листает стили: сплошная → пунктир → … Выбранный стиль применяется к СЛЕДУЮЩЕЙ линии. По умолчанию Y. Цвет точек-якорей отражает текущий стиль." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)), "Режим области (клавиша)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)),
                    "При выбранном узле запускает режим полигональной области: кликайте по опорным точкам, чтобы построить заливку. Повторное нажатие или Esc — отмена. По умолчанию A." },

                { m_Setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)), "Стиль области по кругу (клавиша)" },
                { m_Setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)),
                    "При активном инструменте листает стиль заливки для СЛЕДУЮЩЕЙ замкнутой области (бетон → вафельная разметка → белая штриховка → жёлтая штриховка → велополоса → автобусная полоса → сначала). По умолчанию U. Стили G87 без установленного мода G87 заменяются бетоном." },

                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid), "Белая сплошная" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolidThick), "Белая сплошная (толстая)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed), "Белый пунктир" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid), "Жёлтая сплошная" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid_G87), "Белая сплошная (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed_G87), "Белый пунктир (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid_G87), "Жёлтая сплошная (G87)" },

                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashedDense), "Белый пунктир (частый)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed), "Белый пунктир" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid), "Белая сплошная" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed), "Жёлтый пунктир" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid), "Жёлтая сплошная" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid_G87), "Белая сплошная (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed_G87), "Белый пунктир (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid_G87), "Жёлтая сплошная (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed_G87), "Жёлтый пунктир (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueSolid_G87), "Синяя сплошная (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueDashed_G87), "Синий пунктир (G87)" },

                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.None), "Нет" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolid), "Белая сплошная" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolidThick), "Белая сплошная (толстая)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteTerminal_G87), "Белая концевая линия (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.YellowTerminal_G87), "Жёлтая концевая линия (G87)" },
                { m_Setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.BlueSolid_G87), "Синяя сплошная (G87)" },
            };
        }

        public void Unload() { }
    }
}
