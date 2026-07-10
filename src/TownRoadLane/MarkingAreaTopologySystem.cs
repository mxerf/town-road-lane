using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 6e: owns the (<see cref="MarkingArea"/> + <see cref="MarkingLine"/>) →
    /// <see cref="MarkingAreaPiece"/> relationship. Mirrors <see cref="MarkingTopologySystem"/>
    /// which does the same job for lines (splitting them at intersections with other lines).
    ///
    /// On every change to either MarkingArea or MarkingLine on a node, recomputes the per-area
    /// piece set by clipping the area's outer ring against each line's Bezier (sampled into a
    /// short polyline, then fed to <see cref="PolygonSplitter"/>). Visibility per piece is
    /// inherited from whichever old piece contained the new piece's centroid — keeps user edits
    /// stable across topology changes (toggle a piece hidden, then add a line: the hidden state
    /// survives where it makes sense).
    ///
    /// Trigger: combined hash of MarkingArea + MarkingLine buffer. Skips work when both
    /// unchanged.
    ///
    /// Ordering: runs after <see cref="MarkingTopologySystem"/> so it sees the latest line
    /// buffer, and before <see cref="MarkingAreaEmissionSystem"/> so the piece buffer is fresh
    /// when emission diffs against spawned entities.
    /// </summary>
    [UpdateAfter(typeof(MarkingTopologySystem))]
    [UpdateBefore(typeof(MarkingAreaEmissionSystem))]
    public partial class MarkingAreaTopologySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private EntityQuery _nodesWithAreas;

        // How many points to sample a marking-line Bezier into for the chord-cut algorithm.
        // 16 points = 15 segments — enough resolution that a typical area (5–20 m on a side)
        // doesn't see visible faceting at the cut.
        private const int kCurveSampleCount = 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nodesWithAreas = GetEntityQuery(
                ComponentType.ReadOnly<MarkingArea>(),
                ComponentType.ReadOnly<Node>());
            RequireForUpdate(_nodesWithAreas);
        }

        protected override void OnUpdate()
        {
            using var nodes = _nodesWithAreas.ToEntityArray(Allocator.Temp);
            int rewritten = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (RecomputeIfChanged(nodes[i])) rewritten++;
            }
            if (rewritten > 0) log.Info($"MarkingAreaTopologySystem: recomputed pieces on {rewritten} node(s)");
        }

        private bool RecomputeIfChanged(Entity node)
        {
            if (!EntityManager.HasBuffer<MarkingArea>(node)) return false;
            var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
            if (areas.Length == 0 && !EntityManager.HasBuffer<MarkingAreaPiece>(node))
                return false;
            if (!EntityManager.HasBuffer<MarkingAreaVertex>(node)) return false;
            var areaVerts = EntityManager.GetBuffer<MarkingAreaVertex>(node, isReadOnly: true);

            DynamicBuffer<MarkingLine> lines = default;
            bool hasLines = EntityManager.HasBuffer<MarkingLine>(node);
            if (hasLines) lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);

            int newHash = HashAreaAndLines(areas, areaVerts, hasLines ? (DynamicBuffer<MarkingLine>?)lines : null);
            int oldHash = EntityManager.HasComponent<MarkingAreaTopologyState>(node)
                ? EntityManager.GetComponentData<MarkingAreaTopologyState>(node).combinedHash
                : 0;
            if (newHash == oldHash && EntityManager.HasBuffer<MarkingAreaPiece>(node))
                return false;

            // Save/load guard: right after loading a save, Composition/EdgeGeometry on the
            // connected edges are still zeroed (refilled at Modification3/4, after us), so
            // endpoint/corner extraction returns nothing and every ring would fail to resolve —
            // wiping the saved pieces and their per-piece visibility. Defer the whole node while
            // any connected edge is alive but not yet extraction-ready; retry next tick.
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                var connected = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
                for (int i = 0; i < connected.Length; i++)
                {
                    if (MarkingEndpointExtractor.IsEdgeAliveButUnready(EntityManager, connected[i].m_Edge))
                        return false;
                }
            }

            // Snapshot before any structural change — buffers become invalid the moment we
            // touch AddBuffer/RemoveComponent below.
            int areaCount = areas.Length;
            var areasSnap = new NativeArray<MarkingArea>(areaCount, Allocator.Temp);
            for (int i = 0; i < areaCount; i++) areasSnap[i] = areas[i];
            int areaVertCount = areaVerts.Length;
            var areaVertsSnap = new NativeArray<MarkingAreaVertex>(areaVertCount, Allocator.Temp);
            for (int i = 0; i < areaVertCount; i++) areaVertsSnap[i] = areaVerts[i];

            // Resolve current lane endpoints / corner anchors for vertex position lookup. Same
            // call MarkingAreaEmissionSystem makes — cheap (only on hash mismatch).
            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            var corners = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);

            // Sample every line into a short polyline for chord-cut intersection. Lines that
            // fail to resolve (one of their endpoints has gone away) are skipped — they're not
            // an active cut.
            var lineSamples = new List<List<float3>>();
            if (hasLines)
            {
                for (int l = 0; l < lines.Length; l++)
                {
                    if (!MarkingCurveBuilder.TryBuild(endpoints, lines[l], out var bez)) continue;
                    lineSamples.Add(SampleBezier(bez, kCurveSampleCount));
                }
            }

            // Capture old pieces so visibility can be inherited.
            var oldPiecesByArea = new List<List<(MarkingAreaPiece header, List<float3> ring)>>(areaCount);
            for (int i = 0; i < areaCount; i++) oldPiecesByArea.Add(new List<(MarkingAreaPiece, List<float3>)>());
            if (EntityManager.HasBuffer<MarkingAreaPiece>(node) && EntityManager.HasBuffer<MarkingAreaPieceVertex>(node))
            {
                var oldPieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
                var oldVerts = EntityManager.GetBuffer<MarkingAreaPieceVertex>(node, isReadOnly: true);
                for (int i = 0; i < oldPieces.Length; i++)
                {
                    var op = oldPieces[i];
                    if (op.areaIndex < 0 || op.areaIndex >= areaCount) continue;
                    var ring = new List<float3>(op.vertexCount);
                    for (int v = 0; v < op.vertexCount; v++)
                    {
                        int idx = op.firstVertex + v;
                        if (idx >= 0 && idx < oldVerts.Length) ring.Add(oldVerts[idx].position);
                    }
                    oldPiecesByArea[op.areaIndex].Add((op, ring));
                }
            }

            // Build the new flat piece list. For every area: assemble its outer ring →
            // split by every line in turn → record the resulting sub-polygons.
            var newPieces = new List<MarkingAreaPiece>(areaCount * 2);
            var newVerts = new List<MarkingAreaPieceVertex>(areaCount * 8);

            for (int a = 0; a < areaCount; a++)
            {
                var ad = areasSnap[a];
                // Note: hidden areas (ad.visible == false) still get their pieces computed —
                // emission filters on area visibility, and keeping the pieces means per-piece
                // visibility survives an area hide→show cycle instead of resetting to default.
                if (ad.vertexCount < 3) continue;

                var outerRing = ResolveOuterRing(areasSnap[a], areaVertsSnap, endpoints, corners);
                if (outerRing == null || outerRing.Count < 3)
                {
                    // Ring permanently unresolvable (a referenced anchor disappeared after a
                    // road change — the transient load case is deferred above). Carry the old
                    // pieces over verbatim instead of dropping them: cached geometry is the best
                    // truth we have and the user's per-piece visibility must survive.
                    var carried = oldPiecesByArea[a];
                    for (int p = 0; p < carried.Count; p++)
                    {
                        var (header, ring) = carried[p];
                        if (ring.Count < 3) continue;
                        header.areaIndex = a;
                        header.firstVertex = newVerts.Count;
                        header.vertexCount = ring.Count;
                        newPieces.Add(header);
                        for (int v = 0; v < ring.Count; v++)
                            newVerts.Add(new MarkingAreaPieceVertex { position = ring[v] });
                    }
                    continue;
                }

                var pieces = PolygonSplitter.SplitByPolylines(outerRing, lineSamples);
                for (int p = 0; p < pieces.Count; p++)
                {
                    var ring = pieces[p];
                    if (ring.Count < 3) continue;
                    float3 c = PolygonSplitter.CentroidXZ(ring);
                    bool visible = LookupInheritedVisibility(oldPiecesByArea[a], c, defaultVisible: true);
                    int firstVertex = newVerts.Count;
                    for (int v = 0; v < ring.Count; v++)
                        newVerts.Add(new MarkingAreaPieceVertex { position = ring[v] });
                    newPieces.Add(new MarkingAreaPiece
                    {
                        areaIndex = a,
                        pieceIndex = p,
                        visible = visible,
                        firstVertex = firstVertex,
                        vertexCount = ring.Count,
                        centroid = c,
                    });
                }
            }

            // Write back. AddBuffer overwrites if present.
            var pieceBuf = EntityManager.HasBuffer<MarkingAreaPiece>(node)
                ? EntityManager.GetBuffer<MarkingAreaPiece>(node)
                : EntityManager.AddBuffer<MarkingAreaPiece>(node);
            pieceBuf.Clear();
            for (int i = 0; i < newPieces.Count; i++) pieceBuf.Add(newPieces[i]);

            var vertBuf = EntityManager.HasBuffer<MarkingAreaPieceVertex>(node)
                ? EntityManager.GetBuffer<MarkingAreaPieceVertex>(node)
                : EntityManager.AddBuffer<MarkingAreaPieceVertex>(node);
            vertBuf.Clear();
            for (int i = 0; i < newVerts.Count; i++) vertBuf.Add(newVerts[i]);

            if (EntityManager.HasComponent<MarkingAreaTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = newHash });
            else
                EntityManager.AddComponentData(node, new MarkingAreaTopologyState { combinedHash = newHash });

            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);

            areasSnap.Dispose();
            areaVertsSnap.Dispose();

            log.Info($"area-topology node#{node.Index}: {areaCount} area(s) × {lineSamples.Count} cut(s) → {newPieces.Count} piece(s)");
            return true;
        }

        /// <summary>Build the outer-ring vertex list of one area by looking up its vertex refs
        /// against the live lane-endpoint / corner-anchor lists. Returns null if any vertex
        /// fails to resolve (line removed, road demolished, etc.).</summary>
        private static List<float3> ResolveOuterRing(MarkingArea ad, NativeArray<MarkingAreaVertex> verts,
                                                     List<MarkingEndpoint> endpoints, List<MarkingCornerAnchor> corners)
        {
            var ring = new List<float3>(ad.vertexCount);
            for (int v = 0; v < ad.vertexCount; v++)
            {
                int idx = ad.firstVertex + v;
                if (idx < 0 || idx >= verts.Length) return null;
                var av = verts[idx];
                if (av.kind == 0)
                {
                    if (av.refIndex < 0 || av.refIndex >= endpoints.Count) return null;
                    ring.Add(endpoints[av.refIndex].position);
                }
                else if (av.kind == 1)
                {
                    if (av.refIndex < 0 || av.refIndex >= corners.Count) return null;
                    ring.Add(corners[av.refIndex].position);
                }
                else return null;
            }
            return ring;
        }

        /// <summary>Sample a cubic Bezier at <paramref name="count"/> evenly spaced t values.</summary>
        private static List<float3> SampleBezier(Bezier4x3 bez, int count)
        {
            var pts = new List<float3>(count);
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0f : (float)i / (count - 1);
                pts.Add(MathUtils.Position(bez, t));
            }
            return pts;
        }

        /// <summary>Inheritance: new piece is "the same as" old piece P if P contains the new
        /// piece's centroid. Returns the visibility of the first such P, or default if none
        /// match (= truly new piece, e.g. after a new line was added).</summary>
        private static bool LookupInheritedVisibility(List<(MarkingAreaPiece header, List<float3> ring)> oldPieces,
                                                     float3 newCentroid, bool defaultVisible)
        {
            for (int i = 0; i < oldPieces.Count; i++)
            {
                if (PolygonSplitter.ContainsXZ(oldPieces[i].ring, newCentroid))
                    return oldPieces[i].header.visible;
            }
            return defaultVisible;
        }

        private static int HashAreaAndLines(DynamicBuffer<MarkingArea> areas, DynamicBuffer<MarkingAreaVertex> verts, DynamicBuffer<MarkingLine>? lines)
        {
            // FNV-1a 32-bit over: area styleId/visible/vertex-slice for every area, then each
            // vertex's (kind, refIndex, edgeToNext), then each line's geometry identity. Same
            // shape as MarkingTopologySystem.HashLines.
            const uint kPrime = 16777619u;
            uint h = 2166136261u;
            for (int i = 0; i < areas.Length; i++)
            {
                var a = areas[i];
                h = (h ^ (uint)a.styleId) * kPrime;
                h = (h ^ (uint)(a.visible ? 1 : 0)) * kPrime;
                h = (h ^ (uint)a.firstVertex) * kPrime;
                h = (h ^ (uint)a.vertexCount) * kPrime;
            }
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                h = (h ^ (uint)v.kind) * kPrime;
                h = (h ^ (uint)v.refIndex) * kPrime;
                h = (h ^ (uint)v.edgeToNext) * kPrime;
            }
            if (lines.HasValue)
            {
                var lb = lines.Value;
                for (int i = 0; i < lb.Length; i++)
                {
                    var l = lb[i];
                    h = (h ^ (uint)l.sourceEdge.Index) * kPrime;
                    h = (h ^ (uint)l.sourceGapIndex) * kPrime;
                    h = (h ^ (uint)l.targetEdge.Index) * kPrime;
                    h = (h ^ (uint)l.targetGapIndex) * kPrime;
                    // Curvature moves the cut polyline — pieces must be recomputed.
                    h = (h ^ math.asuint(l.curvature)) * kPrime;
                }
            }
            return (int)h;
        }
    }
}
