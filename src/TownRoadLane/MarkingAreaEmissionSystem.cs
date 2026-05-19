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
    /// Phase 6c: per-node MarkingArea → vanilla Game.Areas.Area emitter. Mirrors
    /// <see cref="MarkingSegmentEmissionSystem"/> at a higher granularity (one entity per area,
    /// not per segment) since vanilla AreaRenderSystem handles fill rendering on its own once
    /// the entity carries the right archetype + Node buffer.
    ///
    /// Spawn template (validated by Area Bucket — see project-areas-feature memory):
    ///
    ///   Entity area = ecb.CreateEntity(surfacePrefabAreaData.m_Archetype);
    ///   ecb.SetComponent(area, new PrefabRef(surfacePrefab));
    ///   ecb.AddComponent(area, new Owner(hostNode));        // cascades delete when node dies
    ///   ecb.AddComponent(area, new Area(AreaFlags.Complete));
    ///   var nodeBuf = ecb.AddBuffer&lt;Node&gt;(area);
    ///   foreach (float3 v in sampledPolygon) nodeBuf.Add(new Node(v, float.MinValue));
    ///
    /// Vanilla Game.Areas.GeometrySystem auto-triangulates when AreaFlags.Complete is set and
    /// Triangle buffer is empty — we don't fill it ourselves.
    ///
    /// MVP scope (6c):
    ///   • Straight-chord sampling only — sub-bezier sampling for LineBezier edges deferred to 6d.
    ///   • Single vanilla "Concrete Surface 01" prefab for all areas — style picker deferred to 6d.
    /// </summary>
    public partial class MarkingAreaEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithAreas;
        private PrefabSystem _prefabSystem;
        private TerrainSystem _terrainSystem;

        // Resolved lazily on first use — PrefabSystem may not have the prefab loaded when this
        // system's OnCreate runs (vanilla prefabs are loaded during PrefabUpdate). Cached after
        // the first successful resolve.
        private SurfacePrefab _solidSurfacePrefab;
        private Entity _solidSurfacePrefabEntity;

        // Vanilla concrete surface — see AreasPrototypeSystem dump: prio=-97, uvScale=0.2, layer=Terrain.
        // Solid grey concrete fill, the closest vanilla match to a safety-island texture.
        private const string kSolidSurfacePrefabName = "Concrete Surface 01";

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
        }

        protected override void OnUpdate()
        {
            _heartbeatTicks++;
            if (_heartbeatTicks % 240 == 1)
                log.Info($"[area-emission] heartbeat tick={_heartbeatTicks} nodesWithAreas={_nodesWithAreas.CalculateEntityCount()} prefabResolved={(_solidSurfacePrefabEntity != Entity.Null)}");

            // Lazy-resolve the surface prefab. PrefabSystem may not have it yet on the first few
            // ticks after game load — just retry next tick.
            if (_solidSurfacePrefabEntity == Entity.Null && !TryResolveSolidSurfacePrefab()) return;

            // Need the prefab's pre-built area archetype. AreaInitializeSystem fills this in once
            // the prefab is registered (see decomp/Game.Prefabs/AreaInitializeSystem.cs:266).
            if (!EntityManager.HasComponent<AreaData>(_solidSurfacePrefabEntity))
            {
                if (_heartbeatTicks % 60 == 1) log.Info("[area-emission] surface prefab entity exists but AreaData not ready yet");
                return;
            }
            var areaData = EntityManager.GetComponentData<AreaData>(_solidSurfacePrefabEntity);
            if (!areaData.m_Archetype.Valid)
            {
                if (_heartbeatTicks % 60 == 1) log.Info("[area-emission] AreaData.m_Archetype not Valid yet");
                return;
            }

            // `using var` would block the `ref ecb` argument below — keep manual Dispose.
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            using var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp);

            int spawned = 0;
            for (int n = 0; n < nodes.Length; n++)
            {
                ReconcileNode(nodes[n], areaData.m_Archetype, ref ecb, ref spawned);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
            if (spawned > 0) log.Info($"[area-emission] spawned {spawned} area entit(ies)");
        }

        private bool TryResolveSolidSurfacePrefab()
        {
            // Enumerate all SurfacePrefabs (cheap — there were 29 in the prototype dump) until we
            // find the one whose name matches kSolidSurfacePrefabName. Hashed lookup via PrefabID
            // would also work but requires the exact PrefabID type for SurfacePrefab — name match
            // is good enough for one-shot resolve.
            var query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<SurfaceData>());
            using var ents = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (pb is SurfacePrefab sp && sp.name == kSolidSurfacePrefabName)
                {
                    _solidSurfacePrefab = sp;
                    _solidSurfacePrefabEntity = ents[i];
                    log.Info($"[area-emission] resolved SurfacePrefab '{sp.name}' entity #{ents[i].Index}");
                    return true;
                }
            }
            if (_heartbeatTicks % 60 == 1)
                log.Info($"[area-emission] SurfacePrefab '{kSolidSurfacePrefabName}' not yet loaded (have {ents.Length} surfaces)");
            return false;
        }

        private void ReconcileNode(Entity node, EntityArchetype archetype, ref EntityCommandBuffer ecb, ref int spawned)
        {
            var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
            if (areas.Length == 0) return;
            if (!EntityManager.HasBuffer<MarkingAreaVertex>(node)) return;
            var verts = EntityManager.GetBuffer<MarkingAreaVertex>(node, isReadOnly: true);

            // TRLAreaLink buffer is parallel with MarkingArea (same index = same area). Create if
            // missing; size up to match areas.Length so the for-loop below stays simple.
            if (!EntityManager.HasBuffer<TRLAreaLink>(node))
                EntityManager.AddBuffer<TRLAreaLink>(node);
            var links = EntityManager.GetBuffer<TRLAreaLink>(node);
            while (links.Length < areas.Length) links.Add(new TRLAreaLink { areaEntity = Entity.Null });
            // Trim if user deleted areas (Phase 6d will hook up a delete UI; defensive trim here).
            while (links.Length > areas.Length)
            {
                var stale = links[links.Length - 1].areaEntity;
                if (stale != Entity.Null && EntityManager.Exists(stale))
                    ecb.AddComponent<Deleted>(stale);
                links.RemoveAt(links.Length - 1);
            }

            // Endpoints + corners change every node-edit tick — re-extract on each emission pass.
            // This is the same call SelectNode makes; cheap enough for the area count (~10 max).
            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            var corners = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);

            for (int a = 0; a < areas.Length; a++)
            {
                var areaDef = areas[a];
                var link = links[a];

                // Already spawned + entity still alive = no-op. Phase 6d will compare
                // (visible / styleId / vertex content hash) and respawn on change; for the MVP
                // we never edit areas in-place, so existence is enough.
                if (link.areaEntity != Entity.Null && EntityManager.Exists(link.areaEntity))
                {
                    if (!areaDef.visible)
                    {
                        ecb.AddComponent<Deleted>(link.areaEntity);
                        link.areaEntity = Entity.Null;
                        links[a] = link;
                    }
                    continue;
                }
                if (!areaDef.visible) continue;
                if (areaDef.vertexCount < 3) continue;

                // Resolve vertex positions through the live endpoint / corner lists.
                var positions = new List<float3>(areaDef.vertexCount);
                bool resolved = true;
                for (int v = 0; v < areaDef.vertexCount; v++)
                {
                    var vert = verts[areaDef.firstVertex + v];
                    if (!TryResolvePosition(vert, endpoints, corners, out var p))
                    {
                        resolved = false;
                        break;
                    }
                    positions.Add(p);
                }
                if (!resolved)
                {
                    log.Info($"[area-emission] node #{node.Index} area #{a}: unresolved vertex (topology changed?), skipping");
                    continue;
                }

                Entity newArea = SpawnAreaEntity(archetype, node, positions, ref ecb);
                link.areaEntity = newArea;
                links[a] = link;
                spawned++;
                log.Info($"[area-emission] spawned area #{a} on node #{node.Index} ({positions.Count} vertices)");
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

        private Entity SpawnAreaEntity(EntityArchetype archetype, Entity hostNode, List<float3> positions, ref EntityCommandBuffer ecb)
        {
            Entity e = ecb.CreateEntity(archetype);
            ecb.SetComponent(e, new PrefabRef(_solidSurfacePrefabEntity));
            ecb.AddComponent(e, new Owner(hostNode));
            ecb.AddComponent(e, new GameAreas.Area(GameAreas.AreaFlags.Complete));

            // Vanilla AreaUtils.AdjustPosition snaps Y to terrain height. Using float.MinValue as
            // a sentinel for unknown elevation matches what GenerateAreasSystem produces.
            var terrainHeights = _terrainSystem.GetHeightData(waitForPending: false);
            var nodeBuf = ecb.AddBuffer<GameAreas.Node>(e);
            for (int i = 0; i < positions.Count; i++)
            {
                var node = new GameAreas.Node(positions[i], float.MinValue);
                node = GameAreas.AreaUtils.AdjustPosition(node, ref terrainHeights);
                nodeBuf.Add(node);
            }
            return e;
        }
    }
}
