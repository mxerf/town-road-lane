using System;
using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Creates the per-segment "Lane Markings" road upgrade that appears in the road toolbar's upgrade row
    /// (alongside Lighting / Trees / Grass / Sound Barrier / …). Painting it onto a road segment sets our
    /// <see cref="MarkingFlags.MarkingsOff"/> bit on that edge; <see cref="MarkingSuppressSystem"/> then removes
    /// the city-road markings on it. (Yes, the toolbar entry is "remove markings" — courtyard/alley roads where
    /// the markings hurt visual consistency.)
    ///
    /// How it's built: we clone the vanilla <c>'RoadZones'</c> upgrade — it is a <see cref="FencePrefab"/> whose
    /// only purpose is to flip a single composition bit (it carries no visible geometry), which is exactly the
    /// shape we want. We then:
    ///   • blank its <c>NetUpgrade.m_SetState</c>/<c>m_UnsetState</c> (so it no longer toggles zoning),
    ///   • give it our own name / icon / toolbar priority,
    ///   • after <c>NetInitializeSystem</c> has baked its <c>PlaceableNetData</c>, OR our spare bit into
    ///     <c>PlaceableNetData.m_SetUpgradeFlags.m_General</c> — the normal <c>m_SetState</c> path can't express
    ///     bit 0x80000000, so we inject it directly,
    ///   • OR the same bit into every <see cref="RoadPrefab"/>'s <c>NetData.m_GeneralFlagMask</c> so the upgrade
    ///     tool considers the upgrade applicable to roads (it checks <c>targetRoad.m_GeneralFlagMask &amp; upgradeFlags</c>).
    ///
    /// Runs in <see cref="SystemUpdatePhase.PrefabUpdate"/>. It's a tiny state machine: one update creates+registers
    /// the clone, a later update (once init has run and <c>PlaceableNetData</c> exists) finalizes the flag patches.
    /// </summary>
    public partial class MarkingUpgradePrefabSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private const string kTemplateName = "RoadZones";          // vanilla FencePrefab, pure flag-toggle upgrade
        private const string kCloneName    = "TownRoadLane Lane Markings";
        private const string kIcon         = "Media/Game/Icons/Crosswalk.svg"; // placeholder until we ship our own
        private const int    kPriority     = 75;                   // sits between Trees (80) and Grass (70)

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_NetPrefabQuery; // anything with PrefabData + NetData (roads, fences, …) — used to find the template and to patch road masks
        private EntityQuery m_RoadPrefabQuery;

        private PrefabBase m_Clone;
        private bool m_CloneAdded;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_NetPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetData>());
            m_RoadPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<RoadData>());
            RequireForUpdate(m_NetPrefabQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) { Enabled = false; return; }

            if (Mod.Settings != null && !Mod.Settings.SegmentToggleEnabled)
            {
                log.Info("MarkingUpgradePrefabSystem: per-segment Lane Markings toggle disabled in settings — skipping");
                m_Done = true; Enabled = false; return;
            }

            try
            {
                if (!m_CloneAdded) { TryCreateClone(); return; }   // step 1: create + register; let init run next
                TryFinalize();                                      // step 2: once baked, inject the flags
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingUpgradePrefabSystem failed");
                m_Done = true; Enabled = false;
            }
        }

        private void TryCreateClone()
        {
            // Find the vanilla 'RoadZones' FencePrefab.
            FencePrefab template = null;
            var entities = m_NetPrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (m_PrefabSystem.TryGetPrefab<FencePrefab>(entities[i], out var f) && f != null && f.name == kTemplateName)
                { template = f; break; }
            }
            entities.Dispose();

            if (template == null) return; // prefabs not loaded yet — try again next update

            // Already added in a previous session-of-this-system run? (paranoia — m_CloneAdded covers it normally)
            m_Clone = m_PrefabSystem.DuplicatePrefab(template, kCloneName);
            m_CloneAdded = true;

            // Make it a pure marker upgrade: no zoning toggle, paint-only, not underground.
            if (m_Clone.TryGet<NetUpgrade>(out var upg))
            {
                upg.m_SetState = Array.Empty<NetPieceRequirements>();
                upg.m_UnsetState = Array.Empty<NetPieceRequirements>();
                upg.m_Standalone = false;
                upg.m_Underground = false;
            }
            else log.Warn($"clone '{kCloneName}' unexpectedly has no NetUpgrade component");

            // Our own toolbar identity (keep the group it inherited: RoadsServices).
            if (m_Clone.TryGet<UIObject>(out var ui))
            {
                ui.m_Icon = kIcon;
                ui.m_Priority = kPriority;
                ui.m_IsDebugObject = false;
            }

            // Drop any unlock requirement so it's always available.
            m_Clone.Remove<Unlockable>();
            m_Clone.Remove<UnlockOnBuild>();

            log.Info($"MarkingUpgradePrefabSystem: cloned '{kTemplateName}' → '{kCloneName}', awaiting prefab init");
        }

        private void TryFinalize()
        {
            if (m_Clone == null) { m_Done = true; Enabled = false; return; }
            if (!m_PrefabSystem.TryGetEntity(m_Clone, out var cloneEntity)) return;
            if (!EntityManager.HasComponent<PlaceableNetData>(cloneEntity)) return; // not baked yet

            // 1. Inject our spare bit into the upgrade's set-flags so the tool actually writes it onto the edge.
            var pnd = EntityManager.GetComponentData<PlaceableNetData>(cloneEntity);
            pnd.m_SetUpgradeFlags.m_General |= MarkingFlags.MarkingsOff;
            // Ensure it's recognised as an upgrade and paint-only (NetInitializeSystem should already have set these
            // because the source carried a NetUpgrade, but be explicit).
            pnd.m_PlacementFlags |= Game.Net.PlacementFlags.IsUpgrade | Game.Net.PlacementFlags.UpgradeOnly;
            EntityManager.SetComponentData(cloneEntity, pnd);

            // 2. Make the upgrade applicable to roads: widen every RoadPrefab's general flag mask by our bit.
            int patchedRoads = 0;
            var roads = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < roads.Length; i++)
            {
                var e = roads[i];
                if (!EntityManager.HasComponent<NetData>(e)) continue;
                var nd = EntityManager.GetComponentData<NetData>(e);
                if ((nd.m_GeneralFlagMask & MarkingFlags.MarkingsOff) != 0) continue;
                nd.m_GeneralFlagMask |= MarkingFlags.MarkingsOff;
                EntityManager.SetComponentData(e, nd);
                patchedRoads++;
            }
            roads.Dispose();

            m_Done = true;
            Enabled = false;
            log.Info($"MarkingUpgradePrefabSystem: finalized — injected MarkingsOff bit into '{kCloneName}' PlaceableNetData and {patchedRoads} road prefab flag mask(s)");
        }
    }
}
