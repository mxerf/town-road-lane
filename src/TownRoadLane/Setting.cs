using System;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using Game.UI;
using TownRoadLane.Systems.Tool;
using TownRoadLane.Systems.Topology;

namespace TownRoadLane
{
    /// <summary>
    /// Mod settings: an on/off switch and a mesh style (vanilla or G87) per automatic feature,
    /// segment-splitting thresholds for the marking editor, and keybinds. Each clone system reads
    /// its style when it builds prefabs; G87 options fall back to vanilla when the G87 Road
    /// Markings mod isn't loaded. Style changes need a game restart (see the note above the
    /// keybind group).
    /// </summary>
    [FileLocation(nameof(TownRoadLane))]
    [SettingsUIGroupOrder(kEdgeGroup, kParkingGroup, kSegmentGroup, kSegmentDevGroup, kKeybindGroup)]
    [SettingsUIShowGroupName(kEdgeGroup, kParkingGroup, kSegmentGroup, kSegmentDevGroup, kKeybindGroup)]
    [SettingsUIKeyboardAction(ToggleMarkingTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(CycleMarkingStyle, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(EnterAreaMode, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(CycleAreaStyle, Usages.kToolUsage)]
    // The class name must be unique among all installed mods: ApplyAndSave() calls
    // AssetDatabase.SaveSpecificSetting(GetType().Name), which matches settings by bare type name
    // across every mod and takes the first hit. With the template name "Setting" and another
    // template-based mod installed, changes are written to the other mod's file and ours never
    // persist.
    public class TownRoadLaneSetting : ModSetting
    {
        public const string kSection = "Main";
        public const string kEdgeGroup = "EdgeLine";
        public const string kParkingGroup = "ParkingMarkings";
        public const string kSegmentGroup = "SegmentSplit";
        public const string kSegmentDevGroup = "SegmentSplitDev";
        public const string kKeybindGroup = "Keybinds";

        // Action names. Each must match its SettingsUIKeyboardAction attribute on this class and
        // its binding property below.
        public const string ToggleMarkingTool = "ToggleMarkingTool";

        // Cycles the line style for the next drawn line. This and the two actions below use
        // Usages.kToolUsage, so their keys only listen while the marking tool is active and don't
        // clash with vanilla shortcuts.
        public const string CycleMarkingStyle = "CycleMarkingStyle";

        // Starts polygon-area selection from a selected node.
        public const string EnterAreaMode = "EnterAreaMode";

        // Cycles the fill style for the next closed area.
        public const string CycleAreaStyle = "CycleAreaStyle";

        public TownRoadLaneSetting(IMod mod) : base(mod) { }

        // Vanilla ApplyAndSave() is async void: every checkbox click or pin toggle starts its own
        // read-modify-write of the settings file, and two quick changes race, so the slower task
        // can overwrite the faster one's write. ApplyAndSave isn't virtual, so instead Apply()
        // marks the state dirty and one extra save writes the final in-memory state once the UI
        // has been quiet for a couple of seconds.
        private const int kQuietFramesBeforeSave = 120; // ~2 s at 60 fps

        private bool _saveDirty;
        private int _quietFrames;
        private bool _saverRegistered;

        public override void Apply()
        {
            base.Apply();
            _saveDirty = true;
            _quietFrames = 0;
            if (_saverRegistered) return;
            _saverRegistered = true;
            MainThreadDispatcher.RegisterUpdater(SaveWhenQuiet);
        }

        // Never unregisters (returns false), so later Apply() calls reuse it. Idle cost is one
        // bool check per frame.
        private bool SaveWhenQuiet()
        {
            if (!_saveDirty) return false;
            if (++_quietFrames < kQuietFramesBeforeSave) return false;
            _saveDirty = false;
            SaveNow();
            return false;
        }

        private async void SaveNow()
        {
            try
            {
                await AssetDatabase.global.SaveSpecificSetting(GetType().Name);
                Mod.log.Debug($"settings: coalesced save landed (edge={EdgeLineEnabled}/{EdgeLineStyle}, parking={ParkingMarkingsEnabled}/{ParkingLineStyle}/{ParkingEndStyle})");
            }
            catch (Exception e)
            {
                Mod.log.Warn($"settings: coalesced save failed: {e.Message}");
            }
        }

        // Edge line: curb-side line on city roads with 3 m lanes.

        [SettingsUISection(kSection, kEdgeGroup)]
        public bool EdgeLineEnabled { get; set; } = true;

        [SettingsUISection(kSection, kEdgeGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsEdgeDisabled))]
        public EdgeLineStyleEnum EdgeLineStyle { get; set; } = EdgeLineStyleEnum.WhiteSolid;

        // US convention: NA-theme cities get a yellow line on the left (median) edge of one-way
        // and divided carriageways, while the white edge line stays curb-side. No explicit theme
        // check is needed: the yellow clone is based on the NA prefab and inherits its
        // ThemeObject, so EU-theme cities never spawn it.
        [SettingsUISection(kSection, kEdgeGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsEdgeDisabled))]
        public bool YellowLeftLineEnabled { get; set; } = true;

        // Parallel street-parking markings.

        [SettingsUISection(kSection, kParkingGroup)]
        public bool ParkingMarkingsEnabled { get; set; } = true;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsParkingDisabled))]
        // G87 is a dependency of the mod, so the G87 dashed decal is the default. Without G87,
        // ParkingLineCloneSystem falls back to the vanilla dense dashed mesh.
        public ParkingLineStyleEnum ParkingLineStyle { get; set; } = ParkingLineStyleEnum.WhiteDashed_G87;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(TownRoadLaneSetting), nameof(IsParkingDisabled))]
        public ParkingEndStyleEnum ParkingEndStyle { get; set; } = ParkingEndStyleEnum.WhiteSolid;

        // Segment-splitting thresholds for MarkingTopologySystem. Unlike the styles above, these
        // need no restart: a junction re-segments the next time its lines change, and every
        // junction re-segments on save load because the topology hash isn't saved.
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

        // There is deliberately no runtime "reapply" button. Refreshing the clone prefabs
        // (UpdatePrefab) while a world is live leaves existing sublanes with stale PrefabRefs, and
        // the next SecondaryLane rebuild (any road edit, even a bulldozer hover creating Temp
        // roads) crashes natively in a Burst job. Style changes therefore apply after a game
        // restart, when ApplyOrUpdate runs before anything references the clones. Reloading a
        // save is not enough: the clone prefabs live for the whole game process.

        // Marking tool: the settings button always works, the keybind may be taken by another mod.

        [SettingsUIButton]
        [SettingsUISection(kSection, kKeybindGroup)]
        public bool ActivateMarkingTool
        {
            set
            {
                Mod.log.Debug("settings button: activate marking tool");
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

        // Pinned styles for the in-game style dropdowns: CSV of numeric style ids, e.g. "2,6".
        // Set by the pin buttons (TogglePin* triggers in TownRoadLaneUISystem) and hidden from
        // the options screen; they live here only to be persisted with the other settings.
        [SettingsUIHidden]
        public string PinnedLineStylesCsv { get; set; } = "";

        [SettingsUIHidden]
        public string PinnedAreaStylesCsv { get; set; } = "";

        // Per-action tool, panel and rebuild messages at Debug level, for bug reports. Hidden from
        // the options screen; to enable, set "VerboseLogging": true in TownRoadLane.coc and
        // restart the game.
        [SettingsUIHidden]
        public bool VerboseLogging { get; set; } = false;

#if DEBUG
        // Developer prefab dumps at boot (RoadPrefabDumpSystem, AreasPrototypeSystem): tens of
        // thousands of log lines per start, enough for Skyve to flag the mod for extreme logging.
        // Debug builds only and hidden from the options screen; to enable, set
        // "DiagnosticDumps": true in TownRoadLane.coc and restart the game.
        [SettingsUIHidden]
        public bool DiagnosticDumps { get; set; } = false;
#endif

        public override void SetDefaults()
        {
            EdgeLineEnabled = true;
            EdgeLineStyle = EdgeLineStyleEnum.WhiteSolid;
            YellowLeftLineEnabled = true;
            ParkingMarkingsEnabled = true;
            ParkingLineStyle = ParkingLineStyleEnum.WhiteDashed_G87;
            ParkingEndStyle = ParkingEndStyleEnum.WhiteSolid;
            SegmentMinLengthM = MarkingTopologySystem.kDefaultMinSegmentLengthM;
            SegmentAnchorDeadZoneM = MarkingTopologySystem.kDefaultEndpointMarginM;
            SegmentMinCrossingAngleDeg = MarkingTopologySystem.kDefaultMinCrossingAngleDeg;
            SegmentHitClusterM = MarkingTopologySystem.kDefaultHitClusterM;
            PinnedLineStylesCsv = "";
            PinnedAreaStylesCsv = "";
            VerboseLogging = false;
#if DEBUG
            DiagnosticDumps = false;
#endif
        }

        // Common prefixes of G87 RenderPrefab names.
        private const string kG87 = "G87 UK Road Markings RoadMarking G87 ";
        private const string kG87Dec = "G87 UK Road Markings RoadMarkings G87 ";

        /// <summary>Resolves the chosen edge-line style to a render-prefab name (vanilla or G87).</summary>
        public string EdgeLineMeshName() => EdgeLineStyle switch
        {
            EdgeLineStyleEnum.WhiteSolid => "White Solid Line Mesh",
            EdgeLineStyleEnum.WhiteSolidThick => "White Solid Line Mesh - Thick",
            EdgeLineStyleEnum.WhiteDashed => "White Dashed Line Mesh",
            EdgeLineStyleEnum.YellowSolid => "Yellow Solid Line Mesh",
            EdgeLineStyleEnum.WhiteSolid_G87 => kG87 + "UK Carriageway Line White NetLaneDecal_RenderPrefab",
            EdgeLineStyleEnum.WhiteDashed_G87 => kG87 + "UK Carriageway Line White Dashed NetLaneDecal_RenderPrefab",
            EdgeLineStyleEnum.YellowSolid_G87 => kG87 + "UK Carriageway Line Yellow NetLaneDecal_RenderPrefab",
            _ => "White Solid Line Mesh",
        };

        /// <summary>Resolves the chosen longitudinal parking-line style to a render-prefab name (vanilla or G87).</summary>
        public string ParkingLineMeshName() => ParkingLineStyle switch
        {
            ParkingLineStyleEnum.WhiteDashedDense => "White Dashed Line Mesh - Dense",
            ParkingLineStyleEnum.WhiteDashed => "White Dashed Line Mesh",
            ParkingLineStyleEnum.WhiteSolid => "White Solid Line Mesh",
            ParkingLineStyleEnum.YellowDashed => "Yellow Dashed Line Mesh - Long",
            ParkingLineStyleEnum.YellowSolid => "Yellow Solid Line Mesh",
            ParkingLineStyleEnum.WhiteSolid_G87 => kG87 + "UK Carriageway Line White NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.WhiteDashed_G87 => kG87 + "UK Carriageway Line White Dashed NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.YellowSolid_G87 => kG87 + "UK Carriageway Line Yellow NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.YellowDashed_G87 => kG87 + "UK Carriageway Line Yellow Dashed NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.BlueSolid_G87 => kG87 + "RM Line Blue NetLaneDecal_RenderPrefab",
            ParkingLineStyleEnum.BlueDashed_G87 => kG87 + "RM Line Blue Dashed NetLaneDecal_RenderPrefab",
            _ => "White Dashed Line Mesh - Dense",
        };

        /// <summary>Resolves the chosen end-tick style to a render-prefab name (vanilla or G87); null means no end ticks.</summary>
        public string ParkingEndMeshName() => ParkingEndStyle switch
        {
            ParkingEndStyleEnum.None => null,
            ParkingEndStyleEnum.WhiteSolid => "White Solid Line Mesh",
            ParkingEndStyleEnum.WhiteSolidThick => "White Solid Line Mesh - Thick",
            ParkingEndStyleEnum.WhiteTerminal_G87 => kG87Dec + "UK Terminal Line White Decal_RenderPrefab",
            ParkingEndStyleEnum.YellowTerminal_G87 => kG87Dec + "UK Terminal Line Yellow Decal_RenderPrefab",
            ParkingEndStyleEnum.BlueSolid_G87 => kG87 + "RM Line Blue NetLaneDecal_RenderPrefab",
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
}
