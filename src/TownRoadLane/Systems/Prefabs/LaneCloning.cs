using System.Collections.Generic;
using Colossal.Logging;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Prefab lookups and clone steps shared by <see cref="EdgeLineCloneSystem"/> and
    /// <see cref="ParkingLineCloneSystem"/>: find vanilla lane and mesh prefabs by name, duplicate
    /// a SecondaryLane marking, and swap its mesh.
    /// </summary>
    internal static class LaneCloning
    {
        private static readonly ILog log = Mod.log;

        /// <summary>Lane prefabs whose names are in <paramref name="names"/>, keyed by name, from
        /// one pass over <paramref name="lanePrefabQuery"/>. The first prefab with a name wins.</summary>
        public static Dictionary<string, NetLanePrefab> FindLanes(PrefabSystem prefabSystem, EntityQuery lanePrefabQuery, HashSet<string> names)
        {
            var result = new Dictionary<string, NetLanePrefab>();
            var ents = lanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!prefabSystem.TryGetPrefab<NetLanePrefab>(ents[i], out var lane) || lane == null) continue;
                if (names.Contains(lane.name) && !result.ContainsKey(lane.name)) result[lane.name] = lane;
            }
            ents.Dispose();
            return result;
        }

        /// <summary>Render prefabs whose names are in <paramref name="names"/> (null or empty
        /// names are ignored), keyed by name, from one pass over <paramref name="meshPrefabQuery"/>.
        /// The first prefab with a name wins.</summary>
        public static Dictionary<string, RenderPrefab> ResolveMeshes(PrefabSystem prefabSystem, EntityQuery meshPrefabQuery, IEnumerable<string> names)
        {
            var wanted = new HashSet<string>();
            foreach (var n in names) if (!string.IsNullOrEmpty(n)) wanted.Add(n);
            var result = new Dictionary<string, RenderPrefab>();
            var ents = meshPrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                if (prefabSystem.TryGetPrefab<RenderPrefab>(ents[i], out var rp) && rp != null && wanted.Contains(rp.name) && !result.ContainsKey(rp.name))
                    result[rp.name] = rp;
            ents.Dispose();
            return result;
        }

        /// <summary>The <paramref name="names"/> found in <paramref name="byName"/>, in order;
        /// missing ones are logged and skipped.</summary>
        public static List<NetLanePrefab> ResolveLanes(Dictionary<string, NetLanePrefab> byName, string[] names, string what)
        {
            var list = new List<NetLanePrefab>();
            foreach (var n in names)
                if (byName.TryGetValue(n, out var p) && p != null) list.Add(p);
                else log.Warn($"{what} '{n}' not found — skipping it");
            return list;
        }

        /// <summary>The wanted mesh, else the fallback, else null (the clone keeps its source
        /// prefab's mesh).</summary>
        public static RenderPrefab PickMesh(Dictionary<string, RenderPrefab> byName, string wanted, string fallback, string what)
        {
            if (!string.IsNullOrEmpty(wanted) && byName.TryGetValue(wanted, out var rp) && rp != null) return rp;
            if (byName.TryGetValue(fallback, out var fb) && fb != null)
            { log.Warn($"{what} mesh '{wanted}' not found (G87 not installed?) — falling back to '{fallback}'"); return fb; }
            log.Warn($"{what} mesh '{wanted}' and fallback '{fallback}' both missing — keeping source mesh");
            return null;
        }

        /// <summary>
        /// The existing clone named <paramref name="cloneName"/>, or a new duplicate of
        /// <paramref name="sourceName"/> (added to <paramref name="laneByName"/>), with its
        /// SecondaryLane. The source must be a NetLaneGeometryPrefab with SecondaryLane. Logs and
        /// returns false when neither is usable.
        /// </summary>
        public static bool TryGetOrDuplicate(
            PrefabSystem prefabSystem, Dictionary<string, NetLanePrefab> laneByName, string sourceName, string cloneName,
            out NetLanePrefab clone, out SecondaryLane secondaryLane)
        {
            secondaryLane = null;
            if (!laneByName.TryGetValue(cloneName, out clone) || clone == null)
            {
                if (!laneByName.TryGetValue(sourceName, out var src) || !(src is NetLaneGeometryPrefab) || !src.TryGet<SecondaryLane>(out _))
                { log.Warn($"source '{sourceName}' missing/invalid — can't create '{cloneName}'"); return false; }
                clone = prefabSystem.DuplicatePrefab(src, cloneName) as NetLanePrefab;
                laneByName[cloneName] = clone;
            }
            if (clone == null || !clone.TryGet(out secondaryLane)) { log.Warn($"'{cloneName}' has no SecondaryLane — skipping"); return false; }
            return true;
        }

        /// <summary>Puts <paramref name="mesh"/> into every mesh slot of the prefab; returns how
        /// many slots changed. A null mesh leaves the prefab untouched.</summary>
        public static int SwapMesh(NetLanePrefab prefab, RenderPrefab mesh)
        {
            if (mesh == null || !(prefab is NetLaneGeometryPrefab g) || g.m_Meshes == null) return 0;
            int n = 0;
            for (int m = 0; m < g.m_Meshes.Length; m++)
                if (g.m_Meshes[m].m_Mesh != null) { g.m_Meshes[m].m_Mesh = mesh; n++; }
            return n;
        }
    }
}
