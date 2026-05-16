using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Net;
using Game.SceneFlow;
using Colossal.IO.AssetDatabase;
using TownRoadLane.Diagnostics;

namespace TownRoadLane
{
    /// <summary>
    /// v2 phase 0: disable vanilla <see cref="SecondaryLaneSystem"/> and run our drop-in copy
    /// <see cref="CustomSecondaryLaneSystem"/> instead. The copy is currently behaviour-identical
    /// to vanilla — phase 1 will add the per-edge <c>MarkingOverride</c> read inside
    /// <c>UpdateLanesJob.UpdateLanes</c>.
    /// </summary>
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(TownRoadLane)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

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

            // Read-only structural dump, useful when something changes between game patches.
            updateSystem.UpdateAt<RoadPrefabDumpSystem>(SystemUpdatePhase.PrefabUpdate);

            // Disable vanilla markings generator. Cars still drive normally — LaneSystem (primary lanes)
            // is untouched; only the secondary marking pass is replaced.
            var vanilla = updateSystem.World.GetOrCreateSystemManaged<SecondaryLaneSystem>();
            vanilla.Enabled = false;
            log.Info($"vanilla SecondaryLaneSystem disabled (was Enabled={vanilla.Enabled})");

            // Our replacement. Phase 0 is byte-for-byte equivalent to vanilla — success criterion is
            // "city looks identical after enabling the mod". MUST run on Modification4B (not 4) — that's
            // where AllowBarrier<ModificationBarrier4B> lives. Using Modification4 instead would put us
            // outside the barrier's allowed window and SafeCommandBufferSystem.CreateCommandBuffer
            // would throw "Trying to create EntityCommandBuffer when it's not allowed!".
            // See decomp/Game/Game.Common/SystemOrder.cs:184.
            updateSystem.UpdateAt<CustomSecondaryLaneSystem>(SystemUpdatePhase.Modification4B);
            log.Info($"CustomSecondaryLaneSystem registered at Modification4B");

            // Settings-button handler. Idle until the user clicks; touches EntityManager directly so
            // Modification1 is fine (no SafeCommandBufferSystem dance needed).
            updateSystem.UpdateAt<MarkingToggleSystem>(SystemUpdatePhase.Modification1);
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
