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
        // Singleton handle to the live mod instance — needed by TownRoadLaneUISystem so it can
        // resolve its own ExecutableAsset path to read the React bundle. ModManager indexes
        // assets by mod instance; using a fresh new Mod() would lose that mapping.
        public static Mod Instance { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Instance = this;
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
            GameManager.instance.localizationManager.AddSource("ru-RU", new LocaleRU(Settings));
            AssetDatabase.global.LoadSettings(nameof(TownRoadLane), Settings, new Setting(this));

            // Read-only structural dump, useful when something changes between game patches.
            updateSystem.UpdateAt<RoadPrefabDumpSystem>(SystemUpdatePhase.PrefabUpdate);
            // Phase 6 prototype: one-shot probes for Shader.Find + vanilla SurfacePrefab inventory.
            // Self-disables after first run. Remove from registration once Phase 6 is wired.
            updateSystem.UpdateAt<AreasPrototypeSystem>(SystemUpdatePhase.PrefabUpdate);
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

            // Phase 4 step 4 (B.1 revive): spawn vanilla SecondaryLane entities per user pair.
            // The earlier custom-mesh path (MarkingMeshRenderSystem on Graphics.DrawMesh /
            // GameObject+MeshRenderer) was exhaustively explored and proven incompatible with
            // HDRP's DBufferMesh pass — vanilla decal shaders depend on DOTS InstanceProperties
            // (colossal_CurveMatrix) only BRG can supply. See commits 35e504c..7d6a9f1 +
            // research/RESEARCH_decal_*.md for the full dead-end exploration.
            //
            // ECS path: spawn entity with edge-line clone prefab's archetype, vanilla BRG
            // pipeline picks it up via Game.Net.SecondaryLane tag (auto-included by archetype),
            // SecondaryLaneReferencesSystem registers it in node.SubLane at Modification5,
            // vanilla CurvedDecalShader renders with full quality. Same path EAI/RealVision use.
            // See research/RESEARCH_sublane_lifecycle.md for the full spec.
            //
            // Modification1 puts us BEFORE LaneSystem (4) / SecondaryLaneSystem (4B) /
            // SecondaryLaneReferencesSystem (5) — same phase the old commits used; proven safe.
            // Stage 5b migration: rewrite v2 MarkingPair buffers as v3 MarkingLine+MarkingSegment
            // on first sight of a node. Idempotent + cheap (empty query 99% of frames). Must run
            // before emission — [UpdateBefore] on the class handles ordering inside Modification1.
            updateSystem.UpdateAt<MarkingPairMigrationSystem>(SystemUpdatePhase.Modification1);
            // Stage 5b: pairwise Bezier intersection + segment buffer rewrite. Ordered between
            // migration and emission via [UpdateAfter]/[UpdateBefore] on the class itself.
            updateSystem.UpdateAt<MarkingTopologySystem>(SystemUpdatePhase.Modification1);
            // Stage 5b emission: spawn one sublane per visible MarkingSegment (replaces the
            // Phase-4 MarkingPairEmissionSystem which keyed off MarkingPair). The old system is
            // intentionally not registered any more — its TRLPairLink entities get GC'd by
            // MarkingSegmentEmissionSystem on first tick after migration.
            updateSystem.UpdateAt<MarkingSegmentEmissionSystem>(SystemUpdatePhase.Modification1);
            // Phase 6e: split areas at every line intersection. Must run AFTER MarkingTopologySystem
            // (line buffer up-to-date) and BEFORE MarkingAreaEmissionSystem (piece buffer must be
            // fresh when emission diffs). [UpdateAfter]/[UpdateBefore] on the class enforces this.
            updateSystem.UpdateAt<MarkingAreaTopologySystem>(SystemUpdatePhase.Modification1);
            // Phase 6c: per-node MarkingArea → vanilla Game.Areas.Area emitter. Same Modification1
            // phase as the line emitter (independent buffers; no ordering required between them).
            updateSystem.UpdateAt<MarkingAreaEmissionSystem>(SystemUpdatePhase.Modification1);

            // Stage 5d: React panel bridge. UISystemBase wants UIUpdate phase.
            updateSystem.UpdateAt<TownRoadLaneUISystem>(SystemUpdatePhase.UIUpdate);

            // MarkingMeshRenderSystem (HDRP/Unlit + GameObject pipeline) kept as commented
            // fallback in case ECS path reveals an unknown blocker. Source file stays in tree.
            // updateSystem.UpdateAt<MarkingMeshRenderSystem>(SystemUpdatePhase.Rendering);
            // updateSystem.UpdateAt<UserPairEmissionDumpSystem>(SystemUpdatePhase.GameSimulation);
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
