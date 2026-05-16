using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace TownRoadLane
{
    /// <summary>
    /// v2 phase 1 settings. The "Toggle all markings" button is a cheat-style switch that adds or
    /// removes <see cref="MarkingOverride"/> on every road edge + intersection node in the world.
    /// Real per-segment / per-node UI comes with the marking tool in later phases.
    /// </summary>
    [FileLocation(nameof(TownRoadLane))]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kToggleGroup = "Toggle";

        public Setting(IMod mod) : base(mod) { }

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

        public override void SetDefaults() { }
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kToggleGroup), "Marking toggle" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ToggleAllMarkings)), "Toggle all markings" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ToggleAllMarkings)),
                    "Hides or shows the engine-generated road markings on EVERY road and intersection in the current city. " +
                    "Click once to hide, click again to bring them back. Road Builder roads are not affected. " +
                    "On a large city this can cause a brief freeze while the network rebuilds." },
            };
        }

        public void Unload() { }
    }
}
