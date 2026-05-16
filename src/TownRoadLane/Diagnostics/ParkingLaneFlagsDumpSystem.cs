using System;
using System.Text;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using SubLane = Game.Net.SubLane;
using ParkingLane = Game.Net.ParkingLane;

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// Sibling of <see cref="CarLaneFlagsDumpSystem"/>: dumps the Parking Lane 2 sublanes on each
    /// road edge once after load, with EdgeLane vs non-EdgeLane separation, the ParkingLane
    /// component's flags (incl. VirtualLane), and the road prefab name. Wire in when the parking
    /// inject misbehaves; otherwise leave deregistered.
    /// </summary>
    public partial class ParkingLaneFlagsDumpSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_EdgeQuery;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<SubLane>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            RequireForUpdate(m_EdgeQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) return;
            m_Done = true;
            Enabled = false;

            try
            {
                var edges = m_EdgeQuery.ToEntityArray(Allocator.Temp);
                int dumped = 0;
                for (int i = 0; i < edges.Length && dumped < 10; i++)
                {
                    var e = edges[i];
                    if (!EntityManager.HasComponent<PrefabRef>(e)) continue;
                    var roadPe = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    string roadName = "<?>";
                    if (m_PrefabSystem.TryGetPrefab<PrefabBase>(roadPe, out var rp) && rp != null) roadName = rp.name;

                    var sub = EntityManager.GetBuffer<SubLane>(e, isReadOnly: true);
                    var edgeP = new StringBuilder();
                    var nodeP = new StringBuilder();
                    int edgeCount = 0, nodeCount = 0;

                    for (int j = 0; j < sub.Length; j++)
                    {
                        var le = sub[j].m_SubLane;
                        if (!EntityManager.HasComponent<PrefabRef>(le)) continue;
                        var lp = EntityManager.GetComponentData<PrefabRef>(le).m_Prefab;
                        if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(lp, out var lpf) || lpf == null) continue;
                        if (lpf.name != "Parking Lane 2") continue;
                        bool isEdge = EntityManager.HasComponent<EdgeLane>(le);
                        bool hasPark = EntityManager.HasComponent<ParkingLane>(le);
                        string flagStr = hasPark ? EntityManager.GetComponentData<ParkingLane>(le).m_Flags.ToString() : "NO_PARKINGLANE";
                        var sbTarget = isEdge ? edgeP : nodeP;
                        sbTarget.Append($" [#{le.Index} parkflags={flagStr}]");
                        if (isEdge) edgeCount++; else nodeCount++;
                    }

                    if (edgeCount == 0 && nodeCount == 0) continue;
                    log.Info($"[parkdump] edge#{e.Index} road='{roadName}'; EDGE Parking Lane 2 ({edgeCount}):{edgeP}; NODE ({nodeCount}):{nodeP}");
                    dumped++;
                }
                edges.Dispose();
                log.Info($"[parkdump] dumped {dumped} Parking Lane 2-bearing edges");
            }
            catch (Exception ex) { log.Error(ex, "ParkingLaneFlagsDumpSystem failed"); }
        }
    }
}
