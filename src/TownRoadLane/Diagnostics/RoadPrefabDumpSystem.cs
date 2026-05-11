using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane.Diagnostics
{
    /// <summary>
    /// One-shot diagnostic system: walks every loaded road prefab and dumps its
    /// section / piece / lane structure to the log, so we can see exactly which
    /// lane prefabs and mesh prefabs are used for edge markings on highways and
    /// which are missing on city roads. Read-only; disables itself after one run.
    /// </summary>
    public partial class RoadPrefabDumpSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_RoadPrefabQuery;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Road prefab entities carry PrefabData + RoadData.
            m_RoadPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<RoadData>());
            RequireForUpdate(m_RoadPrefabQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) return;
            m_Done = true;
            Enabled = false;

            try
            {
                Dump();
            }
            catch (Exception e)
            {
                log.Error(e, "RoadPrefabDumpSystem failed");
            }
        }

        private void Dump()
        {
            var entities = m_RoadPrefabQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            log.Info($"=== RoadPrefabDumpSystem: {entities.Length} road prefabs ===");

            var sb = new StringBuilder();
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<RoadPrefab>(entities[i], out var road) || road == null)
                    continue;

                sb.Clear();
                sb.AppendLine();
                sb.Append("ROAD '").Append(road.name).Append("'  roadType=").Append(road.m_RoadType)
                  .Append(" highwayRules=").Append(road.m_HighwayRules)
                  .Append(" speed=").Append(road.m_SpeedLimit);
                sb.AppendLine();
                DumpComponents(sb, road, "  ");

                var sections = road.m_Sections ?? Array.Empty<NetSectionInfo>();
                sb.Append("  sections: ").Append(sections.Length).AppendLine();
                for (int s = 0; s < sections.Length; s++)
                {
                    DumpSection(sb, sections[s], "    ", new HashSet<NetSectionPrefab>());
                }

                log.Info(sb.ToString());
            }

            entities.Dispose();

            DumpLaneGeometryPrefabs();
            DumpParkingLanes();
            DumpNetUpgrades();
            log.Info("=== RoadPrefabDumpSystem: done ===");
        }

        /// <summary>
        /// Dumps every ParkingLane prefab: its slot size/angle/road-type, which roads use it
        /// (reverse-indexed from road sections), and which marking prefabs host it
        /// (search every NetLaneGeometryPrefab's SecondaryLane left/right/crossing arrays).
        /// This is the data needed to add longitudinal markings to parallel street parking.
        /// </summary>
        private void DumpParkingLanes()
        {
            // 1. Collect all NetLanePrefab entities, split into parking lanes and marking prefabs.
            var laneQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            var laneEntities = laneQuery.ToEntityArray(Unity.Collections.Allocator.Temp);

            var parkingLanes = new List<NetLanePrefab>();
            var markingPrefabs = new List<NetLaneGeometryPrefab>(); // those with a SecondaryLane component
            for (int i = 0; i < laneEntities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(laneEntities[i], out var lane) || lane == null) continue;
                if (lane.TryGet<ParkingLane>(out _)) parkingLanes.Add(lane);
                if (lane is NetLaneGeometryPrefab geom && geom.TryGet<SecondaryLane>(out _)) markingPrefabs.Add(geom);
            }
            laneEntities.Dispose();

            // 2. Build reverse index: parking lane name -> set of road names that reference it.
            var usedInRoads = new Dictionary<string, SortedSet<string>>();
            var roadEntities = m_RoadPrefabQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < roadEntities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<RoadPrefab>(roadEntities[i], out var road) || road == null) continue;
                var found = new HashSet<string>();
                CollectParkingLaneNames(road, found, new HashSet<NetSectionPrefab>());
                foreach (var pn in found)
                {
                    if (!usedInRoads.TryGetValue(pn, out var set)) usedInRoads[pn] = set = new SortedSet<string>();
                    set.Add(road.name);
                }
            }
            roadEntities.Dispose();

            // 3. Emit.
            log.Info($"=== ParkingLane prefabs: {parkingLanes.Count} ===");
            var sb = new StringBuilder();
            foreach (var pk in parkingLanes)
            {
                sb.Clear();
                sb.Append("PARKINGLANE '").Append(pk.name).Append("'  ");
                AppendLaneKind(sb, pk);
                sb.AppendLine();
                if (usedInRoads.TryGetValue(pk.name, out var roads) && roads.Count > 0)
                {
                    sb.Append("  used in roads: ");
                    bool first = true;
                    foreach (var r in roads) { if (!first) sb.Append("; "); sb.Append(r); first = false; }
                    sb.AppendLine();
                }
                else
                {
                    sb.Append("  used in roads: (none directly referenced by a RoadPrefab section)").AppendLine();
                }
                // Which marking prefabs host this parking lane?
                foreach (var mk in markingPrefabs)
                {
                    if (!mk.TryGet<SecondaryLane>(out var sec)) continue;
                    int l = CountHits(sec.m_LeftLanes, pk), r = CountHits(sec.m_RightLanes, pk), c = CountHits2(sec.m_CrossingLanes, pk);
                    if (l + r + c == 0) continue;
                    sb.Append("  hosted by marking '").Append(mk.name).Append("'  meshes=");
                    if (mk.m_Meshes != null)
                        for (int m = 0; m < mk.m_Meshes.Length; m++) { if (m > 0) sb.Append(','); var mm = mk.m_Meshes[m].m_Mesh; sb.Append(mm != null ? mm.name : "<null>"); }
                    sb.Append("  [LEFT×").Append(l).Append(" RIGHT×").Append(r).Append(" CROSSING×").Append(c).Append("]")
                      .Append(" fitToParking=").Append(sec.m_FitToParkingSpaces)
                      .Append(" canFlip=").Append(sec.m_CanFlipSides);
                    sb.AppendLine();
                }
                log.Info(sb.ToString());
            }
        }

        /// <summary>
        /// Dumps every NetUpgrade prefab (paint-only road decoration upgrades like grass / trees / lighting /
        /// quay / sound barrier): its concrete prefab class, full ComponentBase list, the ECS component types on
        /// the prefab entity (= the archetype), its PlaceableNetData (set/unset upgrade flags, placement flags),
        /// its UIObject (group / priority / icon), and NetData flag masks. This is the template data we need to
        /// build our own "Lane Markings" toolbar upgrade by cloning a suitable vanilla one.
        /// </summary>
        private void DumpNetUpgrades()
        {
            // Net upgrade prefabs are NetPrefab/RoadPrefab instances carrying a NetUpgrade component; once
            // NetInitializeSystem has run they have PlaceableNetData. Scan everything with PlaceableNetData.
            var q = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<PlaceableNetData>());
            var entities = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            log.Info($"=== NetUpgrade prefabs: scanning {entities.Length} prefab entities with PlaceableNetData ===");
            var sb = new StringBuilder();
            int found = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(entities[i], out var prefab) || prefab == null) continue;
                if (!prefab.TryGet<NetUpgrade>(out var upg)) continue;
                found++;

                sb.Clear();
                sb.AppendLine();
                sb.Append("NETUPGRADE '").Append(prefab.name).Append("'  class=").Append(prefab.GetType().Name)
                  .Append("  standalone=").Append(upg.m_Standalone)
                  .Append(" underground=").Append(upg.m_Underground)
                  .Append("  setState=[").Append(Join(upg.m_SetState)).Append("]")
                  .Append(" unsetState=[").Append(Join(upg.m_UnsetState)).Append("]");
                sb.AppendLine();

                // ComponentBase list on the prefab.
                DumpComponents(sb, prefab, "  ");

                // ECS component types on the prefab entity (= the archetype the prefab was given).
                sb.Append("  archetype: ");
                using (var types = EntityManager.GetComponentTypes(entities[i], Unity.Collections.Allocator.Temp))
                {
                    for (int t = 0; t < types.Length; t++) { if (t > 0) sb.Append(", "); sb.Append(types[t].GetManagedType()?.Name ?? types[t].ToString()); }
                }
                sb.AppendLine();

                // PlaceableNetData.
                var pnd = EntityManager.GetComponentData<PlaceableNetData>(entities[i]);
                sb.Append("  PlaceableNetData: placementFlags=").Append(pnd.m_PlacementFlags)
                  .Append("  setUpgradeFlags={G=").Append(pnd.m_SetUpgradeFlags.m_General)
                  .Append(", L=").Append(pnd.m_SetUpgradeFlags.m_Left)
                  .Append(", R=").Append(pnd.m_SetUpgradeFlags.m_Right).Append("}")
                  .Append("  unsetUpgradeFlags={G=").Append(pnd.m_UnsetUpgradeFlags.m_General)
                  .Append(", L=").Append(pnd.m_UnsetUpgradeFlags.m_Left)
                  .Append(", R=").Append(pnd.m_UnsetUpgradeFlags.m_Right).Append("}");
                sb.AppendLine();

                // NetData flag masks (which composition bits this prefab "owns").
                if (EntityManager.HasComponent<NetData>(entities[i]))
                {
                    var nd = EntityManager.GetComponentData<NetData>(entities[i]);
                    sb.Append("  NetData: requiredLayers=").Append(nd.m_RequiredLayers)
                      .Append("  generalFlagMask=").Append(nd.m_GeneralFlagMask)
                      .Append("  sideFlagMask=").Append(nd.m_SideFlagMask);
                    sb.AppendLine();
                }

                // UIObject (toolbar group / icon / priority).
                if (prefab.TryGet<UIObject>(out var ui))
                {
                    sb.Append("  UIObject: group=").Append(ui.m_Group != null ? ui.m_Group.name : "<null>")
                      .Append(" (").Append(ui.m_Group != null ? ui.m_Group.GetType().Name : "?").Append(")")
                      .Append("  priority=").Append(ui.m_Priority)
                      .Append("  icon=").Append(ui.m_Icon ?? "<null>")
                      .Append("  isDebug=").Append(ui.m_IsDebugObject);
                    sb.AppendLine();
                }
                else
                {
                    sb.Append("  UIObject: <none>").AppendLine();
                }

                AppendSource(sb, prefab);
                log.Info(sb.ToString());
            }
            entities.Dispose();
            log.Info($"=== NetUpgrade prefabs: {found} found ===");

            // Also list all UI groups/categories that exist, so we can pick where to put our upgrade.
            DumpUIGroups();
        }

        private void DumpUIGroups()
        {
            // Scan all prefabs for UIGroupPrefab subclasses (categories / menus).
            var allPrefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            var ents = allPrefabs.ToEntityArray(Unity.Collections.Allocator.Temp);
            log.Info("=== UI groups / categories (for placing the upgrade toolbar entry) ===");
            var sb = new StringBuilder();
            for (int i = 0; i < ents.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var p) || p == null) continue;
                if (p is not UIGroupPrefab) continue;
                sb.Clear();
                sb.Append("UIGROUP '").Append(p.name).Append("'  class=").Append(p.GetType().Name);
                if (p is UIAssetCategoryPrefab cat)
                    sb.Append("  menu=").Append(cat.m_Menu != null ? cat.m_Menu.name : "<null>");
                log.Info(sb.ToString());
            }
            ents.Dispose();
        }

        private void CollectParkingLaneNames(PrefabBase netGeom, HashSet<string> outNames, HashSet<NetSectionPrefab> visited)
        {
            if (netGeom is not NetGeometryPrefab geom) return;
            var sections = geom.m_Sections ?? Array.Empty<NetSectionInfo>();
            for (int s = 0; s < sections.Length; s++)
                CollectFromSection(sections[s]?.m_Section, outNames, visited);
        }

        private void CollectFromSection(NetSectionPrefab sec, HashSet<string> outNames, HashSet<NetSectionPrefab> visited)
        {
            if (sec == null || !visited.Add(sec)) return;
            if (sec.m_SubSections != null)
                foreach (var sub in sec.m_SubSections) CollectFromSection(sub?.m_Section, outNames, visited);
            if (sec.m_Pieces != null)
                foreach (var pi in sec.m_Pieces)
                {
                    var piece = pi?.m_Piece;
                    if (piece != null && piece.TryGet<NetPieceLanes>(out var pl) && pl.m_Lanes != null)
                        foreach (var li in pl.m_Lanes)
                            if (li.m_Lane != null && li.m_Lane.TryGet<ParkingLane>(out _))
                                outNames.Add(li.m_Lane.name);
                }
        }

        private static int CountHits(SecondaryLaneInfo[] arr, NetLanePrefab lane)
        {
            if (arr == null) return 0;
            int n = 0;
            for (int i = 0; i < arr.Length; i++) if (arr[i] != null && arr[i].m_Lane == lane) n++;
            return n;
        }

        private static int CountHits2(SecondaryLaneInfo2[] arr, NetLanePrefab lane)
        {
            if (arr == null) return 0;
            int n = 0;
            for (int i = 0; i < arr.Length; i++) if (arr[i] != null && arr[i].m_Lane == lane) n++;
            return n;
        }

        private void DumpSection(StringBuilder sb, NetSectionInfo info, string indent, HashSet<NetSectionPrefab> visited)
        {
            var sec = info?.m_Section;
            sb.Append(indent).Append("section '").Append(sec != null ? sec.name : "<null>").Append("'")
              .Append("  reqAll=[").Append(Join(info?.m_RequireAll)).Append("]")
              .Append(" reqAny=[").Append(Join(info?.m_RequireAny)).Append("]")
              .Append(" reqNone=[").Append(Join(info?.m_RequireNone)).Append("]")
              .Append(info != null && info.m_Invert ? " INVERT" : "")
              .Append(info != null && info.m_Flip ? " FLIP" : "")
              .Append(info != null && info.m_Median ? " MEDIAN" : "")
              .Append(info != null && info.m_HalfLength ? " HALFLEN" : "")
              .Append("  offset=").Append(info != null ? info.m_Offset.ToString() : "-");
            sb.AppendLine();

            if (sec == null) return;
            if (!visited.Add(sec))
            {
                sb.Append(indent).Append("  (already visited)").AppendLine();
                return;
            }

            var subs = sec.m_SubSections;
            if (subs != null)
            {
                for (int i = 0; i < subs.Length; i++)
                {
                    var sub = subs[i];
                    // NetSubSectionInfo: m_Section + requirement arrays (mirror of NetSectionInfo-ish).
                    var nested = new NetSectionInfo
                    {
                        m_Section = sub.m_Section,
                        m_RequireAll = sub.m_RequireAll,
                        m_RequireAny = sub.m_RequireAny,
                        m_RequireNone = sub.m_RequireNone,
                    };
                    DumpSection(sb, nested, indent + "  ", visited);
                }
            }

            var pieces = sec.m_Pieces;
            if (pieces != null)
            {
                for (int i = 0; i < pieces.Length; i++)
                {
                    DumpPiece(sb, pieces[i], indent + "  ");
                }
            }
        }

        private void DumpPiece(StringBuilder sb, NetPieceInfo info, string indent)
        {
            var piece = info?.m_Piece;
            sb.Append(indent).Append("piece '").Append(piece != null ? piece.name : "<null>").Append("'")
              .Append("  reqAll=[").Append(Join(info?.m_RequireAll)).Append("]")
              .Append(" reqAny=[").Append(Join(info?.m_RequireAny)).Append("]")
              .Append(" reqNone=[").Append(Join(info?.m_RequireNone)).Append("]")
              .Append("  offset=").Append(info != null ? info.m_Offset.ToString() : "-");
            if (piece != null)
            {
                sb.Append("  layer=").Append(piece.m_Layer)
                  .Append(" width=").Append(piece.m_Width)
                  .Append(" widthOffset=").Append(piece.m_WidthOffset);
            }
            sb.AppendLine();

            if (piece == null) return;

            if (piece.TryGet<NetPieceLanes>(out var pieceLanes) && pieceLanes.m_Lanes != null)
            {
                for (int i = 0; i < pieceLanes.m_Lanes.Length; i++)
                {
                    var li = pieceLanes.m_Lanes[i];
                    sb.Append(indent).Append("  laneInfo lane='").Append(li.m_Lane != null ? li.m_Lane.name : "<null>").Append("'")
                      .Append("  pos=").Append(li.m_Position).Append(" findAnchor=").Append(li.m_FindAnchor);
                    AppendLaneKind(sb, li.m_Lane);
                    sb.AppendLine();
                    DumpLanePrefabAux(sb, li.m_Lane, indent + "    ");
                }
            }

            // Curb/sidewalk pieces sometimes carry other relevant components — list them.
            DumpComponents(sb, piece, indent + "  ");
        }

        private void DumpLanePrefabAux(StringBuilder sb, NetLanePrefab lane, string indent)
        {
            if (lane == null) return;
            if (lane.TryGet<AuxiliaryLanes>(out var aux) && aux.m_AuxiliaryLanes != null)
            {
                for (int i = 0; i < aux.m_AuxiliaryLanes.Length; i++)
                {
                    var a = aux.m_AuxiliaryLanes[i];
                    sb.Append(indent).Append("AUX lane='").Append(a.m_Lane != null ? a.m_Lane.name : "<null>").Append("'")
                      .Append("  pos=").Append(a.m_Position)
                      .Append(" spacing=").Append(a.m_Spacing)
                      .Append(" evenSpacing=").Append(a.m_EvenSpacing)
                      .Append(" findAnchor=").Append(a.m_FindAnchor)
                      .Append("  reqAll=[").Append(Join(a.m_RequireAll)).Append("]")
                      .Append(" reqAny=[").Append(Join(a.m_RequireAny)).Append("]")
                      .Append(" reqNone=[").Append(Join(a.m_RequireNone)).Append("]");
                    AppendLaneKind(sb, a.m_Lane);
                    sb.AppendLine();
                }
            }
            DumpSecondaryLane(sb, lane, indent);
        }

        private void DumpSecondaryLane(StringBuilder sb, NetLanePrefab lane, string indent)
        {
            if (lane == null) return;
            if (!lane.TryGet<SecondaryLane>(out var sec)) return;
            sb.Append(indent).Append("SECONDARYLANE on '").Append(lane.name).Append("'")
              .Append(" canFlip=").Append(sec.m_CanFlipSides)
              .Append(" dupSides=").Append(sec.m_DuplicateSides)
              .Append(" reqParallel=").Append(sec.m_RequireParallel)
              .Append(" reqOpposite=").Append(sec.m_RequireOpposite)
              .Append(" fitToParking=").Append(sec.m_FitToParkingSpaces)
              .Append(" evenSpacing=").Append(sec.m_EvenSpacing)
              .Append(" posOffset=").Append(sec.m_PositionOffset)
              .Append(" lenOffset=").Append(sec.m_LengthOffset)
              .Append(" cutMargin=").Append(sec.m_CutMargin)
              .Append(" cutOffset=").Append(sec.m_CutOffset)
              .Append(" cutOverlap=").Append(sec.m_CutOverlap)
              .Append(" spacing=").Append(sec.m_Spacing);
            sb.AppendLine();
            DumpSecInfoArray(sb, indent + "  ", "LEFT", sec.m_LeftLanes);
            DumpSecInfoArray(sb, indent + "  ", "RIGHT", sec.m_RightLanes);
            DumpSecInfo2Array(sb, indent + "  ", "CROSSING", sec.m_CrossingLanes);
        }

        private void DumpSecInfoArray(StringBuilder sb, string indent, string side, SecondaryLaneInfo[] arr)
        {
            if (arr == null || arr.Length == 0) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i];
                sb.Append(indent).Append(side).Append(" lane='").Append(s.m_Lane != null ? s.m_Lane.name : "<null>").Append("'  req:");
                if (s.m_RequireSafe) sb.Append(" Safe");
                if (s.m_RequireUnsafe) sb.Append(" Unsafe");
                if (s.m_RequireSingle) sb.Append(" Single");
                if (s.m_RequireMultiple) sb.Append(" Multiple");
                if (s.m_RequireAllowPassing) sb.Append(" AllowPassing");
                if (s.m_RequireForbidPassing) sb.Append(" ForbidPassing");
                if (s.m_RequireMerge) sb.Append(" Merge");
                if (s.m_RequireContinue) sb.Append(" Continue");
                if (s.m_RequireSafeMaster) sb.Append(" SafeMaster");
                if (s.m_RequireRoundabout) sb.Append(" Roundabout");
                if (s.m_RequireNotRoundabout) sb.Append(" NotRoundabout");
                AppendLaneKind(sb, s.m_Lane);
                sb.AppendLine();
            }
        }

        private void DumpSecInfo2Array(StringBuilder sb, string indent, string side, SecondaryLaneInfo2[] arr)
        {
            if (arr == null || arr.Length == 0) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var s = arr[i];
                sb.Append(indent).Append(side).Append(" lane='").Append(s.m_Lane != null ? s.m_Lane.name : "<null>").Append("'  req:");
                if (s.m_RequireStop) sb.Append(" Stop");
                if (s.m_RequireYield) sb.Append(" Yield");
                if (s.m_RequirePavement) sb.Append(" Pavement");
                if (s.m_RequireContinue) sb.Append(" Continue");
                AppendLaneKind(sb, s.m_Lane);
                sb.AppendLine();
            }
        }

        private void AppendLaneKind(StringBuilder sb, NetLanePrefab lane)
        {
            if (lane == null) return;
            sb.Append("  {");
            if (lane is NetLaneGeometryPrefab geom)
            {
                sb.Append("GEOMETRY meshes=");
                if (geom.m_Meshes != null)
                {
                    for (int i = 0; i < geom.m_Meshes.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var m = geom.m_Meshes[i].m_Mesh;
                        sb.Append(m != null ? m.name : "<null>");
                    }
                }
                sb.Append("; ");
            }
            if (lane.TryGet<CarLane>(out var car)) sb.Append("CarLane(w=").Append(car.m_Width).Append(",rt=").Append(car.m_RoadType).Append(") ");
            if (lane.TryGet<PedestrianLane>(out _)) sb.Append("PedestrianLane ");
            if (lane.TryGet<ParkingLane>(out var pk)) sb.Append("ParkingLane(slot=").Append(pk.m_SlotSize).Append(",angle=").Append(pk.m_SlotAngle).Append(",rt=").Append(pk.m_RoadType).Append(",special=").Append(pk.m_SpecialVehicles).Append(") ");
            if (lane.TryGet<TrackLane>(out _)) sb.Append("TrackLane ");
            if (lane.TryGet<UtilityLane>(out _)) sb.Append("UtilityLane ");
            if (lane.TryGet<SecondaryLane>(out _)) sb.Append("HAS-SecondaryLane ");
            if (lane.TryGet<AuxiliaryLanes>(out _)) sb.Append("HAS-AuxiliaryLanes ");
            sb.Append('}');
        }

        private void DumpComponents(StringBuilder sb, PrefabBase prefab, string indent)
        {
            if (prefab?.components == null || prefab.components.Count == 0) return;
            sb.Append(indent).Append("components: ");
            for (int i = 0; i < prefab.components.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var c = prefab.components[i];
                sb.Append(c != null ? c.GetType().Name : "<null>");
            }
            sb.AppendLine();
        }

        private void DumpLaneGeometryPrefabs()
        {
            // Enumerate all NetLaneGeometryPrefab entities (likely the marking line prefabs).
            var q = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneGeometryData>());
            var entities = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            log.Info($"=== NetLaneGeometryPrefab list: {entities.Length} ===");
            var sb = new StringBuilder();
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLaneGeometryPrefab>(entities[i], out var p) || p == null) continue;
                sb.Clear();
                sb.Append("LANEGEOM '").Append(p.name).Append("'  meshes=");
                if (p.m_Meshes != null)
                {
                    for (int m = 0; m < p.m_Meshes.Length; m++)
                    {
                        if (m > 0) sb.Append(',');
                        var mesh = p.m_Meshes[m].m_Mesh;
                        sb.Append(mesh != null ? mesh.name : "<null>");
                    }
                }
                if (p.components != null)
                {
                    sb.Append("  comps=");
                    for (int c = 0; c < p.components.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(p.components[c]?.GetType().Name);
                    }
                }
                AppendSource(sb, p);
                log.Info(sb.ToString());
            }
            entities.Dispose();

            DumpRenderPrefabs();
            DumpLanesWithSecondary();
        }

        /// <summary>Lists all RenderPrefab (mesh) prefabs that look like line markings, with their source mod.</summary>
        private void DumpRenderPrefabs()
        {
            var q = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<MeshData>());
            var entities = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            log.Info($"=== RenderPrefab (mesh) list: {entities.Length} total; line/marking-looking ones: ===");
            var sb = new StringBuilder();
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<RenderPrefab>(entities[i], out var p) || p == null) continue;
                var n = p.name ?? "";
                bool looksLikeMarking = n.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Dash", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Marking", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Stripe", StringComparison.OrdinalIgnoreCase) >= 0;
                // also always print anything from a mod (non-vanilla source)
                bool fromMod = p.asset != null && p.isSubscribedMod;
                if (!looksLikeMarking && !fromMod) continue;
                sb.Clear();
                sb.Append("MESH '").Append(n).Append("'");
                AppendSource(sb, p);
                log.Info(sb.ToString());
            }
            entities.Dispose();
        }

        private static void AppendSource(StringBuilder sb, PrefabBase p)
        {
            sb.Append("  src=");
            if (p.asset == null) { sb.Append("<runtime>"); return; }
            try
            {
                var meta = p.asset.GetMeta();
                string platform = meta.platformID;
                bool sub = p.isSubscribedMod;
                if (sub || !string.IsNullOrEmpty(platform))
                    sb.Append("MOD(").Append(string.IsNullOrEmpty(platform) ? "?" : platform).Append(", packaged=").Append(meta.packaged).Append(")");
                else
                    sb.Append("vanilla");
            }
            catch { sb.Append("?"); }
        }

        private void DumpLanesWithSecondary()
        {
            // Enumerate ALL NetLanePrefab entities and dump their SecondaryLane config (if any).
            // This is the key data: which marking lines a drive-lane prefab attaches and under what conditions.
            var q = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            var entities = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            log.Info($"=== Lane prefabs with SecondaryLane: scanning {entities.Length} NetLanePrefab entities ===");
            var sb = new StringBuilder();
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(entities[i], out var lane) || lane == null) continue;
                if (!lane.TryGet<SecondaryLane>(out _)) continue;
                sb.Clear();
                sb.Append("--- ");
                AppendLaneKind(sb, lane);
                sb.AppendLine();
                DumpSecondaryLane(sb, lane, "");
                log.Info(sb.ToString());
            }
            entities.Dispose();
        }

        private static string Join<T>(T[] arr)
        {
            if (arr == null || arr.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i]);
            }
            return sb.ToString();
        }
    }
}
