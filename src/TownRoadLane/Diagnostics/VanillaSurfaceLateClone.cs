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

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// EXPERIMENT (vanilla-surface revival, 2026-07-19): register SurfacePrefab clones of a
    /// vanilla decorative surface on a LIVE frame — the way ExtraAssetsImporter registers every
    /// G87 surface (MainThreadDispatcher updater fires once the game is out of Booting/Loading,
    /// then PrefabSystem.AddPrefab on the main thread). Our 2026-07-16 attempts created clones
    /// during save loading, where the one-frame Created window of the area batch system
    /// (query {All: RenderedAreaData, Any: Created|Deleted}) elapses before rendering ticks —
    /// every fill fell back to the grey 'Missing Area' prefab. See cs2-vanilla-surface-dead-end
    /// project memory for the full autopsy and the EAI code walk that produced this recipe.
    ///
    /// Confirmed working in-game 2026-07-19. Every clone is a faithful copy: the vanilla
    /// material duplicated as-is, vanilla renderer priority, decal mask |= Roads. (A second
    /// material variant — the AreasConfigurationPrefab template with transplanted textures —
    /// was verified to render identically and retired for being the more convoluted path;
    /// see cs2-vanilla-surface-dead-end memory.) Style catalogue slots 15+ point at these
    /// names — reachable via the U-cycle and the area style dropdowns.
    /// </summary>
    public static class VanillaSurfaceLateClone
    {
        private static readonly ILog log = Mod.log;

        public const string kCloneGrass = "TRL Grass Surface";
        public const string kCloneGrassDark = "TRL Grass Dark Surface";
        public const string kCloneSand = "TRL Sand Surface";
        public const string kClonePavement = "TRL Pavement Surface";
        public const string kCloneTiles1 = "TRL Tiles 1 Surface";
        public const string kCloneTiles2 = "TRL Tiles 2 Surface";
        public const string kCloneTiles3 = "TRL Tiles 3 Surface";

        private const string kSourceGrass = "Grass Surface 01";

        // source vanilla SurfacePrefab name → our clone name (variant A).
        // Vanilla inventory (29 prefabs) verified by the AreasPrototypeSystem dump 2026-07-19.
        private static readonly string[,] kVariantAClones =
        {
            { kSourceGrass,         kCloneGrass },
            { "Grass Surface 02",   kCloneGrassDark },
            { "Sand Surface 01",    kCloneSand },
            { "Pavement Surface 01", kClonePavement },
            { "Tiles Surface 01",   kCloneTiles1 },
            { "Tiles Surface 02",   kCloneTiles2 },
            { "Tiles Surface 03",   kCloneTiles3 },
        };

        private static World _world;
        private static bool _done;

        public static void Register(World world)
        {
            _world = world;
            MainThreadDispatcher.RegisterUpdater(TryInitialize);
        }

        // Runs every frame on the main thread until it returns true. Mirrors ExtraLib's
        // MainSystem.Initialize gate, plus GameMode.Game so the launcher's "Continue" path
        // (which skips the main menu — the known way to break EAI/G87 imports) still gets the
        // clones: an in-game frame is equally inside the live render loop.
        private static bool TryInitialize()
        {
            if (_done) return true;
            var gm = GameManager.instance;
            if (gm == null || !gm.modManager.isInitialized) return false;
            if (gm.gameMode != GameMode.MainMenu && gm.gameMode != GameMode.Game) return false;
            if (gm.state == GameManager.State.Booting || gm.state == GameManager.State.Loading) return false;
            if (_world == null || !_world.IsCreated) return false;

            _done = true;
            try
            {
                CreateClones();
            }
            catch (Exception e)
            {
                log.Error($"[late-clone] experiment failed: {e}");
            }
            return true;
        }

        private static void CreateClones()
        {
            var prefabSystem = _world.GetOrCreateSystemManaged<PrefabSystem>();
            log.Info($"[late-clone] creating vanilla surface clones (gameMode={GameManager.instance.gameMode})");

            // ---- Variant A set: faithful clones, own copy of each vanilla material ----
            for (int i = 0; i < kVariantAClones.GetLength(0); i++)
            {
                string sourceName = kVariantAClones[i, 0];
                string cloneName = kVariantAClones[i, 1];
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

        /// <summary>Fresh SurfacePrefab with the source's own serialized fields (m_Color etc.)
        /// and an independent RenderedArea whose fields are copied one-to-one. DeclaredOnly
        /// everywhere so PrefabBase/ComponentBase plumbing (components list, prefab backrefs)
        /// is never shared with the vanilla original.</summary>
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

        /// <summary>Keep the material's own decal-layer float in step with the component field —
        /// EAI's importers treat the two as one value; unclear which one the batch system trusts.</summary>
        private static void SyncMaterialLayerMask(RenderedArea ra)
        {
            if (ra.m_Material != null && ra.m_Material.HasProperty("colossal_DecalLayerMask"))
                ra.m_Material.SetFloat("colossal_DecalLayerMask", (float)(uint)ra.m_DecalLayerMask);
        }
    }
}
