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
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using ParkingLane = Game.Net.ParkingLane;
using CarLane = Game.Net.CarLane;

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// One-shot dump for the parking-line pairing in <c>CustomSecondaryLaneSystem</c>. For up to
    /// 20 road edges with parking lanes, logs every parking and car edge lane with its prefab
    /// name and flags, to show which lane prefabs a road type actually uses.
    ///
    /// Endpoints come from the lane curves: the LaneCorner data the job uses only exists inside
    /// UpdateLanesJob, and the curve ends match it up to a small offset.
    /// </summary>
    public partial class ParkingPairDumpSystem : GameSystemBase
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
                for (int i = 0; i < edges.Length && dumped < 20; i++)
                {
                    var e = edges[i];
                    if (!EntityManager.HasComponent<PrefabRef>(e)) continue;
                    var roadPe = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    string roadName = "<?>";
                    if (m_PrefabSystem.TryGetPrefab<PrefabBase>(roadPe, out var rp) && rp != null) roadName = rp.name;

                    var sub = EntityManager.GetBuffer<SubLane>(e, isReadOnly: true);

                    var parkings = new System.Collections.Generic.List<LaneInfo>();
                    var drives = new System.Collections.Generic.List<LaneInfo>();
                    for (int j = 0; j < sub.Length; j++)
                    {
                        var le = sub[j].m_SubLane;
                        if (!EntityManager.HasComponent<PrefabRef>(le)) continue;
                        var lp = EntityManager.GetComponentData<PrefabRef>(le).m_Prefab;
                        if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(lp, out var lpf) || lpf == null) continue;

                        // No filter by prefab name: some road types (asymmetric avenues, oneway
                        // variants) use lane prefabs that a name pattern would miss.
                        if (!EntityManager.HasComponent<EdgeLane>(le)) continue;
                        if (!EntityManager.HasComponent<Curve>(le)) continue;
                        bool isPark = EntityManager.HasComponent<ParkingLane>(le);
                        bool isDrive = EntityManager.HasComponent<CarLane>(le);
                        if (!isPark && !isDrive) continue;

                        var curve = EntityManager.GetComponentData<Curve>(le);
                        var info = new LaneInfo
                        {
                            entity = le,
                            prefabName = lpf.name,
                            startPos = curve.m_Bezier.a,
                            endPos = curve.m_Bezier.d,
                        };
                        // Approximates LaneCorner.m_Tangents from the first and last control segments.
                        info.startTan = math.normalizesafe((curve.m_Bezier.b - curve.m_Bezier.a).xz);
                        info.endTan = math.normalizesafe((curve.m_Bezier.d - curve.m_Bezier.c).xz);

                        if (isPark)
                        {
                            info.parkFlags = EntityManager.HasComponent<ParkingLane>(le)
                                ? EntityManager.GetComponentData<ParkingLane>(le).m_Flags.ToString()
                                : "NO_PARKINGLANE";
                            parkings.Add(info);
                        }
                        else
                        {
                            info.parkFlags = EntityManager.HasComponent<CarLane>(le)
                                ? EntityManager.GetComponentData<CarLane>(le).m_Flags.ToString()
                                : "NO_CARLANE";
                            drives.Add(info);
                        }
                    }

                    if (parkings.Count == 0) continue;

                    log.Info($"[pairdump] === edge#{e.Index} road='{roadName}' parkings={parkings.Count} drives={drives.Count} ===");
                    for (int p = 0; p < parkings.Count; p++)
                    {
                        var pk = parkings[p];
                        log.Info($"[pairdump] P[{p}] #{pk.entity.Index} prefab='{pk.prefabName}' flags={pk.parkFlags}");
                    }
                    for (int d = 0; d < drives.Count; d++)
                    {
                        var dr = drives[d];
                        log.Info($"[pairdump] D[{d}] #{dr.entity.Index} prefab='{dr.prefabName}' flags={dr.parkFlags}");
                    }
                    dumped++;
                }
                edges.Dispose();
                log.Info($"[pairdump] dumped {dumped} edges total");
            }
            catch (Exception ex) { log.Error(ex, "ParkingPairDumpSystem failed"); }
        }

        private struct LaneInfo
        {
            public Entity entity;
            public string prefabName;
            public float3 startPos;
            public float3 endPos;
            public float2 startTan;
            public float2 endTan;
            public string parkFlags;
        }
    }
}
