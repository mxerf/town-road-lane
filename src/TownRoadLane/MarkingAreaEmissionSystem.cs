using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using GameAreas = Game.Areas;

namespace TownRoadLane
{
    /// <summary>
    /// Per-node MarkingArea → vanilla Game.Areas.Area emitter. Mirrors
    /// <see cref="MarkingSegmentEmissionSystem"/>: builds the "wanted" set of (host node,
    /// area index) keys from MarkingArea buffers, then diffs against the set of already-spawned
    /// area entities (tagged with <see cref="TRLAreaLink"/>). Adds whatever's missing, deletes
    /// whatever's stale.
    ///
    /// Why the diff-by-tag pattern instead of "store the spawned Entity in a buffer on the host
    /// node": ECB.CreateEntity returns a deferred placeholder Entity. The real Entity only
    /// exists after Playback. Stashing the placeholder in a buffer means
    /// EntityManager.Exists(...) returns false next tick → we re-spawn every frame → infinite
    /// loop + FPS death. Tagging the spawned entity itself dodges the deferred-entity trap.
    ///
    /// Spawn template (validated by Area Bucket — see project-areas-feature memory):
    ///   Entity area = ecb.CreateEntity(surfacePrefabAreaData.m_Archetype);
    ///   ecb.SetComponent(area, new PrefabRef(surfacePrefab));
    ///   ecb.AddComponent(area, new Owner(hostNode));        // cascades delete when node dies
    ///   ecb.AddComponent(area, new Area(AreaFlags.Complete));
    ///   ecb.AddComponent(area, new TRLAreaLink { node, areaIndex });
    ///   var nodeBuf = ecb.AddBuffer&lt;Node&gt;(area);
    ///   foreach (float3 v in sampledPolygon) nodeBuf.Add(new Node(v, float.MinValue));
    ///
    /// Vanilla Game.Areas.GeometrySystem auto-triangulates when AreaFlags.Complete is set and
    /// Triangle buffer is empty — we don't fill it ourselves.
    /// </summary>
    public partial class MarkingAreaEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithAreas;
        private EntityQuery _ourAreas;
        private PrefabSystem _prefabSystem;
        private TerrainSystem _terrainSystem;

        // Phase 6d: style → SurfacePrefab name catalogue. styleId 0 stays "Solid Concrete" so
        // existing 6c areas keep rendering unchanged. G87 entries are best-effort — if the user
        // doesn't have G87 installed those slots fall back to the solid concrete prefab.
        //
        // Inventory verified by AreasPrototypeSystem dump 2026-05-19:
        //   - "Concrete Surface 01" : vanilla, prio=-97 layer=Terrain
        //   - "G87 UK Road Markings Misc G87 UK Junction Box Surface" : prio=4
        //     layer=Terrain,Roads,Buildings,Other — renders OVER road markings
        //   - G87 stripe surfaces use layer=Roads + prio=-10 → render on roads but UNDER markings
        //   - G87 bike/bus lane surfaces use layer=Terrain[,Roads] + prio=-5
        private static readonly string[] kStyleSurfaceNames = new[]
        {
            "Concrete Surface 01",                                                                              // 0 Solid
            "G87 UK Road Markings Misc G87 UK Junction Box Surface",                                             // 1 Junction Box (over markings)
            "G87 Road Markings SC Misc G87 Stripes 1to1 30cm Surface",                                           // 2 White Stripes dense
            "G87 Road Markings SC Misc G87 Stripes 2to1 60cm Surface",                                           // 3 White Stripes sparse
            "G87 Road Markings SC Misc G87 Stripes 1to1 30cm Yellow Surface",                                    // 4 Yellow Stripes dense
            "G87 UK Road Markings Misc G87 CS2 Green Bike Lane UM Surface",                                      // 5 Green bike
            "G87 UK Road Markings Misc G87 CS2 Red Bus Lane UM Surface",                                         // 6 Red bus
        };
        public const int kStyleCount = 7;
        public const int kStyleSolidConcrete = 0;

        // Resolved lazily — G87 surfaces show up ~10 s after game load. Entity.Null = retry next tick.
        private Entity[] _stylePrefabEntities = new Entity[kStyleCount];

        private int _heartbeatTicks;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            _nodesWithAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MarkingArea>(), ComponentType.ReadOnly<Game.Net.Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            _ourAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TRLAreaLink>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            _heartbeatTicks++;

