using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.Topology;
using TownRoadLane.Utilities;

namespace TownRoadLane.Systems.Tool
{
    // Area mode: placing the vertices of an area polygon and committing it to the node.
    public partial class MarkingNodeToolSystem
    {
        /// <summary>Panel "Area" button. Drops a half-picked line first, so it works from any
        /// state with a selected node; returns false in Default.</summary>
        public bool TryEnterAreaMode()
        {
            if (_state == MarkingToolState.SourceSelected)
                DropSource();
            if (_state != MarkingToolState.NodeSelected) return false;
            EnterAreaSelecting();
            log.Debug($"area: entered AreaSelecting via UI on node #{_selectedNode.Index}");
            return true;
        }

        /// <summary>Panel "Lines" button or area-mode cancel: back to NodeSelected, dropping any
        /// placed vertices.</summary>
        public void ExitAreaMode()
        {
            if (_state != MarkingToolState.AreaSelecting) return;
            log.Debug($"area: exited AreaSelecting via UI (had {_areaPolygon.Count} vertices)");
            LeaveAreaSelecting();
        }

        private void EnterAreaSelecting()
        {
            _state = MarkingToolState.AreaSelecting;
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
        }

        /// <summary>Back to NodeSelected, dropping any placed vertices. Callers log first, while
        /// the vertex count is still known.</summary>
        private void LeaveAreaSelecting()
        {
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _state = MarkingToolState.NodeSelected;
        }

        /// <summary>World position of an area anchor, resolved from its kind and refIndex.</summary>
        public bool TryGetAreaAnchorPos(AreaCandidate c, out float3 pos)
        {
            pos = float3.zero;
            if (!c.IsValid) return false;
            if (c.kind == AreaAnchorKind.LaneEndpoint)
            {
                if (c.refIndex >= _endpoints.Count) return false;
                pos = _endpoints[c.refIndex].position;
                return true;
            }
            if (c.kind == AreaAnchorKind.NodeCorner)
            {
                if (c.refIndex >= _cornerAnchors.Count) return false;
                pos = _cornerAnchors[c.refIndex].position;
                return true;
            }
            // LineIntersection: refIndex is the packed reference, not a list index.
            int i = IndexOfIntersection(c.refIndex);
            if (i < 0) return false;
            pos = _lineIntersections[i].position;
            return true;
        }

        /// <summary>Index into <see cref="_lineIntersections"/> of the crossing with this packed
        /// reference, or -1.</summary>
        private int IndexOfIntersection(int packedRef)
        {
            for (int i = 0; i < _lineIntersections.Count; i++)
            {
                if (_lineIntersections[i].PackedRef == packedRef) return i;
            }
            return -1;
        }

        private static AreaCandidate ToCandidate(AreaPolygonVertex v)
            => new AreaCandidate { kind = v.kind, refIndex = v.refIndex };

        /// <summary>Line topology hash of the selected node, or 0 when it has none.</summary>
        private int SelectedNodeLinesHash()
        {
            return _selectedNode != Entity.Null && EntityManager.HasComponent<MarkingTopologyState>(_selectedNode)
                ? EntityManager.GetComponentData<MarkingTopologyState>(_selectedNode).linesHash
                : 0;
        }

        /// <summary>Re-extracts the line-crossing anchors and remembers the topology hash they
        /// were built from.</summary>
        private void RefreshIntersectionAnchors()
        {
            _lineIntersections = MarkingIntersectionExtractor.ExtractAll(EntityManager, _selectedNode);
            _lineIntersectionsHash = SelectedNodeLinesHash();
        }

        /// <summary>Per-frame check in area mode. When the line topology hash changes (a line
        /// added or deleted, curvature edited), re-extracts the crossings and refreshes the cached
        /// positions of placed vertices, so the contour follows the lines.</summary>
        private void RefreshIntersectionAnchorsIfStale()
        {
            if (_selectedNode == Entity.Null) return;
            if (SelectedNodeLinesHash() == _lineIntersectionsHash) return;
            RefreshIntersectionAnchors();
            for (int i = 0; i < _areaPolygon.Count; i++)
            {
                var pv = _areaPolygon[i];
                if (TryGetAreaAnchorPos(ToCandidate(pv), out var pos))
                {
                    pv.position = pos;
                    _areaPolygon[i] = pv;
                }
            }
        }

