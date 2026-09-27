using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;
using TownRoadLane.Components;
using TownRoadLane.Geometry;

namespace TownRoadLane.Systems.Tool
{
    // Cursor hit-testing against the selected node's dots, lines, areas and area anchors. All
    // distances are measured in the XZ plane: the raycast hit and the target can be at
    // different heights.
    public partial class MarkingNodeToolSystem
    {
        // Squared pick radius for dots, in metres, measured in the XZ plane from the cursor's
        // raycast hit. Deliberately larger than the drawn dot.
        private const float kDotPickRadiusSq = 1.5f * 1.5f;
        // Squared XZ distance within which the cursor counts as being on a line.
        private const float kLinePickRadiusSq = 2.0f * 2.0f;
        // Samples per Bezier: about 1 m spacing on a typical 10-12 m line.
        private const int kLineSampleCount = 12;

        // Reused point-in-polygon ring, to avoid per-frame allocations.
        private readonly List<float3> _areaHitScratch = new List<float3>();


        private int FindHoveredEndpoint(float3 cursor)
        {
            int best = -1;
            float bestSq = kDotPickRadiusSq;
            for (int i = 0; i < _endpoints.Count; i++)
            {
                float sq = PolygonUtils.DistSqXZ(_endpoints[i].position, cursor);
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }

        /// <summary>Index of the MarkingLine passing closest to the cursor, or -1 if none is
        /// within <see cref="kLinePickRadiusSq"/>. Samples each curve instead of solving
        /// exactly; the lines are short and the radius generous, so that is accurate enough.</summary>
        private int HitTestLines(float3 cursor)
        {
            if (_selectedNode == Entity.Null) return -1;
            if (!EntityManager.HasBuffer<MarkingLine>(_selectedNode)) return -1;
            var lines = EntityManager.GetBuffer<MarkingLine>(_selectedNode, isReadOnly: true);
            if (lines.Length == 0) return -1;

            int best = -1;
            float bestSq = kLinePickRadiusSq;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!MarkingCurveBuilder.TryBuild(_endpoints, lines[i], out var bez)) continue;
                for (int s = 0; s <= kLineSampleCount; s++)
                {
                    float t = (float)s / kLineSampleCount;
                    float sq = PolygonUtils.DistSqXZ(MathUtils.Position(bez, t), cursor);
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
            }
            return best;
        }

        /// <summary>areaIndex of the piece the cursor is inside, or -1. Uses the MarkingAreaPiece
        /// rings, whose positions the topology system has already resolved.</summary>
        private int HitTestAreas(float3 cursor)
        {
            if (_selectedNode == Entity.Null) return -1;
            if (!EntityManager.HasBuffer<MarkingAreaPiece>(_selectedNode)
                || !EntityManager.HasBuffer<MarkingAreaPieceVertex>(_selectedNode)) return -1;
            var pieces = EntityManager.GetBuffer<MarkingAreaPiece>(_selectedNode, isReadOnly: true);
            var verts = EntityManager.GetBuffer<MarkingAreaPieceVertex>(_selectedNode, isReadOnly: true);
            for (int p = 0; p < pieces.Length; p++)
            {
                var pd = pieces[p];
                if (pd.vertexCount < 3) continue;
                _areaHitScratch.Clear();
                bool ok = true;
                for (int v = 0; v < pd.vertexCount; v++)
                {
                    int idx = pd.firstVertex + v;
                    if (idx < 0 || idx >= verts.Length) { ok = false; break; }
                    _areaHitScratch.Add(verts[idx].position);
                }
                if (ok && PolygonUtils.ContainsXZ(_areaHitScratch, cursor)) return pd.areaIndex;
            }
            return -1;
        }

        /// <summary>Closest area candidate (lane endpoint, corner anchor or line crossing) to the
        /// cursor, measured in XZ like <see cref="FindHoveredEndpoint"/>.</summary>
        private AreaCandidate FindHoveredAreaCandidate(float3 cursor)
        {
            AreaCandidate best = AreaCandidate.None;
            float bestSq = kDotPickRadiusSq;
            for (int i = 0; i < _endpoints.Count; i++)
            {
                float sq = PolygonUtils.DistSqXZ(_endpoints[i].position, cursor);
                if (sq < bestSq) { bestSq = sq; best = new AreaCandidate { kind = AreaAnchorKind.LaneEndpoint, refIndex = i }; }
            }
            for (int i = 0; i < _cornerAnchors.Count; i++)
            {
                float sq = PolygonUtils.DistSqXZ(_cornerAnchors[i].position, cursor);
                if (sq < bestSq) { bestSq = sq; best = new AreaCandidate { kind = AreaAnchorKind.NodeCorner, refIndex = i }; }
            }
            for (int i = 0; i < _lineIntersections.Count; i++)
            {
                float sq = PolygonUtils.DistSqXZ(_lineIntersections[i].position, cursor);
                if (sq < bestSq) { bestSq = sq; best = new AreaCandidate { kind = AreaAnchorKind.LineIntersection, refIndex = _lineIntersections[i].PackedRef }; }
            }
            return best;
        }
    }
}
