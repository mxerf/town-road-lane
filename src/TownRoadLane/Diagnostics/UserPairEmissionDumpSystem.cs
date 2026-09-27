using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// Every 60 frames, counts the secondary-lane entities that use the EU/NA edge-line clone
    /// prefabs and logs the components of the first few. Tells apart the cases where committed
    /// markings do not show up:
    ///   - count is 0: no lane entities were emitted;
    ///   - Owner or Curve missing: the archetype is wrong;
    ///   - everything present: the entities exist and the problem is further down the
    ///     rendering path (Deleted added, culling data missing).
    /// </summary>
    public partial class UserPairEmissionDumpSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem _prefabSystem;
        private EntityQuery _allSubLanesQuery;
        private int _ticks;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _allSubLanesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Net.SecondaryLane>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnUpdate()
        {
            if ((++_ticks % 60) != 0) return;

            Entity euClone = Entity.Null, naClone = Entity.Null;
            var lanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            var ents = lanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<NetLanePrefab>(ents[i], out var p) || p == null) continue;
                if (p.name == "TownRoadLane EU City Edge Line") euClone = ents[i];
                else if (p.name == "TownRoadLane NA City Edge Line") naClone = ents[i];
            }
            ents.Dispose();

            var all = _allSubLanesQuery.ToEntityArray(Allocator.Temp);
            int eu = 0, na = 0, dumpedSample = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var pr = EntityManager.GetComponentData<PrefabRef>(all[i]).m_Prefab;
                if (pr == euClone) { eu++; if (dumpedSample < 3) DumpSample(all[i], "EU", ref dumpedSample); }
                else if (pr == naClone) { na++; if (dumpedSample < 3) DumpSample(all[i], "NA", ref dumpedSample); }
            }
            all.Dispose();

            log.Info($"[emission-dump] tick={_ticks} euCloneEntity=#{euClone.Index} naCloneEntity=#{naClone.Index} → EU-sublanes={eu}, NA-sublanes={na}");
        }

        private void DumpSample(Entity sub, string region, ref int dumpedSample)
        {
            bool hasOwner = EntityManager.HasComponent<Owner>(sub);
            bool hasCurve = EntityManager.HasComponent<Curve>(sub);
            bool hasLane = EntityManager.HasComponent<Lane>(sub);
            bool hasSecondary = EntityManager.HasComponent<Game.Net.SecondaryLane>(sub);
            bool hasDeleted = EntityManager.HasComponent<Deleted>(sub);
            bool hasUpdated = EntityManager.HasComponent<Updated>(sub);
            Entity owner = hasOwner ? EntityManager.GetComponentData<Owner>(sub).m_Owner : Entity.Null;
            log.Info($"[emission-dump]   sample {region} #{sub.Index}: Owner=#{owner.Index} hasCurve={hasCurve} hasLane={hasLane} hasSecondary={hasSecondary} hasDeleted={hasDeleted} hasUpdated={hasUpdated}");
            dumpedSample++;
        }
    }
}
