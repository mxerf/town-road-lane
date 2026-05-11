using System;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Makes the "Lane Markings" upgrade applicable to every <see cref="RoadPrefab"/> the game ever loads — both
    /// vanilla and runtime-generated (Road Builder etc.). The upgrade tool considers an upgrade applicable to a road
    /// only if the road's <see cref="NetData.m_GeneralFlagMask"/> has at least one bit in common with the upgrade's
    /// <c>PlaceableNetData.m_SetUpgradeFlags</c>. We use the spare bit <see cref="MarkingFlags.MarkingsOff"/>, so we
    /// need to OR it into every road's mask. <see cref="MarkingUpgradePrefabSystem"/> does this once during initial
    /// load, but RB / mods generate road prefabs LATER — those would never get the bit and the tool wouldn't
    /// highlight them. This system fills that gap: in <see cref="SystemUpdatePhase.PrefabUpdate"/>, on every road
    /// prefab freshly tagged <see cref="Created"/>, OR our bit into its mask (idempotent).
    ///
    /// (RB-generated roads use cloned lane prefabs of their own, so <see cref="MarkingLaneSubstituteSystem"/>'s
    /// original→clone map mostly won't match their lanes and substitution is a no-op there — safe by default. The
    /// few RB roads that DO reuse the vanilla <c>Car Drive Lane 3</c> get the substitution and the suppression
    /// works.)
    /// </summary>
    public partial class MarkingFlagMaskExpanderSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery m_FreshRoadPrefabs;
        private bool m_InitialPassDone;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_FreshRoadPrefabs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<RoadData>(),
                    ComponentType.ReadWrite<NetData>(),
                    ComponentType.ReadOnly<Created>(),
                },
            });
            RequireForUpdate(m_FreshRoadPrefabs);
        }

        protected override void OnUpdate()
        {
            if (Mod.Settings != null && !Mod.Settings.SegmentToggleEnabled) return; // feature off → don't widen anyone

            // Initial pass only: OR our bit into every road prefab present at the first load wave (vanilla + any RB
            // roads already loaded from save). DO NOT widen the mask of road prefabs that appear later, e.g. ones RB
            // generates during play — doing so was observed to crash SecondaryLaneSystem playback on the next net
            // update (the road's pipeline doesn't like having an unknown high composition bit appear out of nowhere
            // mid-session). This means: RB roads loaded with the save get the upgrade tool; RB roads created in play
            // do not, until next reload. Acceptable trade-off until we understand the crash.
            if (m_InitialPassDone) { Enabled = false; return; }

            try
            {
                var ents = m_FreshRoadPrefabs.ToEntityArray(Allocator.Temp);
                int patched = 0;
                for (int i = 0; i < ents.Length; i++)
                {
                    var nd = EntityManager.GetComponentData<NetData>(ents[i]);
                    if ((nd.m_GeneralFlagMask & MarkingFlags.MarkingsOff) != 0) continue;
                    nd.m_GeneralFlagMask |= MarkingFlags.MarkingsOff;
                    EntityManager.SetComponentData(ents[i], nd);
                    patched++;
                }
                ents.Dispose();
                if (patched > 0)
                    log.Info($"MarkingFlagMaskExpanderSystem: OR-ed MarkingsOff into {patched} road prefab(s) on initial pass");
                m_InitialPassDone = true;
                Enabled = false;
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingFlagMaskExpanderSystem failed");
                m_InitialPassDone = true;
                Enabled = false;
            }
        }
    }
}
