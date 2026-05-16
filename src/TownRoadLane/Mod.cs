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
    /// Entry point. Disables vanilla <see cref="SecondaryLaneSystem"/>, registers our drop-in copy
    /// <see cref="CustomSecondaryLaneSystem"/> (Layer 2) plus the per-entity <c>MarkingOverride</c>
    /// toggle (Layer 3). Layers 1 (clone systems) and 4 (node UI tool) are added in later phases —
    /// see IMPLEMENTATION_PLAN.md.
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
            // RegisterKeyBindings must run BEFORE GetAction() resolves anything. Without this call
            // the ProxyAction for ToggleMarkingTool never fires (silent — no warn). Traffic's
            // Mod.cs:56-57 does the same: RegisterKeyBindings before RegisterInOptionsUI.
            Settings.RegisterKeyBindings();
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(TownRoadLane), Settings, new Setting(this));

            // Read-only structural dump, useful when something changes between game patches.
            updateSystem.UpdateAt<RoadPrefabDumpSystem>(SystemUpdatePhase.PrefabUpdate);
            // ParkingPairDumpSystem is kept in the tree for phase 4 endpoint-extraction debugging.
            // Re-register when needed: updateSystem.UpdateAt<ParkingPairDumpSystem>(SystemUpdatePhase.GameSimulation);

            // Layer 1: clone vanilla marking prefabs (one-shot per session, self-disables after first run).
            // Both must live in PrefabUpdate so PrefabSystem.UpdatePrefab fires NetInitializeSystem on the
            // same frame and the SecondaryNetLane buffers are baked before road geometry processes them.
            // See K2 / K4 in IMPLEMENTATION_PLAN.md.
            updateSystem.UpdateAt<EdgeLineCloneSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateAt<ParkingLineCloneSystem>(SystemUpdatePhase.PrefabUpdate);

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

            // "Reapply markings" button handler. Idle until the user clicks; re-runs both clone systems'
            // ApplyOrUpdate then mass-marks edges/nodes Updated. Modification1 is fine here (no
            // SafeCommandBufferSystem dance needed — we touch EntityManager directly).
            updateSystem.UpdateAt<MarkingToggleSystem>(SystemUpdatePhase.Modification1);

            // Phase 4 tool: per-node marking customisation. ToolBaseSystem self-registers with
            // ToolSystem.tools in its OnCreate; we just need to instantiate it. Update phase per
            // vanilla tool convention (ToolBaseSystem.cs base wires its own ToolUpdate path).
            updateSystem.UpdateAt<MarkingNodeToolSystem>(SystemUpdatePhase.ToolUpdate);
            // Hotkey poller — flips activeTool when Ctrl+M fires. Cheap WasPerformedThisFrame check.
            updateSystem.UpdateAt<MarkingToolHotkeySystem>(SystemUpdatePhase.Modification1);
            // Overlay renderer for connector dots, drag-line, and confirmed pairs. Gated on
            // activeTool == MarkingNodeToolSystem; idle otherwise. Rendering phase is fine here
            // (we read tool state, write to vanilla OverlayRenderSystem.Buffer).
            updateSystem.UpdateAt<MarkingOverlaySystem>(SystemUpdatePhase.Rendering);
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
