using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace TownRoadLane
{
    /// <summary>
    /// v2 phase 2a settings. Global toggles for the mod-added markings (edge-line on city 3 m drive
    /// lanes; phase 2a-step1 ships just this one). Per-segment override via the upgrade tool comes in
    /// phase 2b. Style picker (vanilla / G87) comes back when we wire the mesh swap onto our
    /// resolved prefabs — keeping it off the UI until then so we don't show options that do nothing.
    /// </summary>
    [FileLocation(nameof(TownRoadLane))]
    [SettingsUIGroupOrder(kFeatureGroup, kToggleGroup)]
    [SettingsUIShowGroupName(kFeatureGroup, kToggleGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kFeatureGroup = "Features";
        public const string kToggleGroup = "Toggle";

        public Setting(IMod mod) : base(mod) { }

        // --- Features (global on/off; per-segment override is phase 2b) ---

        [SettingsUISection(kSection, kFeatureGroup)]
        public bool EdgeLineEnabled { get; set; } = true;

        // --- Dev/debug toggle (kept from phase 1; will be replaced by the tool eventually) ---

        [SettingsUIButton]
        [SettingsUISection(kSection, kToggleGroup)]
        public bool ToggleAllMarkings
        {
            set
            {
                Mod.log.Info("Toggle all markings requested from settings");
                MarkingToggleSystem.RequestToggle();
            }
        }

        public override void SetDefaults()
        {
            EdgeLineEnabled = true;
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kFeatureGroup), "Features" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kToggleGroup), "Marking toggle" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EdgeLineEnabled)), "Edge line on city roads" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EdgeLineEnabled)),
                    "Adds the curb-side edge line to ordinary city roads (3 m car lanes), the way highway roads have it. Takes effect when the next road update flows through (place / move a piece nearby, or use the toggle button below)." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ToggleAllMarkings)), "Toggle all markings (dev)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ToggleAllMarkings)),
                    "Hides or shows ALL engine-generated road markings on every road and intersection in the current city. Click once to hide, click again to bring them back. Road Builder roads are not affected. Will be replaced by a per-segment upgrade tool in a later version." },
            };
        }

        public void Unload() { }
    }
}
