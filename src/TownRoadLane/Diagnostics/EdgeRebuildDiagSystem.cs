using System;
using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Edge = Game.Net.Edge;
using EdgeGeometry = Game.Net.EdgeGeometry;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// Diagnostic only: each frame that road edges are being (re)built (tagged <c>Updated</c>/<c>Created</c>), logs
    /// a one-line summary of the distinct road prefabs involved — and on the FIRST such frame after load, logs the
    /// full per-edge list (prefab name + edge index). Runs in <see cref="SystemUpdatePhase.Modification4"/>, just
    /// before <see cref="Game.Net.LaneSystem"/> / <see cref="Game.Net.SecondaryLaneSystem"/>, so the last thing
    /// printed before a crash in those systems' command-buffer playback points at the road that broke it.
    /// </summary>
    public partial class EdgeRebuildDiagSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_EdgeQuery;
        private bool m_DumpedFullOnce;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            RequireForUpdate(m_EdgeQuery);
        }

        protected override void OnUpdate()
        {
            try
            {
                var edges = m_EdgeQuery.ToEntityArray(Allocator.Temp);
                if (edges.Length == 0) { edges.Dispose(); return; }

                var roadCounts = new Dictionary<string, int>();
                for (int i = 0; i < edges.Length; i++)
                {
                    var name = "<?>";
                    if (EntityManager.HasComponent<PrefabRef>(edges[i]))
                    {
                        var pe = EntityManager.GetComponentData<PrefabRef>(edges[i]).m_Prefab;
                        if (m_PrefabSystem.TryGetPrefab<PrefabBase>(pe, out var p) && p != null) name = p.name;
                    }
                    roadCounts.TryGetValue(name, out var c); roadCounts[name] = c + 1;
                }

                var sb = new System.Text.StringBuilder($"[diag-rebuild] {edges.Length} road edge(s) rebuilding; roads:");
                foreach (var kv in roadCounts) sb.Append(' ').Append(kv.Key).Append('×').Append(kv.Value).Append(';');
                log.Info(sb.ToString());

                if (!m_DumpedFullOnce)
                {
                    m_DumpedFullOnce = true;
                    for (int i = 0; i < edges.Length; i++)
                    {
                        var name = "<?>";
                        if (EntityManager.HasComponent<PrefabRef>(edges[i]))
                        {
                            var pe = EntityManager.GetComponentData<PrefabRef>(edges[i]).m_Prefab;
                            if (m_PrefabSystem.TryGetPrefab<PrefabBase>(pe, out var p) && p != null) name = p.name;
                        }
                        log.Info($"[diag-rebuild]   edge#{edges[i].Index} road='{name}'");

                        // For non-vanilla-looking roads (Road Builder generated: name like "r<guid>-<steamid>"),
                        // dump the lane prefabs in the edge's SubLane buffer — tells us whether it uses the vanilla
                        // 'Car Drive Lane 3' (which our edge line now hosts) or RB's own cloned lane prefabs.
                        bool looksRb = name.Length > 20 && (name[0] == 'r' || name[0] == 'R') && name.IndexOf('-') > 0 && CountChar(name, '-') >= 4;
                        if (looksRb && EntityManager.HasBuffer<SubLane>(edges[i]))
                        {
                            var sub = EntityManager.GetBuffer<SubLane>(edges[i], isReadOnly: true);
                            var lsb = new System.Text.StringBuilder($"[diag-rebuild]     edge#{edges[i].Index} sub-lanes ({sub.Length}):");
                            for (int j = 0; j < sub.Length; j++)
                            {
                                var le = sub[j].m_SubLane;
                                var ln = "<?>";
                                if (EntityManager.HasComponent<PrefabRef>(le))
                                {
                                    var lp = EntityManager.GetComponentData<PrefabRef>(le).m_Prefab;
                                    if (m_PrefabSystem.TryGetPrefab<PrefabBase>(lp, out var lpf) && lpf != null) ln = lpf.name;
                                }
                                lsb.Append(' ').Append(ln).Append(';');
                            }
                            log.Info(lsb.ToString());
                        }
                    }
                }
                edges.Dispose();
            }
            catch (Exception e) { log.Warn($"[diag-rebuild] failed: {e.Message}"); }
        }

        private static int CountChar(string s, char c) { int n = 0; foreach (var ch in s) if (ch == c) n++; return n; }
    }
}
