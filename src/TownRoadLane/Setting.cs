using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace TownRoadLane
{
    /// <summary>
    /// v2 phase 1 settings. Per-feature on/off + per-feature mesh style (vanilla or G87).
    /// Each feature's style is read by the corresponding clone system in PrefabUpdate phase
    /// and resolved against the loaded mesh prefab set; "G87 …" options gracefully fall back
    /// to vanilla if the G87 Road Markings mod isn't installed (see K7 in IMPLEMENTATION_PLAN.md).
    ///
    /// Reapply Markings button triggers MarkingToggleSystem, which re-runs both clone systems'
    /// ApplyOrUpdate before mass-marking every road edge Updated so live roads pick up the change.
    /// </summary>
    [FileLocation(nameof(TownRoadLane))]
    [SettingsUIGroupOrder(kEdgeGroup, kParkingGroup, kReapplyGroup)]
    [SettingsUIShowGroupName(kEdgeGroup, kParkingGroup, kReapplyGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kEdgeGroup = "EdgeLine";
        public const string kParkingGroup = "ParkingMarkings";
        public const string kReapplyGroup = "Reapply";

        public Setting(IMod mod) : base(mod) { }

        // --- Edge line (curb-side line on city 3 m roads) ---

        [SettingsUISection(kSection, kEdgeGroup)]
        public bool EdgeLineEnabled { get; set; } = true;

        [SettingsUISection(kSection, kEdgeGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsEdgeDisabled))]
        public EdgeLineStyleEnum EdgeLineStyle { get; set; } = EdgeLineStyleEnum.WhiteSolid;

        // --- Parallel street-parking markings ---

        [SettingsUISection(kSection, kParkingGroup)]
        public bool ParkingMarkingsEnabled { get; set; } = true;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsParkingDisabled))]
        public ParkingLineStyleEnum ParkingLineStyle { get; set; } = ParkingLineStyleEnum.WhiteDashedDense;

        [SettingsUISection(kSection, kParkingGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsParkingDisabled))]
        public ParkingEndStyleEnum ParkingEndStyle { get; set; } = ParkingEndStyleEnum.WhiteSolid;

        // --- Reapply (single button covers both features) ---

        [SettingsUIButton]
        [SettingsUISection(kSection, kReapplyGroup)]
        public bool ReapplyMarkings
        {
            set
            {
                Mod.log.Info("Reapply markings requested from settings");
                MarkingToggleSystem.RequestReapply();
            }
        }

        public bool IsEdgeDisabled() => !EdgeLineEnabled;
        public bool IsParkingDisabled() => !ParkingMarkingsEnabled;

        public override void SetDefaults()
        {
            EdgeLineEnabled = true;
            EdgeLineStyle = EdgeLineStyleEnum.WhiteSolid;
            ParkingMarkingsEnabled = true;
            ParkingLineStyle = ParkingLineStyleEnum.WhiteDashedDense;
            ParkingEndStyle = ParkingEndStyleEnum.WhiteSolid;
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kReapplyGroup), "Apply to existing roads" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EdgeLineEnabled)), "Edge line on city roads" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EdgeLineEnabled)),
                    "Adds the curb-side edge line to ordinary city roads (3 m car lanes), the way highway roads have it. Takes effect on the next road update — for live roads, use the Reapply button below." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EdgeLineStyle)), "Edge line style" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EdgeLineStyle)),
                    "The mesh style used for the curb-side edge line. \"G87\" options require the [G87] Road Markings mod; if it isn't installed they fall back to vanilla." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ParkingMarkingsEnabled)), "Mark parallel parking zones" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ParkingMarkingsEnabled)),
                    "Draws a line along parallel street-parking zones with a cross tick at each end of the block. Roads without a Parking Lane 2 sublane (oneway 3-lane, asymmetric variants) remain unmarked — same coverage as v1.1." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ParkingLineStyle)), "Parking line style" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ParkingLineStyle)),
                    "The longitudinal line drawn along the parking zone. \"G87\" options require the [G87] Road Markings mod." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ParkingEndStyle)), "Parking end-tick style" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ParkingEndStyle)),
                    "The short perpendicular tick at the start and end of a parking block. \"None\" disables the ticks. \"G87\" options require the [G87] Road Markings mod." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ReapplyMarkings)), "Reapply markings now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ReapplyMarkings)),
                    "Re-applies the chosen styles and rebuilds markings on every road already in the city. On a large city this causes a brief freeze. Road Builder roads are skipped to avoid known crash patterns." },

                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.WhiteSolid), "White solid" },
                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.WhiteSolidThick), "White solid (thick)" },
                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.WhiteDashed), "White dashed" },
                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.YellowSolid), "Yellow solid" },
                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.WhiteSolid_G87), "White solid (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.WhiteDashed_G87), "White dashed (G87)" },
                { m_Setting.GetEnumValueLocaleID(Setting.EdgeLineStyleEnum.YellowSolid_G87), "Yellow solid (G87)" },

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
