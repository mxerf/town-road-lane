using System;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane
{
    /// <summary>
    /// Handles the "Toggle all markings" button in <see cref="Setting"/>: enumerates every road
    /// edge + intersection node and either adds or removes the <see cref="MarkingOverride"/>
    /// component (<c>hideAll=true</c>), then marks each entity as <c>Updated</c> so
    /// <see cref="CustomSecondaryLaneSystem"/> reprocesses it on the next frame and the existing
    /// markings get cleared (or regenerated).
    ///
    /// Toggle policy: looks at the FIRST eligible edge — if it has the override, this is a "show"
    /// pass and we strip the component from everything; if it doesn't, this is a "hide" pass and
    /// we add the component to everything. Simple, predictable, no internal state to drift.
    ///
    /// Road Builder edges are skipped (same name-pattern filter v1's MarkingReapplySystem used):
    /// re-baking the RB-generated SubLane buffer through SecondaryLaneSystem can destabilise the
    /// engine on saves with monstrous RB roads (highway-based with angled parking). We can revisit
    /// once we know phase 1 is stable on vanilla.
    /// </summary>
    public partial class MarkingToggleSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_NodeQuery;
        private bool m_Requested;

        /// <summary>Called from the settings button. The actual work runs on the next system update.</summary>
        public static void RequestToggle()
        {
            var sys = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MarkingToggleSystem>();
            if (sys == null) { log.Warn("MarkingToggleSystem not found — cannot toggle"); return; }
            sys.m_Requested = true;
            sys.Enabled = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Road edges: have EdgeGeometry + Edge + a PrefabRef. Exclude in-edit / deleted.
            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            // Intersection nodes: have Node + a SubLane buffer (so secondary lanes can attach).
            m_NodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<SubLane>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Enabled = false; // idle until the button asks
        }

        protected override void OnUpdate()
        {
            if (!m_Requested) { Enabled = false; return; }
            m_Requested = false;
            Enabled = false;

            try
            {
                var edges = m_EdgeQuery.ToEntityArray(Allocator.Temp);
                var nodes = m_NodeQuery.ToEntityArray(Allocator.Temp);

                // Decide direction by peeking at the first eligible (non-RB) edge.
                bool hideAfter = true;
                for (int i = 0; i < edges.Length; i++)
                {
                    if (LooksLikeRoadBuilderRoad(edges[i])) continue;
                    hideAfter = !EntityManager.HasComponent<MarkingOverride>(edges[i]);
                    break;
                }
                log.Info($"toggle: direction = {(hideAfter ? "HIDE" : "SHOW")}; {edges.Length} edge(s) + {nodes.Length} node(s) to process");

                int touched = 0, skippedRb = 0;
                Apply(edges, hideAfter, ref touched, ref skippedRb);
                Apply(nodes, hideAfter, ref touched, ref skippedRb);
                log.Info($"toggle done: {touched} entities updated, {skippedRb} RB-edge(s) skipped");

                edges.Dispose();
                nodes.Dispose();
            }
            catch (Exception e) { log.Error(e, "MarkingToggleSystem failed"); }
        }

        private void Apply(NativeArray<Entity> entities, bool hide, ref int touched, ref int skippedRb)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (LooksLikeRoadBuilderRoad(e)) { skippedRb++; continue; }

                if (hide)
                {
                    if (!EntityManager.HasComponent<MarkingOverride>(e))
                        EntityManager.AddComponentData(e, new MarkingOverride { hide = MarkingCategory.All });
                    else
                        EntityManager.SetComponentData(e, new MarkingOverride { hide = MarkingCategory.All });
                }
                else if (EntityManager.HasComponent<MarkingOverride>(e))
                {
                    EntityManager.RemoveComponent<MarkingOverride>(e);
                }

                if (!EntityManager.HasComponent<Updated>(e))
                    EntityManager.AddComponent<Updated>(e);
                touched++;
            }
        }

        /// <summary>Road Builder names its generated road prefabs like "r&lt;guid&gt;-&lt;steamid&gt;".</summary>
        private bool LooksLikeRoadBuilderRoad(Entity e)
        {
            if (!EntityManager.HasComponent<PrefabRef>(e)) return false;
            var pe = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(pe, out var p) || p == null) return false;
            var name = p.name;
            if (string.IsNullOrEmpty(name) || name.Length < 20) return false;
            if (name[0] != 'r' && name[0] != 'R') return false;
            int dashes = 0;
            foreach (var c in name) if (c == '-') dashes++;
            return dashes >= 4;
        }
    }
}
