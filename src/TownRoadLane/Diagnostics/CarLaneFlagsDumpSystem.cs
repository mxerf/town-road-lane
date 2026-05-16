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
using CarLane = Game.Net.CarLane;

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// One-shot dump: walks every road edge once after load and, for each Car Drive Lane 3 found in
    /// its SubLane buffer, logs:
    ///   - lane entity index
    ///   - whether it carries Game.Net.EdgeLane (vs being a node-routing sublane)
    ///   - CarLane.m_Flags
    ///   - our "isOneWay" derivation (sawForward AND sawBackward on EDGE lanes only)
    ///
    /// The inject in CustomSecondaryLaneSystem depends on this layout exactly; dumping it locally
    /// shortcuts a lot of guessing.
    /// </summary>
    public partial class CarLaneFlagsDumpSystem : GameSystemBase
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
                for (int i = 0; i < edges.Length && dumped < 8; i++)
                {
                    var e = edges[i];
                    if (!EntityManager.HasComponent<PrefabRef>(e)) continue;
                    var roadPe = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    string roadName = "<?>";
                    if (m_PrefabSystem.TryGetPrefab<PrefabBase>(roadPe, out var rp) && rp != null) roadName = rp.name;

                    var sub = EntityManager.GetBuffer<SubLane>(e, isReadOnly: true);
                    var edgeLanes = new StringBuilder();
                    var nodeLanes = new StringBuilder();
                    int edgeCount = 0, nodeCount = 0;
                    bool sawForward = false, sawBackward = false;

                    for (int j = 0; j < sub.Length; j++)
                    {
                        var le = sub[j].m_SubLane;
                        if (!EntityManager.HasComponent<PrefabRef>(le)) continue;
                        var lp = EntityManager.GetComponentData<PrefabRef>(le).m_Prefab;
                        if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(lp, out var lpf) || lpf == null) continue;
                        if (lpf.name != "Car Drive Lane 3") continue;
                        bool isEdge = EntityManager.HasComponent<EdgeLane>(le);
                        bool hasCar = EntityManager.HasComponent<CarLane>(le);
                        string flagStr = hasCar ? EntityManager.GetComponentData<CarLane>(le).m_Flags.ToString() : "NO_CARLANE";
                        var sbTarget = isEdge ? edgeLanes : nodeLanes;
                        sbTarget.Append($" [#{le.Index} flags={flagStr}]");
                        if (isEdge)
                        {
                            edgeCount++;
                            if (hasCar)
                            {
                                var fl = EntityManager.GetComponentData<CarLane>(le).m_Flags;
                                if ((fl & CarLaneFlags.Invert) != 0) sawBackward = true;
                                else sawForward = true;
                            }
                        }
                        else nodeCount++;
                    }

                    if (edgeCount == 0 && nodeCount == 0) continue;
                    bool isOneWay = !(sawForward && sawBackward);
                    log.Info($"[flagdump] edge#{e.Index} road='{roadName}' isOneWay={isOneWay} (sawFwd={sawForward}, sawBack={sawBackward}); EDGE lanes ({edgeCount}):{edgeLanes}; NODE lanes ({nodeCount}):{nodeLanes}");
                    dumped++;
                }
                edges.Dispose();
                log.Info($"[flagdump] dumped {dumped} Car Drive Lane 3-bearing edges");
            }
            catch (Exception ex) { log.Error(ex, "CarLaneFlagsDumpSystem failed"); }
        }
    }
}
