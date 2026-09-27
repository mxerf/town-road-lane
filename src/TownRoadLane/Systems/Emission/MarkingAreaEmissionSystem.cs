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
    /// Keeps one vanilla Game.Areas.Area surface entity (tagged <see cref="TRLAreaLink"/>) per
    /// visible piece of every <see cref="MarkingArea"/>, the same way
    /// <see cref="MarkingSegmentEmissionSystem"/> handles lines: missing ones are spawned, stale
    /// ones or ones whose style changed are deleted. The Owner is the host node, so deleting the
    /// node deletes its fills.
    ///
    /// The spawned entities are found through their tag rather than stored on the node:
    /// ECB.CreateEntity returns a placeholder that is only valid until Playback, so a stored
    /// reference would never exist on the next tick and the fill would respawn every frame.
    /// </summary>
    public partial class MarkingAreaEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithAreas;
        private EntityQuery _ourAreas;
        private PrefabSystem _prefabSystem;
        private TerrainSystem _terrainSystem;

        // styleId to SurfacePrefab name. styleId is saved with each area, so slots are never
        // renumbered: a retired slot keeps an empty name (never resolves, spawns concrete, hidden
        // from the UI). G87 slots fall back to concrete when the pack is not installed.
        //
        // Render order: the G87 junction box (priority 4, all layers) draws over road markings,
        // G87 stripes (Roads layer, priority -10) draw on roads but under markings.
        private static readonly string[] kStyleSurfaceNames = new[]
        {
            "Concrete Surface 01",                                                                              // 0 Solid
            "G87 UK Road Markings Misc G87 UK Junction Box Surface",                                             // 1 Junction Box (over markings)
            "G87 Road Markings SC Misc G87 Stripes 1to1 30cm Surface",                                           // 2 White Stripes dense
            "G87 Road Markings SC Misc G87 Stripes 2to1 60cm Surface",                                           // 3 White Stripes sparse
            "G87 Road Markings SC Misc G87 Stripes 1to1 30cm Yellow Surface",                                    // 4 Yellow Stripes dense
            "G87 UK Road Markings Misc G87 CS2 Green Bike Lane UM Surface",                                      // 5 Green bike
            "G87 UK Road Markings Misc G87 CS2 Red Bus Lane UM Surface",                                         // 6 Red bus
            "",                                                                                                  // 7 (reserved: Grass)
            "",                                                                                                  // 8 (reserved: Grass, dark)
            "",                                                                                                  // 9 (reserved: Sand)
            "",                                                                                                  // 10 (reserved: Pavement)
            "",                                                                                                  // 11 (reserved: Tiles 1)
            "",                                                                                                  // 12 (reserved: Tiles 2)
            "",                                                                                                  // 13 (reserved: Tiles 3)
            "G87 Vanilla Asphalt Pavement G87 VA Surface URM Surface",                                           // 14 Asphalt patch (layer=Terrain,Roads)
            // Registered at runtime by VanillaSurfaceLateClone.
            VanillaSurfaceLateClone.kCloneGrass,                                                                 // 15 Grass
            "",                                                                                                  // 16 (reserved: Grass variant B)
            VanillaSurfaceLateClone.kCloneGrassDark,                                                             // 17 Grass, dark
            VanillaSurfaceLateClone.kCloneSand,                                                                  // 18 Sand
            VanillaSurfaceLateClone.kClonePavement,                                                              // 19 Pavement
            VanillaSurfaceLateClone.kCloneTiles1,                                                                // 20 Tiles 1
            VanillaSurfaceLateClone.kCloneTiles2,                                                                // 21 Tiles 2
            VanillaSurfaceLateClone.kCloneTiles3,                                                                // 22 Tiles 3
        };
        public const int kStyleCount = 23;
        public const int kStyleSolidConcrete = 0;

        /// <summary>False for retired slots: they never resolve, are hidden from the UI and are
        /// skipped by the U-hotkey cycle.</summary>
        public static bool IsStyleEnabled(int id)
            => id >= 0 && id < kStyleCount && !string.IsNullOrEmpty(kStyleSurfaceNames[id]);

        /// <summary>Next enabled style after <paramref name="current"/>, wrapping around; used by
        /// the U-hotkey cycle.</summary>
        public static int NextEnabledStyle(int current)
        {
            for (int step = 1; step <= kStyleCount; step++)
            {
                int candidate = (current + step) % kStyleCount;
                if (IsStyleEnabled(candidate)) return candidate;
            }
            return kStyleSolidConcrete;
        }

        // Resolved lazily: G87 surfaces appear some seconds after load. Entity.Null means retry.
        private Entity[] _stylePrefabEntities = new Entity[kStyleCount];

        // Unresolved-style report, see TryResolveAllStyles. Surface prefabs keep importing for
        // minutes after load, with pauses of several seconds, so the report waits until the count
        // has not changed for kSurfaceSettleSeconds. Real time, because the pauses do not scale
        // with frame rate.
        private int _lastSurfaceCount = -1;
        private float _surfaceCountChangedAt;
        private int _lastSurfaceDumpCount = -1;
        private const float kSurfaceSettleSeconds = 60f;
        // Warnings for the OnUpdate early-out and the concrete fallback; without them a user
        // whose fills never appear has nothing in the log.
        private int _blockedTicks;
        private const int kBlockedWarnTicks = 600; // matches kOrphanSweepMaxWaitTicks scale
        private readonly HashSet<int> _fallbackWarned = new HashSet<int>();

        // Older saves contain our fills without the TRLAreaLink tag (it was not serialized), one
        // stacked copy per save/load cycle. Swept once per load, after styles resolve or after
        // the wait budget runs out.
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
            string blocked =
                solidEntity == Entity.Null ? "style 0 'Concrete Surface 01' not resolved" :
                !EntityManager.HasComponent<AreaData>(solidEntity) ? "style 0 prefab has no AreaData" :
                !EntityManager.GetComponentData<AreaData>(solidEntity).m_Archetype.Valid ? "style 0 archetype invalid" : null;
            if (blocked != null)
            {
                // A short block right after load is normal (assets are still importing), but a
                // persistent one disables every fill.
                if (++_blockedTicks == kBlockedWarnTicks)
                    log.Warn($"[area-emission] BLOCKED for {kBlockedWarnTicks} ticks: {blocked} — no fills of any style will spawn");
                return;
            }
            _blockedTicks = 0;
            var solidAreaData = EntityManager.GetComponentData<AreaData>(solidEntity);

            if (_orphanSweepPending)
            {
                // Wait for the full style set so G87 orphans are recognised too, but not
                // forever: G87 may not be installed.
                bool allResolved = true;
                for (int i = 0; i < kStyleCount; i++) if (IsStyleEnabled(i) && _stylePrefabEntities[i] == Entity.Null) { allResolved = false; break; }
                if (allResolved || --_orphanSweepPatience <= 0)
                {
                    _orphanSweepPending = false;
                    SweepOrphanFills();
                }
            }

            // Wanted set: (node, areaIndex, pieceIndex) for every visible piece of every visible
            // area. Pieces and their vertices come precomputed from MarkingAreaTopologySystem.
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

            // Delete stale, duplicate and restyled fills.
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
                            if (curPrefab != wantPrefab) keep = false;
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

            // Spawn the rest.
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

                    // pieceIndex counts pieces within one area; it is not a buffer index.
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

        /// <summary>Deletes untagged copies of our fills left in older saves, where the game kept
        /// the area entity but dropped the unserialized TRLAreaLink tag, adding one more copy
        /// on every save/load.
        ///
        /// An untagged area counts as ours when its prefab is one of our style surfaces and at
        /// least 60% of its nodes lie within 0.7 m of our piece outlines (the tolerance covers
        /// outline changes at sharp tips). A player-placed surface of the same prefab does not
        /// follow our outlines.</summary>
        private void SweepOrphanFills()
        {
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
                    if (onContour * 10 < areaNodes.Length * 6) continue; // under 60%: not ours
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
            for (int i = 0; i < kStyleCount; i++)
                if (IsStyleEnabled(i) && _stylePrefabEntities[i] == Entity.Null) { anyMissing = true; break; }
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
                        // Priority and layer decide whether the fill can draw on the road at
                        // all: without the Roads layer it ends up under the road surface.
                        string renderInfo = sp.TryGet<RenderedArea>(out var ra) && ra != null
                            ? $" prio={ra.m_RendererPriority} layer={ra.m_DecalLayerMask}"
                            : " (no RenderedArea)";
                        log.Info($"[area-emission] resolved style {s} = '{sp.name}' entity #{ents[i].Index}{renderInfo}");
                    }
                }
            }

            // G87 updates have renamed their surface prefabs before, which silently breaks the
            // exact-name match above and turns every fill into concrete. While a style is still
            // unresolved, log the missing styles and the runtime names of all G87 surfaces, which
            // is exactly what kStyleSurfaceNames needs. Only once per settled surface count:
            // logging on every count change during the long asynchronous import floods the log
            // and gets the mod flagged by Skyve.
            bool stillMissing = false;
            for (int i = 0; i < kStyleCount; i++)
                if (IsStyleEnabled(i) && _stylePrefabEntities[i] == Entity.Null) { stillMissing = true; break; }
            if (!stillMissing) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (ents.Length != _lastSurfaceCount)
            {
                _lastSurfaceCount = ents.Length;
                _surfaceCountChangedAt = now;
                return;
            }
            if (now - _surfaceCountChangedAt < kSurfaceSettleSeconds || ents.Length == _lastSurfaceDumpCount) return;
            _lastSurfaceDumpCount = ents.Length;

            var report = new System.Text.StringBuilder("[area-emission] style resolve incomplete after surface import settled — missing:");
            for (int i = 0; i < kStyleCount; i++)
                if (IsStyleEnabled(i) && _stylePrefabEntities[i] == Entity.Null)
                    report.Append(' ').Append(i).Append(" '").Append(kStyleSurfaceNames[i]).Append("';");
            int g87Count = 0;
            for (int i = 0; i < ents.Length; i++)
            {
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(ents[i], out var pb) || pb == null) continue;
                if (!pb.name.Contains("G87")) continue;
                g87Count++;
                report.AppendLine().Append("  G87 surface present: '").Append(pb.name).Append('\'');
            }
            report.AppendLine().Append("  ").Append(ents.Length).Append(" surface prefab(s) total, ").Append(g87Count).Append(" G87 among them");
            log.Info(report.ToString());
        }

        private Entity ResolveStylePrefabEntity(int styleId, Entity solidFallback)
        {
            int s = (styleId >= 0 && styleId < kStyleCount) ? styleId : kStyleSolidConcrete;
            var e = _stylePrefabEntities[s];
            if (e == Entity.Null && s != kStyleSolidConcrete && IsStyleEnabled(s) && _fallbackWarned.Add(s))
                log.Warn($"[area-emission] style {s} '{kStyleSurfaceNames[s]}' unresolved — spawning as concrete (self-corrects once the prefab loads; stays concrete if its pack is missing)");
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
                // Positions carry the real road surface height. At ground level the vanilla
                // terrain-follow convention (elevation float.MinValue, snapped to terrain) keeps
                // the fill on the ground when it is terraformed. On bridges and ramps that snap
                // would drop the fill to the ground below, so the deck height is kept and the
                // offset goes into m_Elevation: GroundHeightSystem only re-snaps nodes whose
                // elevation is float.MinValue.
                float ground = Game.Simulation.TerrainUtils.SampleHeight(ref terrainHeights, positions[i]);
                float dy = positions[i].y - ground;
                if (dy > 0.75f)
                {
                    nodeBuf.Add(new GameAreas.Node(positions[i], dy));
                }
                else
                {
                    var node = new GameAreas.Node(positions[i], float.MinValue);
                    node = GameAreas.AreaUtils.AdjustPosition(node, ref terrainHeights);
                    nodeBuf.Add(node);
                }
            }
        }
    }
}
