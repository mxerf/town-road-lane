using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Colossal.IO.AssetDatabase;
using TownRoadLane.Diagnostics;

namespace TownRoadLane
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(TownRoadLane)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        /// <summary>The active mod settings. Set in OnLoad; read by the prefab-patch systems.</summary>
        public static Setting Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(TownRoadLane), Settings, new Setting(this));

            // Diagnostics: dump road prefab structure once after prefabs are loaded (kept for verification).
            updateSystem.UpdateAt<RoadPrefabDumpSystem>(SystemUpdatePhase.PrefabUpdate);

            // Feature: give city 3 m drive lanes the curb-side edge marking line.
            updateSystem.UpdateAt<EdgeMarkingPatchSystem>(SystemUpdatePhase.PrefabUpdate);

            // Feature: markings along parallel street-parking zones.
            updateSystem.UpdateAt<ParkingMarkingPatchSystem>(SystemUpdatePhase.PrefabUpdate);

            // Hot-reapply of the parking line style (triggered by the settings button). Idle until requested.
            updateSystem.UpdateAt<MarkingReapplySystem>(SystemUpdatePhase.Modification1);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
