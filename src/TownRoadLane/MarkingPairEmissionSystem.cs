using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 4 step 2 (managed approach). Diffs <see cref="MarkingPair"/> buffers against
    /// already-spawned sublanes (tagged with <see cref="TRLPairLink"/>) and reconciles:
    ///
    ///   - pair exists in buffer, no matching sublane     → create sublane
    ///   - pair gone, sublane still has TRLPairLink       → delete sublane
    ///   - pair exists, sublane exists                    → no-op
    ///
    /// Runs once per frame on the main thread (Modification1). EntityManager-based create/delete
    /// — vanilla GC / RemoveUnusedOldLanes only touches sublanes that appear in node SubLane
    /// buffers; the LaneReferencesSystem picks up our Owner-tagged sublanes automatically on the
    /// next tick.
    ///
    /// Curve calculation reuses <see cref="MarkingEndpointExtractor"/> for endpoint positions so
    /// what the user sees (overlay dots) matches what gets emitted.
    /// </summary>
    public partial class MarkingPairEmissionSystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithPairs;
        private EntityQuery _ourSubLanes;
        private PrefabSystem _prefabSystem;
        private EdgeLineCloneSystem _edgeLineSys;
        private CityConfigurationSystem _cityConfig;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _edgeLineSys = World.GetOrCreateSystemManaged<EdgeLineCloneSystem>();
            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            _nodesWithPairs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MarkingPair>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            _ourSubLanes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TRLPairLink>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            // Use one ECB for the whole tick — structural changes (AddComponent / CreateEntity)
            // applied directly to EntityManager mid-OnUpdate invalidate query snapshots, which is
            // what caused the "+2 created, wanted=0" loop after a toggle (the reconciliation pass
            // saw entities one-by-one being deleted as it iterated, queries went stale, and the
            // spawn pass thought entries were still missing). Deferring all writes to the ECB
            // until the end of OnUpdate keeps every read on a consistent snapshot.
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 1. Build a set of (node, pairKey) that SHOULD exist according to MarkingPair buffers.
            var wanted = new HashSet<(Entity, int)>();
            var nodes = _nodesWithPairs.ToEntityArray(Allocator.Temp);
            for (int n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                if (!EntityManager.HasBuffer<MarkingPair>(node)) continue;
                var pairs = EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true);
                for (int i = 0; i < pairs.Length; i++)
                    wanted.Add((node, TRLPairLink.ComputeKey(pairs[i])));
            }
            nodes.Dispose();

            // 2. Walk existing sublanes we own. Match against wanted set:
            //    - in set    → keep, mark as "resolved" by removing from set
            //    - not in set → delete (its pair was toggled off)
            // Dedupe-by-key: query may return multiple entities for the same key if previous
            // ticks accidentally spawned dupes; keep one, queue the rest for deletion via ECB.
            var existing = _ourSubLanes.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<(Entity, int)>();
            int deleted = 0;
            for (int i = 0; i < existing.Length; i++)
            {
                var sub = existing[i];
                var link = EntityManager.GetComponentData<TRLPairLink>(sub);
                var key = (link.node, link.pairKey);
                if (!wanted.Contains(key))
                {
                    ecb.AddComponent<Deleted>(sub);
                    deleted++;
                    continue;
                }
                if (seen.Contains(key))
                {
                    ecb.AddComponent<Deleted>(sub);
                    deleted++;
                    continue;
                }
                seen.Add(key);
                wanted.Remove(key);
            }
            existing.Dispose();

            // 3. wanted now holds only pairs that have no sublane yet. Spawn them.
            int created = 0;
            if (wanted.Count > 0)
            {
                var prefab = PickPrefab();
                if (prefab == Entity.Null)
                {
                    log.Warn("emission: no edge-line clone prefab resolved yet — cannot spawn pairs (deferring)");
                }
                else if (!EntityManager.HasComponent<NetLaneArchetypeData>(prefab))
                {
                    // Diagnostic: what components ARE on this prefab? Helps figure out which
                    // bake step needs to fire (often the archetype data lands later than the
                    // initial UpdatePrefab queue).
                    string compList = "";
                    using (var types = EntityManager.GetComponentTypes(prefab))
                    {
                        for (int i = 0; i < types.Length && i < 12; i++)
                        {
                            if (i > 0) compList += ", ";
                            compList += types[i].GetManagedType().Name;
                        }
                    }
                    log.Warn($"emission: prefab #{prefab.Index} has no NetLaneArchetypeData — cannot spawn. Components: [{compList}]");
                }
                else
                {
                    var arch = EntityManager.GetComponentData<NetLaneArchetypeData>(prefab).m_LaneArchetype;
                    // Walk nodes again to find the pair entries for the wanted keys (cheap — small N).
                    nodes = _nodesWithPairs.ToEntityArray(Allocator.Temp);
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        var node = nodes[n];
                        if (!EntityManager.HasBuffer<MarkingPair>(node)) continue;
                        var pairs = EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true);
                        // Resolve all endpoints for this node once (extractor walks ConnectedEdges).
                        var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
                        for (int i = 0; i < pairs.Length; i++)
                        {
                            int key = TRLPairLink.ComputeKey(pairs[i]);
                            if (!wanted.Contains((node, key))) continue;
                            wanted.Remove((node, key));
                            // Pass the buffer index `i` as the PathNode slot — it's stable per
                            // node-tick and unique among pairs on the same node. Hash-derived
                            // slots collided when two pairs produced the same XOR (delete-one →
                            // another appears, classic symptom).
                            if (SpawnSublane(ecb, node, pairs[i], key, i, prefab, arch, endpoints))
                                created++;
                        }
                    }
                    nodes.Dispose();
                }
            }

            if (created > 0 || deleted > 0)
                log.Info($"emission: +{created} created, -{deleted} deleted (wanted={wanted.Count} unmet, existing query returned {(_ourSubLanes.CalculateEntityCount())})");

            // Apply all structural changes in one go — queries above stayed on a consistent
            // snapshot for the entire OnUpdate.
            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        private bool SpawnSublane(EntityCommandBuffer ecb, Entity node, MarkingPair pair, int pairKey, int pairIndex, Entity prefab, EntityArchetype archetype, List<MarkingEndpoint> endpoints)
        {
            if (!TryFindEndpoint(endpoints, pair.sourceEdge, pair.sourceGapIndex, out var src)) return false;
            if (!TryFindEndpoint(endpoints, pair.targetEdge, pair.targetGapIndex, out var dst)) return false;

            // Pull factor controls how "curvy" the connector is. 0.55 approximates a quarter
            // circle (cubic-Bezier ideal); 0.4 gives a softer arc that still reads as curved
            // but stays closer to vanilla divider geometry.
            float chord = math.distance(src.position, dst.position);
            float pull = chord * 0.4f;
            float3 srcTan3 = new float3(-src.tangent.x, 0f, -src.tangent.y);
            float3 dstTan3 = new float3(-dst.tangent.x, 0f, -dst.tangent.y);
            Bezier4x3 bez = new Bezier4x3(src.position, src.position + srcTan3 * pull, dst.position + dstTan3 * pull, dst.position);

            // PathNode slots: 3 per sublane, unique among ALL sublanes on this node. Vanilla
            // primary sublanes are 0..N-1, then vanilla secondary lanes are higher. We claim a
            // high range (32768+ on top of pairIndex*4) so we never collide with vanilla.
            // pairIndex is buffer-order on the node — stable as long as the buffer order is.
            // Hash-derived bases collided ("delete one → another appears") and broke rendering.
            ushort idxBase = (ushort)(32768 + pairIndex * 4);
            var lane = new Lane
            {
                m_StartNode  = new PathNode(new PathNode(node, idxBase),               secondaryNode: true),
                m_MiddleNode = new PathNode(new PathNode(node, (ushort)(idxBase + 1)), secondaryNode: true),
                m_EndNode    = new PathNode(new PathNode(node, (ushort)(idxBase + 2)), secondaryNode: true),
            };

            // All structural ops go through the ECB so they take effect AFTER OnUpdate finishes
            // and the query snapshots stay consistent for the whole tick.
            Entity e = ecb.CreateEntity(archetype);
            ecb.SetComponent(e, new PrefabRef(prefab));
            ecb.SetComponent(e, lane);
            ecb.SetComponent(e, new Curve { m_Bezier = bez, m_Length = MathUtils.Length(bez) });
            ecb.AddComponent(e, new Owner { m_Owner = node });
            ecb.AddComponent(e, default(Elevation));
            ecb.AddComponent(e, new TRLPairLink { node = node, pairKey = pairKey });
            ecb.AddComponent(e, default(Created));
            ecb.AddComponent(e, default(Updated));
            // We deliberately do NOT mark the owner node Updated. That cascaded through
            // LaneReferencesSystem and re-triggered CustomSecondaryLaneSystem on the node every
            // tick, which fanned out into hundreds of duplicate Spawn calls.
            return true;
        }

        private static bool TryFindEndpoint(List<MarkingEndpoint> endpoints, Entity edge, int gap, out MarkingEndpoint ep)
        {
            for (int i = 0; i < endpoints.Count; i++)
            {
                if (endpoints[i].edge == edge && endpoints[i].gapIndex == gap)
                {
                    ep = endpoints[i];
                    return true;
                }
            }
            ep = default;
            return false;
        }

        /// <summary>Pick EU / NA edge-line clone entity based on the active city theme.</summary>
        private Entity PickPrefab()
        {
            if (_edgeLineSys == null) return Entity.Null;
            var theme = _cityConfig.defaultTheme;
            if (theme == Entity.Null) return _edgeLineSys.CloneEntityEU;
            if (_prefabSystem.TryGetPrefab<PrefabBase>(theme, out var themePrefab) && themePrefab != null)
            {
                var n = themePrefab.name;
                if (!string.IsNullOrEmpty(n) && (n.Contains("North American") || n.StartsWith("NA "))) return _edgeLineSys.CloneEntityNA;
            }
            return _edgeLineSys.CloneEntityEU;
        }
    }
}
