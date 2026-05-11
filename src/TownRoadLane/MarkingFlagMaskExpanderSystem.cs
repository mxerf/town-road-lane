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
                    log.Info($"MarkingFlagMaskExpanderSystem: OR-ed MarkingsOff into {patched} new road prefab(s) (incl. runtime-generated)");
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingFlagMaskExpanderSystem failed");
            }
        }
    }
}
