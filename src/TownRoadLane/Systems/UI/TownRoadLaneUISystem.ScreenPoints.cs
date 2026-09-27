using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Utilities;

namespace TownRoadLane.Systems.UI
{
    public partial class TownRoadLaneUISystem
    {
        /// <summary>Screen anchors for the in-world popovers (segment midpoints and area
        /// centroids). Anchors behind the camera are omitted; the JS positionRegistry hides a
        /// popover whose key got no point. Positions are hashed at 0.1 px and scale at 0.01, so
        /// a static camera pushes nothing.</summary>
        private void RefreshScreenPoints(Entity node)
        {
            var cam = Camera.main;
            if (node == Entity.Null || cam == null)
            {
                ClearScreenPoints();
                return;
            }

            bool hasLines = EntityManager.HasBuffer<MarkingLine>(node)
                            && EntityManager.HasBuffer<MarkingSegment>(node);
            bool hasAreas = EntityManager.HasBuffer<MarkingArea>(node)
                            && EntityManager.HasBuffer<MarkingAreaVertex>(node)
                            && EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true).Length > 0;
            if (!hasLines && !hasAreas)
            {
                ClearScreenPoints();
                return;
            }

            var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
            int screenH = Screen.height; // Unity screen Y is bottom-up, CSS is top-down

            var hash = Fnv1a.Create();
            var points = new List<SegmentPointVM>();

            if (hasLines)
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!MarkingCurveBuilder.TryBuild(endpoints, lines[i], out var fullBez)) continue;
                    // Counts segments behind the camera too, so the index matches the panel's.
                    int indexInLine = 0;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        if (seg.lineIndex != i) continue;
                        int segmentIndex = indexInLine++;
                        var midWorld = MathUtils.Position(fullBez, (seg.tStart + seg.tEnd) * 0.5f);
                        var screen = cam.WorldToScreenPoint(midWorld);
                        if (screen.z <= 0f) continue; // behind camera
                        float x = screen.x;
                        float y = screenH - screen.y;
                        float scale = PopoverScale(screen.z);
                        points.Add(new SegmentPointVM
                        {
                            lineIndex = i,
                            segmentIndex = segmentIndex,
                            x = x,
                            y = y,
                            scale = scale,
                        });
                        hash.Add(i);
                        hash.Add(segmentIndex);
                        // Quantised so sub-pixel camera jitter does not force pushes.
                        hash.Add((int)math.round(x * 10f));
                        hash.Add((int)math.round(y * 10f));
                        hash.Add((int)math.round(scale * 100f));
                    }
                }
            }

            if (hasAreas)
            {
                var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                var verts = EntityManager.GetBuffer<MarkingAreaVertex>(node, isReadOnly: true);
                var corners = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);
                // Crossing vertices resolve through an IReadOnlyList, which DynamicBuffer is not,
                // so the lines are copied.
                var linesSnap = Array.Empty<MarkingLine>();
                if (EntityManager.HasBuffer<MarkingLine>(node))
                {
                    var lb = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                    linesSnap = new MarkingLine[lb.Length];
                    for (int i = 0; i < lb.Length; i++) linesSnap[i] = lb[i];
                }
                for (int a = 0; a < areas.Length; a++)
                {
                    if (!TryAreaAnchor(areas[a], verts, endpoints, corners, linesSnap, out var centroid)) continue;
                    var screen = cam.WorldToScreenPoint(centroid);
                    if (screen.z <= 0f) continue;
                    float x = screen.x;
                    float y = screenH - screen.y;
                    float scale = PopoverScale(screen.z);
                    points.Add(new SegmentPointVM
                    {
                        lineIndex = -1,
                        segmentIndex = -1,
                        areaIndex = a,
                        x = x,
                        y = y,
                        scale = scale,
                    });
                    hash.Add(0x41524541u); // 'AREA', keeps area points apart from (line, seg) pairs
                    hash.Add(a);
                    hash.Add((int)math.round(x * 10f));
                    hash.Add((int)math.round(y * 10f));
                    hash.Add((int)math.round(scale * 100f));
                }
            }

            if (hash.Value != _lastPointsHash)
            {
                _lastPointsHash = hash.Value;
                _screenPoints.Value = points.ToArray();
            }
        }

        private void ClearScreenPoints()
        {
            // An empty hash doubles as the "nothing published" marker, so repeated clears push once.
            if (_lastPointsHash != Fnv1a.kOffset)
            {
                _lastPointsHash = Fnv1a.kOffset;
                _screenPoints.Value = Array.Empty<SegmentPointVM>();
            }
        }

        /// <summary>Popover scale by camera distance (the z of WorldToScreenPoint): full size up
        /// close, shrinking down to 0.65 as the camera pulls away so a zoomed-out view is not
        /// covered in popovers. The JS side restores 1 while a popover is hover-expanded.</summary>
        private static float PopoverScale(float screenZ)
            => math.clamp(math.sqrt(120f / math.max(screenZ, 1f)), 0.65f, 1f);

        /// <summary>Popover anchor for one area: the average of its resolved vertices, which for
        /// the small, roughly convex contours drawn at a junction is as good as the true
        /// centroid. Resolves vertices the same way as MarkingAreaTopologySystem.ResolveOuterRing.
        /// False when any vertex fails to resolve; topology will clean such an area up.</summary>
        private static bool TryAreaAnchor(MarkingArea area, DynamicBuffer<MarkingAreaVertex> verts,
                                          List<MarkingEndpoint> endpoints, List<MarkingCornerAnchor> corners,
                                          MarkingLine[] lines, out float3 centroid)
        {
            centroid = default;
            if (area.vertexCount <= 0) return false;
            float3 sum = float3.zero;
            for (int v = 0; v < area.vertexCount; v++)
            {
                int idx = area.firstVertex + v;
                if (idx < 0 || idx >= verts.Length) return false;
                var av = verts[idx];
                switch (av.kind)
                {
                    case AreaAnchorKind.LaneEndpoint:
                        int epIdx = MarkingEndpointExtractor.ResolveEndpointIndex(endpoints, av);
                        if (epIdx < 0) return false;
                        sum += endpoints[epIdx].position;
                        break;
                    case AreaAnchorKind.NodeCorner:
                        int cIdx = MarkingEndpointExtractor.ResolveCornerIndex(corners, av);
                        if (cIdx < 0) return false;
                        sum += corners[cIdx].position;
                        break;
                    case AreaAnchorKind.LineIntersection: // refIndex is the packed (lineA, lineB, hit)
                        if (!MarkingIntersectionExtractor.TryResolve(endpoints, lines, av.refIndex, out var p)) return false;
                        sum += p;
                        break;
                    default:
                        return false;
                }
            }
            centroid = sum / area.vertexCount;
            return true;
        }
    }
}
