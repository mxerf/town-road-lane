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
    /// Vanilla only marks perpendicular and angled bays ('Parking Cross Line', 'Car Bay Line').
    /// Parallel parking uses the 'Parking Lane 2' lane prefab, which no vanilla marking references.
    /// Two prefabs per region theme are cloned from the closest vanilla marking, so material, LODs,
    /// submeshes and archetype come along, and then get the mesh chosen in the settings (vanilla or
    /// a G87 Road Markings decal):
    ///
    ///  * "Parallel Parking Line", cloned from 'Car Bay Line' and hosted on 'Parking Lane 2' on the
    ///    parking side: a line along the whole zone.
    ///  * "Parallel Parking End", cloned from 'Parking Cross Line', with 'Parking Lane 2' in
    ///    m_CrossingLanes. That lane has SlotInterval 0 (a single slot), so the crossing path draws
    ///    exactly one tick at the block start and one at the block end; RequireContinue=false keeps
    ///    both ends.
    ///
    /// Vanilla prefabs are never edited. CustomSecondaryLaneSystem places and cuts the lines.
    /// </summary>
    public partial class ParkingLineCloneSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        // Carriageway-side host lanes for the longitudinal line (from the 'Car Bay Line' left list).
        private static readonly string[] kCarriagewayLaneNames =
        {
            "Car Drive Lane 3", "Car Drive Lane 3 - Tram", "Public Transport Lane 3", "Public Transport Lane 3 - Tram",
        };

        // 'Parking Lane 2' is present in a road section exactly when it has a parallel parking zone.
        // 'Boarding Lane 0' is always present, even with a wide sidewalk and no parking, so hosting on
        // it would draw the line everywhere and cover the curb edge line.
        private const string kParkingLaneName = "Parking Lane 2";

        // Used when the chosen mesh cannot be resolved (e.g. a G87 option without G87 installed).
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

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_LanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            RequireForUpdate(m_LanePrefabQuery);
        }

        // Runs once per save load, with no mid-session re-run on purpose (see EdgeLineCloneSystem).

        protected override void OnUpdate()
        {
            if (m_Done) return;
            m_Done = true;
            Enabled = false;
            // Runs even when ParkingMarkingsEnabled is off: saved games reference the spawned
            // sublanes' prefabs by name, and missing clones mean "Unknown prefab ID" errors and
            // stale entities. The setting only controls hosting, which ApplyOrUpdate handles.
            try { ApplyOrUpdate(); }
            catch (Exception e) { log.Error(e, "ParkingLineCloneSystem failed"); }
        }

        /// <summary>
        /// Creates the parking-marking prefabs, or refreshes their mesh and hosting to match the current
        /// settings. Idempotent.
        /// </summary>
        public void ApplyOrUpdate()
        {
            string lineMeshName = Mod.Settings?.ParkingLineMeshName() ?? kFallbackLineMesh;
            string endMeshName  = Mod.Settings != null ? Mod.Settings.ParkingEndMeshName() : kFallbackEndMesh;
            bool parkingOn = Mod.Settings == null || Mod.Settings.ParkingMarkingsEnabled;
            bool wantEnds = parkingOn && endMeshName != null;

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
                // Created unconditionally; the settings only decide whether hosting is attached.
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
                    sec.m_LeftLanes = parkingOn ? MakeInfos(carriageway) : Array.Empty<SecondaryLaneInfo>();
                    sec.m_RightLanes = parkingOn ? MakeInfos(new[] { parkingLane }) : Array.Empty<SecondaryLaneInfo>();
                    sec.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
                    sec.m_FitToParkingSpaces = false;
                    sec.m_CanFlipSides = true;
                    mesh = lineMesh;
                }
                else // Role.End
                {
                    sec.m_LeftLanes = Array.Empty<SecondaryLaneInfo>();
                    sec.m_RightLanes = Array.Empty<SecondaryLaneInfo>();
                    sec.m_CrossingLanes = wantEnds ? MakeCrossInfos(new[] { parkingLane }) : Array.Empty<SecondaryLaneInfo2>();
                    sec.m_FitToParkingSpaces = true;
                    sec.m_CanFlipSides = true;
                    sec.m_LengthOffset = new Unity.Mathematics.float2(-0.1f, 0f);
                    sec.m_PositionOffset = new Unity.Mathematics.float3(0.1f, 0f, 0f);
                    mesh = endMesh ?? lineMesh;
                }

                int swapped = SwapMesh(cloneBase, mesh);
                m_PrefabSystem.UpdatePrefab(cloneBase);
                touched++;
                log.Debug($"applied '{cloneName}' ({role}): mesh='{(mesh != null ? mesh.name : "<source>")}' swapped={swapped}");
            }

            log.Info($"ParkingLineCloneSystem: applied {touched} prefab(s) (enabled={parkingOn}, line='{lineMeshName}', end='{endMeshName ?? "(none)"}')");
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
