using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace TownRoadLane
{
    [FileLocation(nameof(TownRoadLane))]
    [SettingsUIGroupOrder(kEdgeGroup, kParkingGroup, kUpgradeGroup)]
    [SettingsUIShowGroupName(kEdgeGroup, kParkingGroup, kUpgradeGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kEdgeGroup = "EdgeLine";
        public const string kParkingGroup = "ParkingMarkings";
        public const string kUpgradeGroup = "PerSegment";

        public Setting(IMod mod) : base(mod) { }

        // --- Edge line (the curb-side line on city 3 m roads) ---

        [SettingsUISection(kSection, kEdgeGroup)]
        public bool EdgeLineEnabled { get; set; } = true;

        // --- Parallel street-parking markings ---

        [SettingsUISection(kSection, kParkingGroup)]
        public bool ParkingMarkingsEnabled { get; set; } = true;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsParkingDisabled))]
        public ParkingLineStyleEnum ParkingLineStyle { get; set; } = ParkingLineStyleEnum.WhiteDashedDense;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsParkingDisabled))]
        public ParkingEndStyleEnum ParkingEndStyle { get; set; } = ParkingEndStyleEnum.WhiteSolid;

        [SettingsUIButton]
        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsParkingDisabled))]
        public bool ReapplyMarkings
        {
            set
            {
                Mod.log.Info("Reapply markings requested from settings");
                MarkingReapplySystem.RequestReapply();
            }
        }

        public bool IsParkingDisabled() => !ParkingMarkingsEnabled;

        // --- Per-segment "Lane Markings" road upgrade (toolbar) ---

        [SettingsUISection(kSection, kUpgradeGroup)]
        public bool SegmentToggleEnabled { get; set; } = true;

        // Changing any of these only takes effect on the next game start (the marking prefabs are built once,
        // during prefab initialization).

        public override void SetDefaults()
        {
            EdgeLineEnabled = true;
            ParkingMarkingsEnabled = true;
            ParkingLineStyle = ParkingLineStyleEnum.WhiteDashedDense;
            ParkingEndStyle = ParkingEndStyleEnum.WhiteSolid;
            SegmentToggleEnabled = true;
        }

        /// <summary>Resolves the chosen longitudinal-line style to a render-prefab name (vanilla or G87).</summary>
        // G87 RenderPrefab name fragments — full names are very long; build them once.
        private const string kG87 = "G87 UK Road Markings RoadMarking G87 ";
        private const string kG87Dec = "G87 UK Road Markings RoadMarkings G87 ";

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
        private readonly Setting m_Setting;
        public LocaleEN(Setting setting) { m_Setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Town Road Lane" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },

                { m_Setting.GetOptionGroupLocaleID(Setting.kEdgeGroup), "Curb-side edge line" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kParkingGroup), "Parallel parking markings" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kUpgradeGroup), "Per-segment toggle" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EdgeLineEnabled)), "Edge line on city roads" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EdgeLineEnabled)),
                    "Adds the curb-side edge line to ordinary city roads (3 m car lanes), the way highway roads have it. Takes effect on the next game start." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ParkingMarkingsEnabled)), "Mark parallel parking zones" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ParkingMarkingsEnabled)),
                    "Draws a line along parallel street-parking zones with a cross tick at each end of the block. Takes effect on the next game start." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ParkingLineStyle)), "Parking line style" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ParkingLineStyle)),
                    "The longitudinal line drawn along the parking zone. \"G87\" options require the [G87] Road Markings mod; if it is not installed they fall back to a white line." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ParkingEndStyle)), "Parking end-tick style" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ParkingEndStyle)),
                    "The short perpendicular tick at the start and end of a parking block. \"None\" disables the ticks. \"G87\" options require the [G87] Road Markings mod." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ReapplyMarkings)), "Reapply markings now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ReapplyMarkings)),
                    "Applies the chosen parking line / end-tick style to roads already built in the current city, without restarting. On a large city this causes a brief freeze while the network rebuilds." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SegmentToggleEnabled)), "\"Lane Markings\" road upgrade" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SegmentToggleEnabled)),
                    "Adds a \"Lane Markings\" upgrade to the road toolbar's upgrade row (next to Lighting / Trees / …). Paint it on a segment to remove this mod's markings there — handy for small courtyard / alley roads where markings look out of place. Takes effect on the next game start." },

                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.WhiteDashedDense), "White dashed (dense)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.WhiteDashed), "White dashed" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.WhiteSolid), "White solid" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.YellowDashed), "Yellow dashed" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.YellowSolid), "Yellow solid" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.WhiteSolid_G87), "White solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.WhiteDashed_G87), "White dashed (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.YellowSolid_G87), "Yellow solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.YellowDashed_G87), "Yellow dashed (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.BlueSolid_G87), "Blue solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingLineStyleEnum.BlueDashed_G87), "Blue dashed (G87)" },

                { m_Setting.GetEnumValueLocaleID(Setting.ParkingEndStyleEnum.None), "None" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingEndStyleEnum.WhiteSolid), "White solid" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingEndStyleEnum.WhiteSolidThick), "White solid (thick)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingEndStyleEnum.WhiteTerminal_G87), "White terminal line (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingEndStyleEnum.YellowTerminal_G87), "Yellow terminal line (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.ParkingEndStyleEnum.BlueSolid_G87), "Blue solid (G87)" },
            };
        }

        public void Unload() { }
    }
}