        /// <summary>Edge kind between two anchors. As in IMT, if both lie on the same MarkingLine
        /// (its endpoints, or a crossing that involves it), the edge follows that line's curve;
        /// otherwise it is a straight chord.</summary>
        private AreaEdgeKind ClassifyEdge(AreaCandidate from, AreaCandidate to)
        {
            if (from.kind == AreaAnchorKind.NodeCorner || to.kind == AreaAnchorKind.NodeCorner)
                return AreaEdgeKind.Straight;
            if (_selectedNode == Entity.Null) return AreaEdgeKind.Straight;
            if (!EntityManager.HasBuffer<MarkingLine>(_selectedNode)) return AreaEdgeKind.Straight;

            var lines = EntityManager.GetBuffer<MarkingLine>(_selectedNode, isReadOnly: true);
            for (int i = 0; i < lines.Length; i++)
            {
                if (AnchorLiesOnLine(from, lines[i], i) && AnchorLiesOnLine(to, lines[i], i))
                    return AreaEdgeKind.LineBezier;
            }
            return AreaEdgeKind.Straight;
        }

        /// <summary>True when the anchor is the line's source or target endpoint, or a crossing
        /// that involves the line.</summary>
        private bool AnchorLiesOnLine(AreaCandidate c, MarkingLine ln, int lineIndex)
        {
            if (c.kind == AreaAnchorKind.LaneEndpoint)
            {
                if (c.refIndex >= _endpoints.Count) return false;
                var ep = _endpoints[c.refIndex];
                return LineStartsAt(ln, ep) || LineEndsAt(ln, ep);
            }
            if (c.kind == AreaAnchorKind.LineIntersection)
            {
                MarkingIntersectionExtractor.Unpack(c.refIndex, out int a, out int b, out _);
                return lineIndex == a || lineIndex == b;
            }
            return false;
        }

        /// <summary>The placed vertices as an open polyline for the overlay, with LineBezier edges
        /// sampled by MarkingAreaTopologySystem.SampleCurvedEdge, so the preview matches the
        /// committed area exactly. The closing edge is left out: its kind is only known once the
        /// closing click classifies it.</summary>
        public void BuildAreaContourPath(List<float3> into)
        {
            into.Clear();
            for (int i = 0; i < _areaPolygon.Count; i++)
            {
                var pv = _areaPolygon[i];
                into.Add(pv.position);
                if (i + 1 >= _areaPolygon.Count) break;
                if (pv.edgeToNext == AreaEdgeKind.LineBezier)
                    AppendEdgeSamples(pv, _areaPolygon[i + 1], into);
            }
        }

