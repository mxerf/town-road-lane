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
    /// Creates the per-segment "Lane Markings" road upgrade that appears in the road toolbar's upgrade row
    /// (alongside Lighting / Trees / Grass / Sound Barrier / …). Painting it onto a road segment sets our
    /// <see cref="MarkingFlags.MarkingsOff"/> bit on that edge; <see cref="MarkingLaneSubstituteSystem"/> then makes
    /// that edge use no-marking lane prefabs (the clones this system creates) so the engine never draws this mod's
    /// markings there. (Yes, the toolbar entry is "remove markings" — courtyard/alley roads where the markings hurt
    /// visual consistency.)
    ///
    /// How it's built: we clone the vanilla <c>'RoadZones'</c> upgrade — it is a <see cref="FencePrefab"/> whose
    /// only purpose is to flip a single composition bit (it carries no visible geometry), which is exactly the
    /// shape we want. We then:
    ///   • blank its <c>NetUpgrade.m_SetState</c>/<c>m_UnsetState</c> (so it no longer toggles zoning),
    ///   • give it our own name / icon / toolbar priority,
    ///   • after <c>NetInitializeSystem</c> has baked its <c>PlaceableNetData</c>, OR our spare bit into
    ///     <c>PlaceableNetData.m_SetUpgradeFlags.m_General</c> — the normal <c>m_SetState</c> path can't express
    ///     bit 0x80000000, so we inject it directly,
    ///   • OR the same bit into every <see cref="RoadPrefab"/>'s <c>NetData.m_GeneralFlagMask</c> so the upgrade
    ///     tool considers the upgrade applicable to roads (it checks <c>targetRoad.m_GeneralFlagMask &amp; upgradeFlags</c>).
    ///
    /// It also creates "no-marking" clones of the city drive-lane prefabs (<c>Car Drive Lane 3</c> / <c>- Tram</c> /
    /// <c>Public Transport Lane 3</c> / <c>- Tram</c>): a clone is byte-identical except nothing references it, so its
    /// baked <c>SecondaryNetLane</c> buffer stays empty and <see cref="Game.Net.SecondaryLaneSystem"/> draws nothing
    /// next to it. <see cref="MarkingLaneSubstituteSystem"/> swaps them into the lane list of any composition that
    /// carries the MarkingsOff bit.
    ///
    /// Runs in <see cref="SystemUpdatePhase.PrefabUpdate"/>. It's a tiny state machine: one update creates+registers
    /// the clones, a later update (once init has run and <c>PlaceableNetData</c> exists) finalizes the flag patches.
    /// </summary>
    public partial class MarkingUpgradePrefabSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private const string kTemplateName = "RoadZones";          // vanilla FencePrefab, pure flag-toggle upgrade
        private const string kCategoryName = "RoadsServices";      // the Roads → Services upgrade-tools category
        private const string kCloneName    = "TownRoadLane Lane Markings";
        private const string kIcon         = "Media/Game/Icons/Crosswalk.svg"; // placeholder until we ship our own
        private const int    kPriority     = 75;                   // sits between Trees (80) and Grass (70)

        // City drive-lane prefabs that get this mod's markings — and thus need a no-marking clone for the upgrade.
        private static readonly string[] kCityLaneNames =
            { "Car Drive Lane 3", "Car Drive Lane 3 - Tram", "Public Transport Lane 3", "Public Transport Lane 3 - Tram" };
        private static string CloneLaneName(string original) => "TownRoadLane " + original + " (no markings)";

        /// <summary>
        /// Maps an original city drive-lane prefab ENTITY → its no-marking clone prefab ENTITY. Populated once the
        /// clones are created and registered. Read by <see cref="MarkingLaneSubstituteSystem"/>. Empty until ready
        /// (or if the per-segment feature is disabled).
        /// </summary>
        public static IReadOnlyDictionary<Entity, Entity> NoMarkingLaneByOriginal => s_NoMarkingLaneByOriginal;
        private static readonly Dictionary<Entity, Entity> s_NoMarkingLaneByOriginal = new Dictionary<Entity, Entity>();

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_NetPrefabQuery; // anything with PrefabData + NetData (roads, fences, …) — used to find the template and to patch road masks
        private EntityQuery m_RoadPrefabQuery;
        private EntityQuery m_LanePrefabQuery;

        private PrefabBase m_Clone;
        private UIAssetCategoryPrefab m_Category;
        private readonly List<(string origName, PrefabBase clone)> m_LaneClones = new List<(string, PrefabBase)>(); // resolved to entities in TryFinalize
        private bool m_CloneAdded;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_NetPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetData>());
            m_RoadPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<RoadData>());
            m_LanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetLaneData>());
            RequireForUpdate(m_NetPrefabQuery);
        }

        protected override void OnUpdate()
        {
            if (m_Done) { Enabled = false; return; }

            if (Mod.Settings != null && !Mod.Settings.SegmentToggleEnabled)
            {
                log.Info("MarkingUpgradePrefabSystem: per-segment Lane Markings toggle disabled in settings — skipping");
                m_Done = true; Enabled = false; return;
            }

            try
            {
                if (!m_CloneAdded) { TryCreateClone(); return; }   // step 1: create + register; let init run next
                TryFinalize();                                      // step 2: once baked, inject the flags
            }
            catch (Exception e)
            {
                log.Error(e, "MarkingUpgradePrefabSystem failed");
                m_Done = true; Enabled = false;
            }
        }

        private void TryCreateClone()
        {
            // Find the vanilla 'RoadZones' FencePrefab and the 'RoadsServices' UI category. Log all name matches
            // (a UI-manager mod could have added duplicates) so we know exactly what we picked.
            FencePrefab template = null;
            UIAssetCategoryPrefab roadsServices = null;
            int templateMatches = 0, categoryMatches = 0;
            var all = GetEntityQuery(ComponentType.ReadOnly<PrefabData>()).ToEntityArray(Allocator.Temp);
            for (int i = 0; i < all.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(all[i], out var p) || p == null) continue;
                if (p.name == kTemplateName && p is FencePrefab f) { templateMatches++; if (template == null) template = f; }
                else if (p.name == kTemplateName) log.Info($"[diag] '{kTemplateName}' name match but type={p.GetType().Name} (not FencePrefab)");
                if (p.name == kCategoryName && p is UIAssetCategoryPrefab cat) { categoryMatches++; if (roadsServices == null) roadsServices = cat; }
                else if (p.name == kCategoryName) log.Info($"[diag] '{kCategoryName}' name match but type={p.GetType().Name} (not UIAssetCategoryPrefab)");
            }
            all.Dispose();

            if (template == null) return; // prefabs not loaded yet — try again next update
            log.Info($"[diag] template '{kTemplateName}' FencePrefab matches={templateMatches}; category '{kCategoryName}' UIAssetCategoryPrefab matches={categoryMatches}");
            if (roadsServices == null) log.Warn($"UI category '{kCategoryName}' not found — the toolbar entry may not appear");

            // Clone manually so we can set m_Group BEFORE the entity is created (DuplicatePrefab's JSON clone does
            // not carry over UIObject.m_Group — it came out null, which is why the toolbar entry didn't appear).
            var clone = (FencePrefab)template.Clone(kCloneName);
            clone.Remove<ObsoleteIdentifiers>();

            // Make it a pure marker upgrade: no zoning toggle, paint-only, not underground.
            if (clone.TryGet<NetUpgrade>(out var upg))
            {
                upg.m_SetState = Array.Empty<NetPieceRequirements>();
                upg.m_UnsetState = Array.Empty<NetPieceRequirements>();
                upg.m_Standalone = false;
                upg.m_Underground = false;
            }
            else log.Warn($"clone '{kCloneName}' unexpectedly has no NetUpgrade component");

            // Our own toolbar identity. Re-attach the RoadsServices group (the clone lost it), set our icon/priority.
            // Keep the inherited Unlockable so it inherits RoadZones' early/free unlock.
            var uiViaTryGet = clone.TryGet<UIObject>(out var ui);
            log.Info($"[diag] clone.TryGet<UIObject> = {uiViaTryGet}; clone.components = [{string.Join(", ", System.Linq.Enumerable.Select(clone.components, c => c?.GetType().Name))}]");
            if (uiViaTryGet)
            {
                log.Info($"[diag] before assign: ui.m_Group = {(ui.m_Group != null ? ui.m_Group.name + "(" + ui.m_Group.GetType().Name + ")" : "<null>")}");
                if (roadsServices != null) ui.m_Group = roadsServices;
                ui.m_Icon = kIcon;
                ui.m_Priority = kPriority;
                ui.m_IsDebugObject = false;
                log.Info($"[diag] after assign:  ui.m_Group = {(ui.m_Group != null ? ui.m_Group.name : "<null>")}  icon={ui.m_Icon}  priority={ui.m_Priority}");
            }
            else log.Warn($"clone '{kCloneName}' unexpectedly has no UIObject component");

            bool added = m_PrefabSystem.AddPrefab(clone);
            m_Clone = clone;
            m_Category = roadsServices;
            m_CloneAdded = true;

            log.Info($"MarkingUpgradePrefabSystem: cloned '{kTemplateName}' → '{kCloneName}' (group='{(roadsServices != null ? roadsServices.name : "<null>")}', AddPrefab={added}), awaiting prefab init");

            CreateNoMarkingLaneClones();
        }

        /// <summary>
        /// Clones each city drive-lane prefab under a new name. Because nothing references the clone, its baked
        /// <c>SecondaryNetLane</c> buffer stays empty, so SecondaryLaneSystem won't draw any markings next to it.
        /// The clones are byte-identical otherwise (same mesh, CarLane width/flags, archetype) — safe substitutes.
        /// </summary>
        private void CreateNoMarkingLaneClones()
        {
            var byName = new Dictionary<string, NetLanePrefab>();
            var wanted = new HashSet<string>(kCityLaneNames);
            var ents = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(ents[i], out var lane) || lane == null) continue;
                if (wanted.Contains(lane.name) && !byName.ContainsKey(lane.name)) byName[lane.name] = lane;
            }
            ents.Dispose();

            foreach (var origName in kCityLaneNames)
            {
                if (!byName.TryGetValue(origName, out var orig) || orig == null)
                {
                    log.Warn($"MarkingUpgradePrefabSystem: city lane prefab '{origName}' not found — no no-marking clone for it");
                    continue;
                }
                var cloneName = CloneLaneName(origName);
                var laneClone = m_PrefabSystem.DuplicatePrefab(orig, cloneName); // Clone + Remove<ObsoleteIdentifiers> + AddPrefab
                m_PrefabSystem.UpdatePrefab(laneClone); // force a (re-)bake so NetLaneArchetypeData gets its archetypes — without
                                                        // this the clone is added but its lane archetype stays default/invalid
                m_LaneClones.Add((origName, laneClone));
                log.Info($"MarkingUpgradePrefabSystem: created no-marking lane clone '{cloneName}' from '{origName}'");
            }
        }

        private void TryFinalize()
        {
            if (m_Clone == null) { m_Done = true; Enabled = false; return; }
            if (!m_PrefabSystem.TryGetEntity(m_Clone, out var cloneEntity)) return;
            if (!EntityManager.HasComponent<PlaceableNetData>(cloneEntity)) return; // not baked yet

            // Resolve the original→clone lane entity map for MarkingLaneSubstituteSystem.
            if (m_LaneClones.Count > 0 && s_NoMarkingLaneByOriginal.Count == 0)
            {
                var origByName = new Dictionary<string, Entity>();
                var ents = m_LanePrefabQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < ents.Length; i++)
                {
                    if (!m_PrefabSystem.TryGetPrefab<NetLanePrefab>(ents[i], out var lane) || lane == null) continue;
                    if (!origByName.ContainsKey(lane.name)) origByName[lane.name] = ents[i];
                }
                ents.Dispose();
                foreach (var (origName, clonePrefab) in m_LaneClones)
                {
                    if (origByName.TryGetValue(origName, out var origEntity)
                        && m_PrefabSystem.TryGetEntity(clonePrefab, out var cloneLaneEntity))
                        s_NoMarkingLaneByOriginal[origEntity] = cloneLaneEntity;
                    else log.Warn($"MarkingUpgradePrefabSystem: could not map '{origName}' → its no-marking clone");
                }
                log.Info($"MarkingUpgradePrefabSystem: mapped {s_NoMarkingLaneByOriginal.Count} drive-lane prefab(s) to no-marking clones");
            }

            // 1. Inject our spare bit into the upgrade's set-flags so the tool actually writes it onto the edge.
            var pnd = EntityManager.GetComponentData<PlaceableNetData>(cloneEntity);
            pnd.m_SetUpgradeFlags.m_General |= MarkingFlags.MarkingsOff;
            // Ensure it's recognised as an upgrade and paint-only (NetInitializeSystem should already have set these
            // because the source carried a NetUpgrade, but be explicit).
            pnd.m_PlacementFlags |= Game.Net.PlacementFlags.IsUpgrade | Game.Net.PlacementFlags.UpgradeOnly;
            EntityManager.SetComponentData(cloneEntity, pnd);

            // (Widening every RoadPrefab's NetData.m_GeneralFlagMask by our bit so the upgrade tool considers it
            // applicable is now handled by MarkingFlagMaskExpanderSystem, which keeps doing it for road prefabs that
            // appear later — e.g. Road Builder generates roads at runtime, after this system has gone idle.)

            // 3. Make sure the toolbar entry is registered. The automatic UIObject.LateInitialize chain did NOT
            //    populate UIObjectData.m_Group for our clone (observed: group=Null, category buffer untouched), so
            //    do what UIObject.LateInitialize would have done, ourselves: add the clone to the RoadsServices
            //    category's element buffer and write its UIObjectData. Guard against double-adding.
            if (m_Category != null && m_PrefabSystem.TryGetEntity(m_Category, out var categoryEntity))
            {
                bool alreadyListed = false;
                if (EntityManager.HasBuffer<UIGroupElement>(categoryEntity))
                {
                    var buf = EntityManager.GetBuffer<UIGroupElement>(categoryEntity, isReadOnly: true);
                    for (int i = 0; i < buf.Length; i++) if (buf[i].m_Prefab == cloneEntity) { alreadyListed = true; break; }
                }
                if (!alreadyListed)
                {
                    m_Category.AddElement(EntityManager, cloneEntity); // appends UIGroupElement + UnlockRequirement on the category
                    log.Info($"MarkingUpgradePrefabSystem: manually registered '{kCloneName}' in category '{m_Category.name}'");
                }
                if (EntityManager.HasComponent<UIObjectData>(cloneEntity))
                {
                    EntityManager.SetComponentData(cloneEntity, new UIObjectData { m_Group = categoryEntity, m_Priority = kPriority });
                }
            }
            else log.Warn($"MarkingUpgradePrefabSystem: no RoadsServices category to register '{kCloneName}' into — toolbar entry will be missing");

            m_Done = true;
            Enabled = false;
            log.Info($"MarkingUpgradePrefabSystem: finalized — injected MarkingsOff bit into '{kCloneName}' PlaceableNetData (road flag masks handled by MarkingFlagMaskExpanderSystem)");

            DumpToolbarDiagnostics(cloneEntity);
        }

        /// <summary>One-off diagnostics: did the clone get registered in the toolbar category, and with what data?</summary>
        private void DumpToolbarDiagnostics(Entity cloneEntity)
        {
            try
            {
                var em = EntityManager;
                bool hasUIObjData = em.HasComponent<UIObjectData>(cloneEntity);
                bool hasLocked = em.HasComponent<Game.Prefabs.Locked>(cloneEntity);
                bool hasUnlockReq = em.HasBuffer<Game.Prefabs.UnlockRequirement>(cloneEntity);
                Entity groupEntity = Entity.Null;
                if (hasUIObjData) groupEntity = em.GetComponentData<UIObjectData>(cloneEntity).m_Group;
                log.Info($"[diag] clone '{kCloneName}': UIObjectData={hasUIObjData} (group={(groupEntity != Entity.Null ? Describe(groupEntity) : "Null")})  Locked={hasLocked}  UnlockRequirement-buffer={hasUnlockReq}");

                // The ComponentBase list still on the prefab (after our Remove<Unlockable>).
                if (m_Clone?.components != null)
                {
                    var sb = new System.Text.StringBuilder("[diag] clone components:");
                    foreach (var c in m_Clone.components) { sb.Append(' ').Append(c?.GetType().Name); }
                    log.Info(sb.ToString());
                }

                // Is the clone listed in the RoadsServices category's element buffer?
                if (groupEntity != Entity.Null && em.HasBuffer<UIGroupElement>(groupEntity))
                {
                    var buf = em.GetBuffer<UIGroupElement>(groupEntity, isReadOnly: true);
                    bool found = false;
                    for (int i = 0; i < buf.Length; i++) if (buf[i].m_Prefab == cloneEntity) { found = true; break; }
                    log.Info($"[diag] '{Describe(groupEntity)}' element buffer has {buf.Length} entries; contains our clone = {found}");
                }
                else log.Info("[diag] group entity has no UIGroupElement buffer (or group is Null) — UIObject.LateInitialize likely didn't run for the clone");

                // What ECS components does the clone entity carry now?
                using var types = em.GetComponentTypes(cloneEntity, Allocator.Temp);
                var sb2 = new System.Text.StringBuilder("[diag] clone archetype:");
                for (int i = 0; i < types.Length; i++) { sb2.Append(' ').Append(types[i].GetManagedType()?.Name ?? types[i].ToString()); }
                log.Info(sb2.ToString());
            }
            catch (Exception e) { log.Warn($"[diag] toolbar diagnostics failed: {e.Message}"); }
        }

        private string Describe(Entity e)
        {
            if (m_PrefabSystem.TryGetPrefab<PrefabBase>(e, out var p) && p != null) return $"{p.name}({p.GetType().Name})#{e.Index}";
            return $"entity#{e.Index}";
        }
    }
}