            TryResolveAllStyles();
            Entity solidEntity = _stylePrefabEntities[kStyleSolidConcrete];
            if (solidEntity == Entity.Null) return;

            if (!EntityManager.HasComponent<AreaData>(solidEntity)) return;
            var solidAreaData = EntityManager.GetComponentData<AreaData>(solidEntity);
            if (!solidAreaData.m_Archetype.Valid) return;

            // 1. Build wanted set: (node, areaIndex) for every visible area with >= 3 vertices.
            //    Track expected styleId so we can detect style changes (respawn if the user
            //    cycles the style of an existing area — Phase 6d will wire this up, but the
            //    book-keeping is cheap to add now).
            var wanted = new HashSet<(Entity, int)>();
            var wantedStyle = new Dictionary<(Entity, int), int>();
            using (var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp))
            {
                for (int n = 0; n < nodes.Length; n++)
                {
                    var node = nodes[n];
                    if (!EntityManager.HasBuffer<MarkingArea>(node)) continue;
                    var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                    for (int a = 0; a < areas.Length; a++)
                    {
                        var ad = areas[a];
                        if (!ad.visible) continue;
                        if (ad.vertexCount < 3) continue;
                        var key = (node, a);
                        wanted.Add(key);
                        wantedStyle[key] = ad.styleId;
                    }
                }
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 2. Diff existing area entities against wanted.
            int deleted = 0;
            using (var existing = _ourAreas.ToEntityArray(Allocator.Temp))
            {
                var seen = new HashSet<(Entity, int)>();
                for (int i = 0; i < existing.Length; i++)
                {
                    var e = existing[i];
                    var link = EntityManager.GetComponentData<TRLAreaLink>(e);
                    var key = (link.node, link.areaIndex);
                    // Stale if: not wanted, or duplicate of one we already kept, or its style
                    // changed since spawn (style is the PrefabRef which we can read off the
                    // entity itself).
                    bool keep = wanted.Contains(key) && !seen.Contains(key);
                    if (keep && wantedStyle.TryGetValue(key, out var wantStyleId))
                    {
                        // Compare current PrefabRef against the wanted style's prefab. Cheap: a
                        // single component read + entity comparison.
                        if (EntityManager.HasComponent<PrefabRef>(e))
                        {
                            var curPrefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                            var wantPrefab = ResolveStylePrefabEntity(wantStyleId, solidEntity);
                            if (curPrefab != wantPrefab) keep = false;  // style changed → respawn
                        }
                    }
                    if (!keep)
                    {
                        ecb.AddComponent<Deleted>(e);
                        deleted++;
                        continue;
                    }
                    seen.Add(key);
                    wanted.Remove(key);
                }
            }

            // 3. Spawn anything left in wanted.
            int spawned = 0;
            if (wanted.Count > 0)
            {
                // Re-extract endpoints/corners per host node — but only for the nodes that
                // actually need a spawn this tick (most ticks: zero).
                var nodesNeedingSpawn = new Dictionary<Entity, (List<MarkingEndpoint> ep, List<MarkingCornerAnchor> co)>();
                foreach (var (node, areaIdx) in wanted)
                {
                    if (nodesNeedingSpawn.ContainsKey(node)) continue;
                    nodesNeedingSpawn[node] = (
                        MarkingEndpointExtractor.Extract(EntityManager, node),
                        MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node));
                }

                var terrainHeights = _terrainSystem.GetHeightData(waitForPending: false);
                foreach (var (node, areaIdx) in wanted)
                {
                    var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                    if (areaIdx < 0 || areaIdx >= areas.Length) continue;
                    var ad = areas[areaIdx];
                    if (!EntityManager.HasBuffer<MarkingAreaVertex>(node)) continue;
                    var verts = EntityManager.GetBuffer<MarkingAreaVertex>(node, isReadOnly: true);

                    var (endpoints, corners) = nodesNeedingSpawn[node];
                    var positions = new List<float3>(ad.vertexCount);
                    bool resolved = true;
                    for (int v = 0; v < ad.vertexCount; v++)
                    {
                        var vert = verts[ad.firstVertex + v];
                        if (!TryResolvePosition(vert, endpoints, corners, out var p))
                        {
                            resolved = false;
                            break;
                        }
                        positions.Add(p);
                    }
                    if (!resolved)
                    {
                        log.Info($"[area-emission] node #{node.Index} area #{areaIdx}: unresolved vertex (topology changed?), skipping");
                        continue;
                    }

                    TryGetStyleForEmission(ad.styleId, solidEntity, solidAreaData.m_Archetype, out var prefabForArea, out var archetypeForArea);
                    SpawnAreaEntity(archetypeForArea, prefabForArea, node, areaIdx, positions, ref terrainHeights, ref ecb);
                    spawned++;
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            if (_heartbeatTicks % 240 == 1 || spawned > 0 || deleted > 0)
            {
                int resolved = 0;
                for (int i = 0; i < kStyleCount; i++) if (_stylePrefabEntities[i] != Entity.Null) resolved++;
                log.Info($"[area-emission] tick={_heartbeatTicks} nodesWithAreas={_nodesWithAreas.CalculateEntityCount()} stylesResolved={resolved}/{kStyleCount} spawned={spawned} deleted={deleted}");
            }
        }

        private void TryResolveAllStyles()
        {
            bool anyMissing = false;
            for (int i = 0; i < kStyleCount; i++) if (_stylePrefabEntities[i] == Entity.Null) { anyMissing = true; break; }
            if (!anyMissing) return;

            var query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<SurfaceData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (pb is not SurfacePrefab sp) continue;
                for (int s = 0; s < kStyleCount; s++)
                {
                    if (_stylePrefabEntities[s] != Entity.Null) continue;
                    if (sp.name == kStyleSurfaceNames[s])
                    {
                        _stylePrefabEntities[s] = ents[i];
                        log.Info($"[area-emission] resolved style {s} = '{sp.name}' entity #{ents[i].Index}");
                    }
                }
            }
        }

        private Entity ResolveStylePrefabEntity(int styleId, Entity solidFallback)
        {
            int s = (styleId >= 0 && styleId < kStyleCount) ? styleId : kStyleSolidConcrete;
            var e = _stylePrefabEntities[s];
            return e != Entity.Null ? e : solidFallback;
        }

        private void TryGetStyleForEmission(int styleId, Entity solidEntity, EntityArchetype solidArchetype, out Entity prefabEntity, out EntityArchetype archetype)
        {
            prefabEntity = ResolveStylePrefabEntity(styleId, solidEntity);
            archetype = solidArchetype;
            if (EntityManager.HasComponent<AreaData>(prefabEntity))
            {
                var ad = EntityManager.GetComponentData<AreaData>(prefabEntity);
                if (ad.m_Archetype.Valid) archetype = ad.m_Archetype;
            }
        }

        private bool TryResolvePosition(MarkingAreaVertex v, List<MarkingEndpoint> endpoints, List<MarkingCornerAnchor> corners, out float3 pos)
        {
            pos = float3.zero;
            if (v.kind == 0)  // LaneEndpoint
            {
                if (v.refIndex < 0 || v.refIndex >= endpoints.Count) return false;
                pos = endpoints[v.refIndex].position;
                return true;
            }
            else if (v.kind == 1)  // NodeCorner
            {
                if (v.refIndex < 0 || v.refIndex >= corners.Count) return false;
                pos = corners[v.refIndex].position;
                return true;
            }
            return false;
        }

        private void SpawnAreaEntity(EntityArchetype archetype, Entity prefabEntity, Entity hostNode, int areaIndex,
                                      List<float3> positions, ref Game.Simulation.TerrainHeightData terrainHeights,
                                      ref EntityCommandBuffer ecb)
        {
            Entity e = ecb.CreateEntity(archetype);
            ecb.SetComponent(e, new PrefabRef(prefabEntity));
            ecb.AddComponent(e, new Owner(hostNode));
            ecb.AddComponent(e, new GameAreas.Area(GameAreas.AreaFlags.Complete));
            ecb.AddComponent(e, new TRLAreaLink { node = hostNode, areaIndex = areaIndex });

            var nodeBuf = ecb.AddBuffer<GameAreas.Node>(e);
            for (int i = 0; i < positions.Count; i++)
            {
                var node = new GameAreas.Node(positions[i], float.MinValue);
                node = GameAreas.AreaUtils.AdjustPosition(node, ref terrainHeights);
                nodeBuf.Add(node);
            }
        }
    }
}
