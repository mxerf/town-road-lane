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
    /// Phase 4 step 2 diagnostic: every 60 frames, count how many sublane entities reference
    /// our EdgeLineClone prefab and log per-entity details for the first few. Answers the
    /// "I committed pairs but see no markings" question by separating these failure modes:
    ///   - count == 0 → EmitUserPairs never created entities (prefab Entity stale, gate path skipped, etc.).
    ///   - count > 0, no Owner/Curve → archetype is wrong.
    ///   - count > 0, has SecondaryLane + Owner + Curve → entities exist but vanilla rendering
    ///     still ignores them. Means the missing piece is downstream (Deleted being added,
    ///     missing CullingInfo, wrong archetype slot, etc.).
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
            // Sublane entities have PrefabRef and Game.Net.SecondaryLane component if they're marking lanes.
            _allSubLanesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Net.SecondaryLane>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnUpdate()
        {
            if ((++_ticks % 60) != 0) return;

            // Resolve our clone prefab entities by name.
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

            // Walk every secondary-lane sublane in the world, count those whose PrefabRef points at our clone.
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
