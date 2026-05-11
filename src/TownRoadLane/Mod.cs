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

            // Feature: per-segment "Lane Markings" road upgrade — creates the toolbar entry / upgrade prefab.
            updateSystem.UpdateAt<MarkingUpgradePrefabSystem>(SystemUpdatePhase.PrefabUpdate);

            // Hot-reapply of the parking line style (triggered by the settings button). Idle until requested.
            updateSystem.UpdateAt<MarkingReapplySystem>(SystemUpdatePhase.Modification1);

            // Per-segment "Lane Markings" upgrade — removes our markings on edges that carry the upgrade bit.
            // In Modification5 (so ModificationBarrier4B has already played back and the sub-lanes SecondaryLaneSystem
            // created in Modification4B exist), but BEFORE Game.Net.SearchSystem — otherwise the spatial tree picks
            // up a sub-lane we then Delete in the same frame, and the renderer/raycast crashes following the stale ref.
            updateSystem.UpdateBefore<MarkingSuppressSystem, Game.Net.SearchSystem>(SystemUpdatePhase.Modification5);
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
