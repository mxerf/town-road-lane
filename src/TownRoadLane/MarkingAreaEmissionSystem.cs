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

        // Surface-prefab count at the last G87 diagnostic dump — see TryResolveAllStyles.
        private int _lastSurfaceDumpCount = -1;

        // Post-load orphan sweep (2.2.0 migration): pre-2.2.0 saves contain our spawned fills
        // WITHOUT the TRLAreaLink tag (it wasn't serialized), one stacked copy per save/load
        // cycle. Runs once per load, after styles resolve (or after the patience budget).
        private bool _orphanSweepPending;
        private int _orphanSweepPatience;
        private const int kOrphanSweepMaxWaitTicks = 600; // ≈ tens of seconds of sim ticks

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

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            _orphanSweepPending = true;
            _orphanSweepPatience = kOrphanSweepMaxWaitTicks;
        }

        protected override void OnUpdate()
        {
            TryResolveAllStyles();
            Entity solidEntity = _stylePrefabEntities[kStyleSolidConcrete];
            if (solidEntity == Entity.Null) return;

            if (!EntityManager.HasComponent<AreaData>(solidEntity)) return;
            var solidAreaData = EntityManager.GetComponentData<AreaData>(solidEntity);
            if (!solidAreaData.m_Archetype.Valid) return;

            if (_orphanSweepPending)
            {
                // Wait for the full style set (G87 resolves lazily ~10 s after load) so G87
                // orphans are recognisable too — but not forever, G87 may be missing.
                bool allResolved = true;
                for (int i = 0; i < kStyleCount; i++) if (_stylePrefabEntities[i] == Entity.Null) { allResolved = false; break; }
                if (allResolved || --_orphanSweepPatience <= 0)
                {
                    _orphanSweepPending = false;
                    SweepOrphanFills();
                }
            }

            // 1. Build wanted set: (node, areaIndex, pieceIndex) for every visible piece of every
            //    visible area. Pieces come from MarkingAreaTopologySystem; their vertex positions
            //    are pre-computed in MarkingAreaPieceVertex so emission doesn't have to re-resolve
            //    lane endpoints / corners per tick.
            var wanted = new HashSet<(Entity, int, int)>();
            var wantedStyle = new Dictionary<(Entity, int, int), int>();
            using (var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp))
            {
                for (int n = 0; n < nodes.Length; n++)
                {
                    var node = nodes[n];
                    if (!EntityManager.HasBuffer<MarkingArea>(node)) continue;
                    if (!EntityManager.HasBuffer<MarkingAreaPiece>(node)) continue;
                    var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                    var pieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
                    for (int p = 0; p < pieces.Length; p++)
                    {
                        var pd = pieces[p];
                        if (!pd.visible) continue;
                        if (pd.vertexCount < 3) continue;
                        if (pd.areaIndex < 0 || pd.areaIndex >= areas.Length) continue;
                        var ad = areas[pd.areaIndex];
                        if (!ad.visible) continue;
                        var key = (node, pd.areaIndex, pd.pieceIndex);
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
                var seen = new HashSet<(Entity, int, int)>();
                for (int i = 0; i < existing.Length; i++)
                {
                    var e = existing[i];
                    var link = EntityManager.GetComponentData<TRLAreaLink>(e);
                    var key = (link.node, link.areaIndex, link.pieceIndex);
                    bool keep = wanted.Contains(key) && !seen.Contains(key);
                    if (keep && wantedStyle.TryGetValue(key, out var wantStyleId))
                    {
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

            // 3. Spawn anything left in wanted. Vertex positions come straight from the
            //    pre-computed MarkingAreaPieceVertex buffer — no per-tick endpoint resolution.
            int spawned = 0;
            if (wanted.Count > 0)
            {
                var terrainHeights = _terrainSystem.GetHeightData(waitForPending: false);
                foreach (var (node, areaIdx, pieceIdx) in wanted)
                {
                    if (!EntityManager.HasBuffer<MarkingArea>(node)) continue;
                    if (!EntityManager.HasBuffer<MarkingAreaPiece>(node)) continue;
                    if (!EntityManager.HasBuffer<MarkingAreaPieceVertex>(node)) continue;
                    var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                    var pieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
                    var pieceVerts = EntityManager.GetBuffer<MarkingAreaPieceVertex>(node, isReadOnly: true);
                    if (areaIdx < 0 || areaIdx >= areas.Length) continue;

                    // Find the piece header in the buffer (pieces are not addressed by their
                    // own index in the buffer — pieceIndex is the per-area dense counter).
                    MarkingAreaPiece pd = default;
                    bool found = false;
                    for (int i = 0; i < pieces.Length; i++)
                    {
                        if (pieces[i].areaIndex == areaIdx && pieces[i].pieceIndex == pieceIdx)
                        {
                            pd = pieces[i];
                            found = true;
                            break;
                        }
                    }
                    if (!found) continue;

                    var positions = new List<float3>(pd.vertexCount);
                    for (int v = 0; v < pd.vertexCount; v++)
                    {
                        int idx = pd.firstVertex + v;
                        if (idx < 0 || idx >= pieceVerts.Length) { positions.Clear(); break; }
                        positions.Add(pieceVerts[idx].position);
                    }
                    if (positions.Count < 3) continue;

                    var ad = areas[areaIdx];
                    TryGetStyleForEmission(ad.styleId, solidEntity, solidAreaData.m_Archetype, out var prefabForArea, out var archetypeForArea);
                    SpawnAreaEntity(archetypeForArea, prefabForArea, node, areaIdx, pieceIdx, positions, ref terrainHeights, ref ecb);
                    spawned++;
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            if (spawned > 0 || deleted > 0)
            {
                int resolved = 0;
                for (int i = 0; i < kStyleCount; i++) if (_stylePrefabEntities[i] != Entity.Null) resolved++;
                log.Info($"[area-emission] nodesWithAreas={_nodesWithAreas.CalculateEntityCount()} stylesResolved={resolved}/{kStyleCount} spawned={spawned} deleted={deleted}");
            }
        }

        /// <summary>One-shot post-load migration: delete untagged copies of OUR fills left by
        /// pre-2.2.0 saves (TRLAreaLink wasn't serialized then — the game kept the vanilla
        /// area entity but dropped the tag; every save/load stacked one more copy).
        ///
        /// An untagged vanilla area counts as ours when (a) its prefab is one of our style
        /// surfaces AND (b) the bulk of its ring nodes lie on one of our piece rings (60% of
        /// nodes within 0.7 m — covers the envelope→true-contour ring change at sharp tips).
        /// A hand-placed player surface of the same prefab won't trace our contour.</summary>
        private void SweepOrphanFills()
        {
            // All piece-ring points of all marked nodes, one flat list (tiny in practice).
            var ringPoints = new List<float3>(256);
            using (var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp))
            {
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (!EntityManager.HasBuffer<MarkingAreaPieceVertex>(nodes[n])) continue;
                    var verts = EntityManager.GetBuffer<MarkingAreaPieceVertex>(nodes[n], isReadOnly: true);
                    for (int v = 0; v < verts.Length; v++) ringPoints.Add(verts[v].position);
                }
            }
            if (ringPoints.Count == 0) return;

            var ourPrefabs = new HashSet<Entity>();
            for (int i = 0; i < kStyleCount; i++)
                if (_stylePrefabEntities[i] != Entity.Null) ourPrefabs.Add(_stylePrefabEntities[i]);

            var candidates = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<TRLAreaLink>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            const float kOnContourSq = 0.7f * 0.7f;
            int removed = 0;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            using (var ents = candidates.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    if (!ourPrefabs.Contains(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab)) continue;
                    var areaNodes = EntityManager.GetBuffer<Game.Areas.Node>(e, isReadOnly: true);
                    if (areaNodes.Length < 3) continue;
                    int onContour = 0;
                    for (int v = 0; v < areaNodes.Length; v++)
                    {
                        var p = areaNodes[v].m_Position;
                        for (int r = 0; r < ringPoints.Count; r++)
                        {
                            float dx = ringPoints[r].x - p.x, dz = ringPoints[r].z - p.z;
                            if (dx * dx + dz * dz < kOnContourSq) { onContour++; break; }
                        }
                    }
                    if (onContour * 10 < areaNodes.Length * 6) continue; // < 60% — not ours
                    ecb.AddComponent<Deleted>(e);
                    removed++;
                }
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();
            if (removed > 0)
                log.Info($"[area-emission] post-load sweep: removed {removed} orphaned untagged fill(s) from a pre-2.2.0 save");
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

            // Diagnostic: G87 updates have renamed/restructured their surface prefabs before
            // (v1.3 merged the UK set into the main package), which silently breaks the
            // exact-name match above and drops every fill back to concrete. While any style is
            // still unresolved, dump the runtime names of all G87 surface prefabs whenever the
            // surface-prefab count changes (assets keep importing ~10 s after load) — the log
            // then contains exactly what kStyleSurfaceNames needs to say.
            bool stillMissing = false;
            for (int i = 0; i < kStyleCount; i++)
                if (_stylePrefabEntities[i] == Entity.Null) { stillMissing = true; break; }
            if (stillMissing && ents.Length != _lastSurfaceDumpCount)
            {
                _lastSurfaceDumpCount = ents.Length;
                int g87Count = 0;
                for (int i = 0; i < ents.Length; i++)
                {
                    if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                    if (!pb.name.Contains("G87")) continue;
                    g87Count++;
                    log.Info($"[area-emission] G87 surface present: '{pb.name}' ({pb.GetType().Name})");
                }
                log.Info($"[area-emission] style resolve incomplete — {ents.Length} surface prefab(s) total, {g87Count} G87 among them");
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

        private void SpawnAreaEntity(EntityArchetype archetype, Entity prefabEntity, Entity hostNode, int areaIndex, int pieceIndex,
                                      List<float3> positions, ref Game.Simulation.TerrainHeightData terrainHeights,
                                      ref EntityCommandBuffer ecb)
        {
            Entity e = ecb.CreateEntity(archetype);
            ecb.SetComponent(e, new PrefabRef(prefabEntity));
            ecb.AddComponent(e, new Owner(hostNode));
            ecb.AddComponent(e, new GameAreas.Area(GameAreas.AreaFlags.Complete));
            ecb.AddComponent(e, new TRLAreaLink { node = hostNode, areaIndex = areaIndex, pieceIndex = pieceIndex });

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
