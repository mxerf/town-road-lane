using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace TownRoadLane
{
    /// <summary>
    /// v2 settings shell. Phase 0 doesn't expose any options yet — the mod runs `CustomSecondaryLaneSystem`
    /// as a drop-in replacement for vanilla `SecondaryLaneSystem`. Real options arrive with phase 1+.
    /// </summary>
    [FileLocation(nameof(TownRoadLane))]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";

        public Setting(IMod mod) : base(mod) { }

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
            };
        }

        public void Unload() { }
    }
}
