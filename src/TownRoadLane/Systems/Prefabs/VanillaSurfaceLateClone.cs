using System;
using System.Reflection;
using Colossal.Core;
using Colossal.Logging;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Unity.Entities;
using UnityEngine;

namespace TownRoadLane.Systems.Prefabs
{
    /// <summary>
    /// Builds the vanilla-surface fill styles (grass, sand, pavement, tiles; style slots 15+) by
    /// registering copies of vanilla decorative SurfacePrefabs. Each copy gets its own instance of
    /// the vanilla material, the vanilla renderer priority and the Roads decal layer added.
    ///
    /// Registration has to happen on a regular frame after loading, the same way
    /// ExtraAssetsImporter registers G87 surfaces. Clones created while a save is loading render
    /// as the grey 'Missing Area' prefab: the area batch system only picks up prefabs in their
    /// Created frame, and that frame passes before rendering starts.
    /// </summary>
    public static class VanillaSurfaceLateClone
    {
        private static readonly ILog log = Mod.log;

        // Prefab names are saved with the fill areas that use them, so they must never change.
        public const string kCloneGrass = "TRL Grass Surface";
        public const string kCloneGrassDark = "TRL Grass Dark Surface";
        public const string kCloneSand = "TRL Sand Surface";
        public const string kClonePavement = "TRL Pavement Surface";
        public const string kCloneTiles1 = "TRL Tiles 1 Surface";
        public const string kCloneTiles2 = "TRL Tiles 2 Surface";
        public const string kCloneTiles3 = "TRL Tiles 3 Surface";

        private static readonly (string source, string clone)[] kSurfaceClones =
        {
            ("Grass Surface 01",    kCloneGrass),
            ("Grass Surface 02",    kCloneGrassDark),
            ("Sand Surface 01",     kCloneSand),
            ("Pavement Surface 01", kClonePavement),
            ("Tiles Surface 01",    kCloneTiles1),
            ("Tiles Surface 02",    kCloneTiles2),
            ("Tiles Surface 03",    kCloneTiles3),
        };

        // Process-wide on purpose, never reset: Register runs once from Mod.OnLoad and the clones
        // stay in PrefabSystem for the whole process, so later save loads must not add them again.
        private static World _world;
        private static bool _done;
        // Without a warning, a gate that never opens leaves no trace in the log: the clone-backed
        // fill styles just fall back to concrete.
        private static int _gateFrames;
        private static bool _gateWarned;
        private const int kGateWarnFrames = 3600; // ≈ 1 min at 60 fps

        public static void Register(World world)
        {
            _world = world;
            MainThreadDispatcher.RegisterUpdater(TryInitialize);
        }

        // Runs every frame on the main thread until it returns true. Same gate as ExtraLib's
        // MainSystem.Initialize, plus GameMode.Game: the launcher's "Continue" button skips the
        // main menu, and an in-game frame works just as well for registration.
        private static bool TryInitialize()
        {
            if (_done) return true;
            var gm = GameManager.instance;
            bool gateOpen = gm != null && gm.modManager.isInitialized
                && (gm.gameMode == GameMode.MainMenu || gm.gameMode == GameMode.Game)
                && gm.state != GameManager.State.Booting && gm.state != GameManager.State.Loading
                && _world != null && _world.IsCreated;
            if (!gateOpen)
            {
                if (++_gateFrames >= kGateWarnFrames && !_gateWarned)
                {
                    _gateWarned = true;
                    log.Warn($"[late-clone] gate not passed after {kGateWarnFrames} frames (gameMode={gm?.gameMode.ToString() ?? "<no GameManager>"}, state={gm?.state.ToString() ?? "-"}) — surface clones missing, clone-backed fill styles will fall back to concrete");
                }
                return false;
            }

            _done = true;
            try
            {
                CreateClones();
            }
            catch (Exception e)
            {
                log.Error($"[late-clone] creating surface clones failed: {e}");
            }
            return true;
        }

        private static void CreateClones()
        {
            var prefabSystem = _world.GetOrCreateSystemManaged<PrefabSystem>();
            log.Info($"[late-clone] creating vanilla surface clones (gameMode={GameManager.instance.gameMode})");

            foreach (var (sourceName, cloneName) in kSurfaceClones)
            {
                if (!TryGetSource(prefabSystem, sourceName, out var src, out var srcRa)) continue;

                var clone = MakeClone(src, srcRa, cloneName);
                clone.TryGet<RenderedArea>(out var ra);
                if (srcRa.m_Material != null)
                    ra.m_Material = new Material(srcRa.m_Material) { name = cloneName + " Material" };
                ra.m_DecalLayerMask = srcRa.m_DecalLayerMask | DecalLayers.Roads;
                SyncMaterialLayerMask(ra);
                prefabSystem.AddPrefab(clone);
                log.Info($"[late-clone] registered '{cloneName}' ← '{sourceName}' (vanilla material copy, prio={ra.m_RendererPriority}, layer={ra.m_DecalLayerMask})");
            }
        }

        private static bool TryGetSource(PrefabSystem prefabSystem, string sourceName, out SurfacePrefab src, out RenderedArea srcRa)
        {
            src = null;
            srcRa = null;
            if (!prefabSystem.TryGetPrefab(new PrefabID(nameof(SurfacePrefab), sourceName), out var srcBase)
                || srcBase is not SurfacePrefab found)
            {
                log.Warn($"[late-clone] vanilla '{sourceName}' not found — skipped");
                return false;
            }
            if (!found.TryGet<RenderedArea>(out var ra) || ra == null)
            {
                log.Warn($"[late-clone] '{sourceName}' has no RenderedArea — skipped");
                return false;
            }
            src = found;
            srcRa = ra;
            return true;
        }

        /// <summary>New SurfacePrefab with the source's serialized fields and its own RenderedArea.
        /// Only fields declared on these types are copied, so the PrefabBase/ComponentBase
        /// internals (component list, prefab back-references) are never shared with the
        /// vanilla original.</summary>
        private static SurfacePrefab MakeClone(SurfacePrefab src, RenderedArea srcRa, string name)
        {
            var clone = ScriptableObject.CreateInstance<SurfacePrefab>();
            clone.name = name;
            CopyDeclaredFields(src, clone, typeof(SurfacePrefab));
            CopyDeclaredFields(src, clone, typeof(AreaPrefab));

            var ra = clone.AddComponent<RenderedArea>();
            CopyDeclaredFields(srcRa, ra, typeof(RenderedArea));
            return clone;
        }

        private static void CopyDeclaredFields(object src, object dst, Type type)
        {
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                f.SetValue(dst, f.GetValue(src));
        }

        /// <summary>Keeps the material's decal-layer property equal to the component field.
        /// ExtraAssetsImporter always sets both, so this does too.</summary>
        private static void SyncMaterialLayerMask(RenderedArea ra)
        {
            if (ra.m_Material != null && ra.m_Material.HasProperty("colossal_DecalLayerMask"))
                ra.m_Material.SetFloat("colossal_DecalLayerMask", (float)(uint)ra.m_DecalLayerMask);
        }
    }
}
