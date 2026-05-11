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
    /// Marks parallel street-parking zones, which vanilla leaves as bare asphalt.
    ///
    /// Vanilla only marks <i>perpendicular/angled</i> bays ('EU/NA Parking Cross Line' + 'Car Bay Line').
    /// Parallel street parking uses the 'Parking Lane 2' lane prefab, which no vanilla marking references.
    /// We create two pairs of marking prefabs (one per region theme), each cloned from the closest vanilla
    /// marking so all the rendering details (material, LODs, SubMesh, archetype) come along for free, then
    /// swap in the mesh chosen in the mod settings (vanilla or, optionally, a G87 Road Markings decal):
    ///
    ///  * "Parallel Parking Line"  — cloned from 'Car Bay Line'; SecondaryLane hosts 'Parking Lane 2' on the
    ///    parking side ⇒ a line down the whole zone.
    ///  * "Parallel Parking End"   — cloned from 'Parking Cross Line' (fit-to-parking, crossing-based);
    ///    SecondaryLane.m_CrossingLanes hosts 'Parking Lane 2'. Since 'Parking Lane 2' has SlotInterval == 0
    ///    (slotCount == 1), the crossing path draws exactly one perpendicular tick at m=0 (block start) and one
    ///    at m=slotCount=1 (block end). RequireContinue=false ⇒ those ends are NOT skipped.
    ///
    /// <see cref="ApplyOrUpdate"/> is idempotent: it creates our prefabs on first call and just refreshes their
    /// mesh / SecondaryLane on later calls, so the "Reapply markings" button can re-run it after a style change.
    /// SecondaryLaneSystem handles position/cuts around the carriageway, sidewalk and intersections by itself.
    /// </summary>
    public partial class ParkingMarkingPatchSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Carriageway-side host lanes for the longitudinal line (kept from the original 'Car Bay Line' left list).
        private static readonly string[] kCarriagewayLaneNames =
        {
            "Car Drive Lane 3", "Car Drive Lane 3 - Tram", "Public Transport Lane 3", "Public Transport Lane 3 - Tram",
        };

        // The parallel-parking lane prefab. ONLY 'Parking Lane 2': it is present in a road section exactly when a
        // parallel parking zone is active. ('Boarding Lane 0' is always present — even with a wide sidewalk / no
        // parking — so hosting on it would draw the line everywhere and stomp the curb edge line.)
        private const string kParkingLaneName = "Parking Lane 2";

        // Fallback meshes if the chosen style can't be resolved (e.g. a "G87" option but G87 isn't installed).
        private const string kFallbackLineMesh = "White Dashed Line Mesh - Dense";
        private const string kFallbackEndMesh  = "White Solid Line Mesh";

        private enum Role { Longitudinal, End }
        private static readonly (string src, string clone, Role role)[] kRecipes =
        {
            ("EU Car Bay Line",       "TownRoadLane EU Parallel Parking Line", Role.Longitudinal),
            ("NA Car Bay Line",       "TownRoadLane NA Parallel Parking Line", Role.Longitudinal),
            ("EU Parking Cross Line", "TownRoadLane EU Parallel Parking End",  Role.End),
            ("NA Parking Cross Line", "TownRoadLane NA Parallel Parking End",  Role.End),
        };

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_LanePrefabQuery;
        private bool m_Done;

        /// <summary>Names of the marking prefabs this system creates/updates — for the reapply system to refresh roads.</summary>
        public static IEnumerable<string> CreatedPrefabNames { get { foreach (var r in kRecipes) yield return r.clone; } }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_LanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            RequireForUpdate(m_LanePrefabQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) return;
            m_Done = true;
            Enabled = false;
            if (Mod.Settings != null && !Mod.Settings.ParkingMarkingsEnabled)
            {
                log.Info("ParkingMarkingPatchSystem: disabled in settings — skipping");
                return;
            }
            try { ApplyOrUpdate(); }
            catch (Exception e) { log.Error(e, "ParkingMarkingPatchSystem failed"); }
        }

        /// <summary>
        /// Creates the parking-marking prefabs (first call) or refreshes their mesh/SecondaryLane to match the
        /// current settings (later calls). Safe to call from the reapply system.
        /// </summary>
        public void ApplyOrUpdate()
        {
            string lineMeshName = Mod.Settings?.ParkingLineMeshName() ?? kFallbackLineMesh;
            string endMeshName  = Mod.Settings != null ? Mod.Settings.ParkingEndMeshName() : kFallbackEndMesh;
            bool wantEnds = endMeshName != null;

            // Resolve every prefab we need by name in one pass over NetLanePrefab entities.
            var wantedLanes = new HashSet<string>(kCarriagewayLaneNames) { kParkingLaneName };
            foreach (var r in kRecipes) { wantedLanes.Add(r.src); wantedLanes.Add(r.clone); }
            var laneByName = new Dictionary<string, NetLanePrefab>();
            var laneEnts = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < laneEnts.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(laneEnts[i], out var lane) || lane == null) continue;
                if (wantedLanes.Contains(lane.name) && !laneByName.ContainsKey(lane.name)) laneByName[lane.name] = lane;
            }
            laneEnts.Dispose();

            var carriageway = ResolveList(laneByName, kCarriagewayLaneNames, "carriageway host lane");
            if (!laneByName.TryGetValue(kParkingLaneName, out var parkingLane) || parkingLane == null)
            { log.Warn($"parallel parking lane '{kParkingLaneName}' not found — aborting"); return; }
            if (carriageway.Count == 0) { log.Warn("no carriageway host lanes found — aborting"); return; }

            var meshByName = ResolveMeshes(new[] { lineMeshName, endMeshName, kFallbackLineMesh, kFallbackEndMesh });
            RenderPrefab lineMesh = PickMesh(meshByName, lineMeshName, kFallbackLineMesh, "longitudinal line");
            RenderPrefab endMesh  = wantEnds ? PickMesh(meshByName, endMeshName, kFallbackEndMesh, "end tick") : null;

            int touched = 0;
            foreach (var (srcName, cloneName, role) in kRecipes)
            {
                if (role == Role.End && !wantEnds) continue; // (we don't delete an already-created End prefab; it just won't be hosted — see below)

                // Get-or-clone our prefab.
                if (!laneByName.TryGetValue(cloneName, out var cloneBase) || cloneBase == null)
                {
                    if (!laneByName.TryGetValue(srcName, out var src) || !(src is NetLaneGeometryPrefab) || !src.TryGet<SecondaryLane>(out _))
                    { log.Warn($"source '{srcName}' missing/invalid — can't create '{cloneName}'"); continue; }
                    cloneBase = m_PrefabSystem.DuplicatePrefab(src, cloneName) as NetLanePrefab;
                    laneByName[cloneName] = cloneBase;
                }
                if (cloneBase == null || !cloneBase.TryGet<SecondaryLane>(out var sec)) { log.Warn($"'{cloneName}' has no SecondaryLane — skipping"); continue; }

                RenderPrefab mesh;
                if (role == Role.Longitudinal)
                {
                    sec.m_LeftLanes = MakeInfos(carriageway);
                    sec.m_RightLanes = MakeInfos(new[] { parkingLane });
                    sec.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
                    sec.m_FitToParkingSpaces = false;
                    sec.m_CanFlipSides = true;
                    mesh = lineMesh;
                }
                else // Role.End
                {
                    sec.m_LeftLanes = Array.Empty<SecondaryLaneInfo>();
                    sec.m_RightLanes = Array.Empty<SecondaryLaneInfo>();
                    // wantEnds is true here (we 'continue'd above otherwise). If a previous run created the End prefab
                    // and the user later picks "None", that prefab simply stops being hosted — see the 'continue' above
                    // means we don't reach here, so its m_CrossingLanes keeps the last non-None value but it's still
                    // referenced by no road... actually to be safe, when ends are wanted we (re)host; otherwise we never
                    // touch it. A leftover End prefab with stale hosting would still draw — so clear it when not wanted:
                    sec.m_CrossingLanes = MakeCrossInfos(new[] { parkingLane });
                    sec.m_FitToParkingSpaces = true;
                    sec.m_CanFlipSides = true;
                    sec.m_LengthOffset = new Unity.Mathematics.float2(-0.1f, 0f);
                    sec.m_PositionOffset = new Unity.Mathematics.float3(0.1f, 0f, 0f);
                    mesh = endMesh;
                }

                int swapped = SwapMesh(cloneBase, mesh);
                m_PrefabSystem.UpdatePrefab(cloneBase);
                touched++;
                log.Info($"applied '{cloneName}' ({role}): mesh='{(mesh != null ? mesh.name : "<source>")}' swapped={swapped}");
            }

            // If ends are NOT wanted but an End prefab was created earlier, strip its hosting so it stops drawing.
            if (!wantEnds)
                foreach (var (_, cloneName, role) in kRecipes)
                {
                    if (role != Role.End) continue;
                    if (laneByName.TryGetValue(cloneName, out var p) && p != null && p.TryGet<SecondaryLane>(out var sec2))
                    {
                        if (sec2.m_CrossingLanes != null && sec2.m_CrossingLanes.Length > 0)
                        {
                            sec2.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
                            m_PrefabSystem.UpdatePrefab(p);
                            log.Info($"cleared end-tick hosting on '{cloneName}' (style = None)");
                        }
                    }
                }

            log.Info($"ParkingMarkingPatchSystem: applied {touched} prefab(s) (line='{lineMeshName}', end='{endMeshName ?? "(none)"}')");
        }

        private static int SwapMesh(NetLanePrefab prefab, RenderPrefab mesh)
        {
            if (mesh == null || !(prefab is NetLaneGeometryPrefab g) || g.m_Meshes == null) return 0;
            int n = 0;
            for (int m = 0; m < g.m_Meshes.Length; m++)
                if (g.m_Meshes[m].m_Mesh != null) { g.m_Meshes[m].m_Mesh = mesh; n++; }
            return n;
        }

        private Dictionary<string, RenderPrefab> ResolveMeshes(IEnumerable<string> names)
        {
            var wanted = new HashSet<string>();
            foreach (var n in names) if (!string.IsNullOrEmpty(n)) wanted.Add(n);
            var result = new Dictionary<string, RenderPrefab>();
            var meshQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<MeshData>());
            var ents = meshQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                if (m_PrefabSystem.TryGetPrefab<RenderPrefab>(ents[i], out var rp) && rp != null && wanted.Contains(rp.name) && !result.ContainsKey(rp.name))
                    result[rp.name] = rp;
            ents.Dispose();
            return result;
        }

        private static RenderPrefab PickMesh(Dictionary<string, RenderPrefab> byName, string wanted, string fallback, string what)
        {
            if (!string.IsNullOrEmpty(wanted) && byName.TryGetValue(wanted, out var rp) && rp != null) return rp;
            if (byName.TryGetValue(fallback, out var fb) && fb != null)
            { log.Warn($"{what} mesh '{wanted}' not found (G87 not installed?) — falling back to '{fallback}'"); return fb; }
            log.Warn($"{what} mesh '{wanted}' and fallback '{fallback}' both missing — keeping source mesh");
            return null;
        }

        private List<NetLanePrefab> ResolveList(Dictionary<string, NetLanePrefab> byName, string[] names, string what)
        {
            var list = new List<NetLanePrefab>();
            foreach (var n in names)
                if (byName.TryGetValue(n, out var p) && p != null) list.Add(p);
                else log.Warn($"{what} '{n}' not found — skipping it");
            return list;
        }

        private static SecondaryLaneInfo[] MakeInfos(IReadOnlyList<NetLanePrefab> lanes)
        {
            var arr = new SecondaryLaneInfo[lanes.Count];
            for (int i = 0; i < lanes.Count; i++) arr[i] = new SecondaryLaneInfo { m_Lane = lanes[i], m_RequireSafe = true };
            return arr;
        }

        private static SecondaryLaneInfo2[] MakeCrossInfos(IReadOnlyList<NetLanePrefab> lanes)
        {
            var arr = new SecondaryLaneInfo2[lanes.Count];
            for (int i = 0; i < lanes.Count; i++) arr[i] = new SecondaryLaneInfo2 { m_Lane = lanes[i], m_RequireContinue = false };
            return arr;
        }
    }
}