        /// <summary>Appends interior samples of the shared line between two placed vertices.
        /// Appends nothing (a straight chord) when the line can no longer be found or built.</summary>
        private void AppendEdgeSamples(AreaPolygonVertex from, AreaPolygonVertex to, List<float3> into)
        {
            if (_selectedNode == Entity.Null || !EntityManager.HasBuffer<MarkingLine>(_selectedNode)) return;
            var lines = EntityManager.GetBuffer<MarkingLine>(_selectedNode, isReadOnly: true);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryDraftAnchorParamOnLine(from.kind, from.refIndex, lines[i], i, out float tFrom)) continue;
                if (!TryDraftAnchorParamOnLine(to.kind, to.refIndex, lines[i], i, out float tTo)) continue;
                if (math.abs(tTo - tFrom) < 1e-4f) continue;
                if (!MarkingCurveBuilder.TryBuild(_endpoints, lines[i], out var bez)) continue;
                MarkingAreaTopologySystem.SampleCurvedEdge(bez, tFrom, tTo, into);
                return;
            }
        }

        /// <summary>Curve parameter t of a placed anchor on the given line: 0 or 1 for the line's
        /// source or target endpoint, the crossing's own t for a crossing.</summary>
        private bool TryDraftAnchorParamOnLine(AreaAnchorKind kind, int refIndex, MarkingLine ln, int lineIndex, out float t)
        {
            t = 0f;
            if (kind == AreaAnchorKind.LaneEndpoint)
            {
                if (refIndex < 0 || refIndex >= _endpoints.Count) return false;
                var ep = _endpoints[refIndex];
                if (LineStartsAt(ln, ep)) { t = 0f; return true; }
                if (LineEndsAt(ln, ep)) { t = 1f; return true; }
                return false;
            }
            if (kind == AreaAnchorKind.LineIntersection)
            {
                int i = IndexOfIntersection(refIndex);
                if (i < 0) return false;
                var x = _lineIntersections[i];
                if (x.lineA == lineIndex) { t = x.tA; return true; }
                if (x.lineB == lineIndex) { t = x.tB; return true; }
            }
            return false;
        }

        /// <summary>Adds a vertex and resolves the previous vertex's edgeToNext, now that the
        /// anchor it connects to is known.</summary>
        private void AreaAddVertex(AreaCandidate c)
        {
            if (!c.IsValid) return;
            if (!TryGetAreaAnchorPos(c, out var pos)) return;

            if (_areaPolygon.Count > 0)
            {
                var prev = _areaPolygon[_areaPolygon.Count - 1];
                prev.edgeToNext = ClassifyEdge(ToCandidate(prev), c);
                _areaPolygon[_areaPolygon.Count - 1] = prev;
            }

            _areaPolygon.Add(new AreaPolygonVertex
            {
                kind = c.kind,
                refIndex = c.refIndex,
                position = pos,
                edgeToNext = AreaEdgeKind.Straight,  // placeholder until the next click or closure
            });
            log.Debug($"area: vertex {_areaPolygon.Count} added (kind={c.kind}, ref={c.refIndex})");
        }

        /// <summary>Right-click: removes the last vertex, or leaves area mode when there is none.</summary>
        private void AreaUndoVertex()
        {
            if (_areaPolygon.Count > 0)
            {
                _areaPolygon.RemoveAt(_areaPolygon.Count - 1);
                log.Debug($"area: popped last vertex, {_areaPolygon.Count} remaining");
            }
            else
            {
                log.Debug("area: RMB on empty contour → leave AreaSelecting");
                LeaveAreaSelecting();
            }
        }

        /// <summary>True when there are 3+ vertices and the candidate is the first one.</summary>
        private bool AreaCanCloseOn(AreaCandidate c)
        {
            if (_areaPolygon.Count < 3 || !c.IsValid) return false;
            var first = _areaPolygon[0];
            return first.kind == c.kind && first.refIndex == c.refIndex;
        }

        /// <summary>Closes the polygon and commits it to the node's <see cref="MarkingArea"/> and
        /// <see cref="MarkingAreaVertex"/> buffers. <c>MarkingAreaEmissionSystem</c> picks it up
        /// next frame and spawns the vanilla Area entity.</summary>
        private void AreaClose()
        {
            // Closing edge, from the last vertex back to the first.
            var last = _areaPolygon[_areaPolygon.Count - 1];
            last.edgeToNext = ClassifyEdge(ToCandidate(last), ToCandidate(_areaPolygon[0]));
            _areaPolygon[_areaPolygon.Count - 1] = last;

            // Buffers are added on demand; most nodes never get an area.
            if (!EntityManager.HasBuffer<MarkingArea>(_selectedNode))
                EntityManager.AddBuffer<MarkingArea>(_selectedNode);
            if (!EntityManager.HasBuffer<MarkingAreaVertex>(_selectedNode))
                EntityManager.AddBuffer<MarkingAreaVertex>(_selectedNode);

            var areas = EntityManager.GetBuffer<MarkingArea>(_selectedNode);
            var verts = EntityManager.GetBuffer<MarkingAreaVertex>(_selectedNode);
            int firstVertex = verts.Length;
            for (int i = 0; i < _areaPolygon.Count; i++)
                verts.Add(ToStoredVertex(_areaPolygon[i]));
            areas.Add(new MarkingArea
            {
                styleId = _currentAreaStyle,
                visible = true,
                firstVertex = firstVertex,
                vertexCount = _areaPolygon.Count,
            });

            // Updated makes MarkingAreaEmissionSystem pick up the change next frame.
            EntityManager.MarkUpdated(_selectedNode);

            log.Debug($"area: closed with {_areaPolygon.Count} vertices on node #{_selectedNode.Index} — buffer now has {areas.Length} area(s)");
            LeaveAreaSelecting();
        }

        /// <summary>The saved form of a placed vertex.</summary>
        private MarkingAreaVertex ToStoredVertex(AreaPolygonVertex pv)
        {
            var av = new MarkingAreaVertex
            {
                kind = pv.kind,
                refIndex = pv.refIndex,
                edgeToNext = pv.edgeToNext,
                refPos = pv.position,
            };
            // List indexes don't survive save/load (the lane rebuild reorders extraction), so
            // the vertex also stores the edge/gap keys MarkingLine uses.
            if (pv.kind == AreaAnchorKind.LaneEndpoint && pv.refIndex >= 0 && pv.refIndex < _endpoints.Count)
            {
                av.refEdgeA = _endpoints[pv.refIndex].edge;
                av.refGap = _endpoints[pv.refIndex].gapIndex;
            }
            else if (pv.kind == AreaAnchorKind.NodeCorner && pv.refIndex >= 0 && pv.refIndex < _cornerAnchors.Count)
            {
                av.refEdgeA = _cornerAnchors[pv.refIndex].edgeA;
                av.refEdgeB = _cornerAnchors[pv.refIndex].edgeB;
            }
            return av;
        }
    }
}
