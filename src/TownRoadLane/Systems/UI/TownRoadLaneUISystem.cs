using System;
using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.SceneFlow;
using Game.Tools;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TownRoadLane
{
    /// <summary>
    /// Bridge between the in-game React panel and the tool, topology and emission systems.
    ///
    /// Publishes two value bindings, split by update frequency:
    ///   - <c>TownRoadLane.GetPanelState</c>: a <see cref="PanelStateVM"/> snapshot of the
    ///     panel structure (tool state, lines, segments, areas). Pushed only when a content hash
    ///     of the node's buffers changes, so camera movement and idle frames cost neither
    ///     serialization nor a React re-render.
    ///   - <c>TownRoadLane.GetScreenPoints</c>: world-to-screen anchors for the in-world
    ///     popovers. Camera-dependent, so it is recomputed every frame, but gated by its own
    ///     hash. The JS side applies it imperatively (positionRegistry), without re-rendering.
    ///
    /// Commands arrive from React as triggers (see OnCreate). They only edit the node's buffers
    /// and mark it Updated; MarkingTopologySystem and MarkingSegmentEmissionSystem then do
    /// their normal work, so their invariants (PathNode slot allocation, archetype sourcing,
    /// GC protection) hold for UI edits too.
    ///
    /// The UI bundle loads through the game's regular UIModuleAsset pipeline: the .mjs next to
    /// the .dll exports a ModRegistrar that adds the toolbar button (GameTopLeft) and the panel
    /// (GameTopRight).
    /// </summary>
    public partial class TownRoadLaneUISystem : ExtendedUISystemBase
    {
        // Hides UISystemBase.log on purpose: Mod.log is the logger set up for this mod.
        private static new readonly ILog log = Mod.log;

        private ValueBindingHelper<PanelStateVM> _panelState;
        private ValueBindingHelper<SegmentPointVM[]> _screenPoints;
        // Pinned styles for the dropdowns, stored as CSV in the settings.
        private ValueBindingHelper<PinnedStylesVM> _pinnedStyles;
        // Hashes of the last pushed values (see RebuildBindings). Every computed hash starts
        // from the FNV offset, never 0, so the first frame always pushes.
        private ulong _lastStateHash;
        private ulong _lastPointsHash;
        private MarkingNodeToolSystem _tool;
        private DefaultToolSystem _defaultTool;
        private ToolSystem _toolSystem;

        // Line row hovered in the panel (-1 = none); MarkingOverlaySystem highlights that line.
        private int _uiHoveredLineIndex = -1;
        public int UIHoveredLineIndex => _uiHoveredLineIndex;

        // Segment popover under the cursor; the overlay highlights only that segment.
        private int _uiHoveredSegmentLine = -1;
        private int _uiHoveredSegmentIndex = -1;
        public int UIHoveredSegmentLineIndex => _uiHoveredSegmentLine;
        public int UIHoveredSegmentIndex => _uiHoveredSegmentIndex;

        // Area hovered in the panel (row or popover); the overlay outlines all of its pieces.
        private int _uiHoveredAreaIndex = -1;
        public int UIHoveredAreaIndex => _uiHoveredAreaIndex;

        protected override void OnCreate()
        {
            base.OnCreate();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            _panelState = CreateBinding("GetPanelState", new PanelStateVM());
            _screenPoints = CreateBinding("GetScreenPoints", Array.Empty<SegmentPointVM>());
            _pinnedStyles = CreateBinding("GetPinnedStyles", BuildPinnedStylesVM());

            // React picks its string dictionary from this. Evaluated every frame, but the
            // binding only pushes when the value changes.
            CreateBinding("GetLocale", GetActiveLocale);

            CreateTrigger<int, int>("ToggleSegment", OnToggleSegment);
            CreateTrigger<int, int>("SetLineStyle", OnSetLineStyle);
            CreateTrigger<int, int, int>("SetSegmentStyle", OnSetSegmentStyle);
            CreateTrigger<int>("DeleteLine", OnDeleteLine);
            CreateTrigger<int, int>("SetLineCurvature", OnSetLineCurvature);
            CreateTrigger("ToggleVanillaMarkings", OnToggleVanillaMarkings);
            CreateTrigger("ActivateTool", OnActivateTool);
            CreateTrigger<int>("SetCurrentStyle", OnSetCurrentStyle);
            CreateTrigger<int>("SetCurrentAreaStyle", OnSetCurrentAreaStyle);
            CreateTrigger<int>("TogglePinLineStyle", OnTogglePinLineStyle);
            CreateTrigger<int>("TogglePinAreaStyle", OnTogglePinAreaStyle);
            CreateTrigger("ToggleAreaMode", OnToggleAreaMode);
            CreateTrigger<int, int>("SetAreaStyle", OnSetAreaStyle);
            CreateTrigger<int>("ToggleAreaVisible", OnToggleAreaVisible);
            CreateTrigger<int>("DeleteArea", OnDeleteArea);
            CreateTrigger("ResetNode", OnResetNode);
            CreateTrigger<int>("SetHoveredLine", OnSetHoveredLine);
            CreateTrigger<int, int>("SetHoveredSegment", OnSetHoveredSegment);
            CreateTrigger<int>("SetHoveredArea", OnSetHoveredArea);
            CreateTrigger<int>("ClearHoveredArea", OnClearHoveredArea);

            log.Info("TownRoadLaneUISystem: bindings registered");
        }

        protected override void OnUpdate()
        {
            // Stage new values first, then let the base flush them: at most one push per binding
            // per frame, and only on a real change.
            RebuildBindings();
            base.OnUpdate();
        }

        /// <summary>The game's active locale id (e.g. "en-US", "ru-RU"), or en-US while the
        /// localization manager is not ready. React maps unsupported locales to en-US.</summary>
        private static string GetActiveLocale()
        {
            try
            {
                return GameManager.instance?.localizationManager?.activeLocaleId ?? "en-US";
            }
            catch
            {
                return "en-US";
            }
        }

        // FNV-1a 64-bit: a cheap incremental hash for the change gates below.
        private const ulong kFnvOffset = 14695981039346656037UL;
        private const ulong kFnvPrime = 1099511628211UL;

        private static ulong Fold(ulong h, uint v) => (h ^ v) * kFnvPrime;
        private static ulong Fold(ulong h, int v) => Fold(h, unchecked((uint)v));
        private static ulong Fold(ulong h, bool v) => Fold(h, v ? 1u : 0u);
        private static ulong Fold(ulong h, float v) => Fold(h, math.asuint(v));

        /// <summary>Per-frame binding refresh. Hashes every UI-relevant value without allocating,
        /// and builds a new <see cref="PanelStateVM"/> only when the hash changed. Screen anchors
        /// have their own hash, so a camera pan pushes only the points.</summary>
        private void RebuildBindings()
        {
            bool isActive = _toolSystem != null && _tool != null && _toolSystem.activeTool == _tool;
            Entity node = (isActive && _tool.SelectedNode != Entity.Null) ? _tool.SelectedNode : Entity.Null;

            ulong hash = HashPanelState(isActive, node);
            if (hash != _lastStateHash)
            {
                _lastStateHash = hash;
                _panelState.Value = BuildPanelState(isActive, node);
            }

            RefreshScreenPoints(node);
        }

        private ulong HashPanelState(bool isActive, Entity node)
        {
            ulong h = kFnvOffset;
            h = Fold(h, isActive);
            h = Fold(h, isActive ? (int)_tool.ToolState : 0);
            h = Fold(h, isActive ? (_tool.AreaPolygon?.Count ?? 0) : 0);
            h = Fold(h, _tool?.CurrentAreaStyle ?? 0);
            h = Fold(h, node != Entity.Null ? node.Index : -1);
            h = Fold(h, (int)(_tool?.CurrentStyle ?? MarkingStyle.Solid));
            h = Fold(h, node != Entity.Null
                && EntityManager.HasComponent<MarkingOverride>(node)
                && EntityManager.GetComponentData<MarkingOverride>(node).HideAll);
            h = Fold(h, _tool?.LastClickedLine ?? -1);
            h = Fold(h, _tool?.LastClickedTick ?? 0);
            h = Fold(h, _tool?.HoveredLineInGame ?? -1);
            h = Fold(h, _tool?.HoveredAreaInGame ?? -1);

            if (node != Entity.Null && EntityManager.HasBuffer<MarkingLine>(node))
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                h = Fold(h, lines.Length);
                for (int i = 0; i < lines.Length; i++)
                {
                    h = Fold(h, lines[i].style);
                    h = Fold(h, lines[i].curvature);
                }
                if (EntityManager.HasBuffer<MarkingSegment>(node))
                {
                    var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                    h = Fold(h, segs.Length);
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        h = Fold(h, seg.lineIndex);
                        h = Fold(h, seg.tStart);
                        h = Fold(h, seg.tEnd);
                        h = Fold(h, seg.visible);
                        h = Fold(h, seg.style);
                    }
                }
            }

            if (node != Entity.Null && EntityManager.HasBuffer<MarkingArea>(node))
            {
                var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                h = Fold(h, areas.Length);
                for (int a = 0; a < areas.Length; a++)
                {
                    h = Fold(h, areas[a].styleId);
                    h = Fold(h, areas[a].visible);
                    h = Fold(h, areas[a].vertexCount);
                }
                if (EntityManager.HasBuffer<MarkingAreaPiece>(node))
                {
                    var pieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
                    h = Fold(h, pieces.Length);
                    for (int p = 0; p < pieces.Length; p++)
                    {
                        h = Fold(h, pieces[p].areaIndex);
                        h = Fold(h, pieces[p].visible);
                    }
                }
            }

            return h;
        }

        private PanelStateVM BuildPanelState(bool isActive, Entity node)
        {
            var vm = new PanelStateVM
            {
                isActive = isActive,
                toolState = isActive ? (int)_tool.ToolState : 0,
                areaVertexCount = isActive ? (_tool.AreaPolygon?.Count ?? 0) : 0,
                currentAreaStyle = _tool?.CurrentAreaStyle ?? 0,
                selectedNodeIndex = node != Entity.Null ? node.Index : -1,
                currentStyle = (int)(_tool?.CurrentStyle ?? MarkingStyle.Solid),
                vanillaHidden = node != Entity.Null
                    && EntityManager.HasComponent<MarkingOverride>(node)
                    && EntityManager.GetComponentData<MarkingOverride>(node).HideAll,
                // Line or area under the cursor in the world (-1 = none), so React can highlight
                // the matching row.
                lastClickedLine = _tool?.LastClickedLine ?? -1,
                lastClickedTick = _tool?.LastClickedTick ?? 0,
                hoveredLineInGame = _tool?.HoveredLineInGame ?? -1,
                hoveredAreaInGame = _tool?.HoveredAreaInGame ?? -1,
            };

            if (node != Entity.Null && EntityManager.HasBuffer<MarkingLine>(node))
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var segs = EntityManager.HasBuffer<MarkingSegment>(node)
                    ? EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true)
                    : default;

                // Segment length is the arc length of the segment's slice of the line's Bezier.
                var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);

                vm.lines = new LineVM[lines.Length];
                var segScratch = new List<SegmentVM>(16);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    bool bezOk = MarkingCurveBuilder.TryBuild(endpoints, line, out var fullBez);
                    segScratch.Clear();
                    if (segs.IsCreated)
                    {
                        int perLineCounter = 0;
                        for (int s = 0; s < segs.Length; s++)
                        {
                            var seg = segs[s];
                            if (seg.lineIndex != i) continue;
                            float lengthM = 0f;
                            if (bezOk)
                            {
                                var cut = MathUtils.Cut(fullBez, new float2(seg.tStart, seg.tEnd));
                                lengthM = MathUtils.Length(cut);
                            }
                            segScratch.Add(new SegmentVM
                            {
                                lineIndex = i,
                                segmentIndex = perLineCounter,
                                tStart = seg.tStart,
                                tEnd = seg.tEnd,
                                visible = seg.visible,
                                style = seg.style,
                                lengthM = lengthM,
                            });
                            perLineCounter++;
                        }
                    }
                    vm.lines[i] = new LineVM
                    {
                        lineIndex = i,
                        style = line.style,
                        // Integer percent of [0, kMaxPullFactor]; 50% is the 0.4 default pull.
                        curv = (int)math.round(math.saturate(line.curvature / MarkingCurveBuilder.kMaxPullFactor) * 100f),
                        segments = segScratch.ToArray(),
                    };
                }
            }

            // Piece counts come from the topology buffer, so the panel can show how many pieces
            // the lines cut an area into.
            if (node != Entity.Null && EntityManager.HasBuffer<MarkingArea>(node))
            {
                var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                var pieces = EntityManager.HasBuffer<MarkingAreaPiece>(node)
                    ? EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true)
                    : default;
                vm.areas = new AreaVM[areas.Length];
                for (int a = 0; a < areas.Length; a++)
                {
                    var area = areas[a];
                    int pieceCount = 0, visiblePieces = 0;
                    if (pieces.IsCreated)
                    {
                        for (int p = 0; p < pieces.Length; p++)
                        {
                            if (pieces[p].areaIndex != a) continue;
                            pieceCount++;
                            if (pieces[p].visible) visiblePieces++;
                        }
                    }
                    vm.areas[a] = new AreaVM
                    {
                        areaIndex = a,
                        styleId = area.styleId,
                        visible = area.visible,
                        vertexCount = area.vertexCount,
                        pieceCount = pieceCount,
                        visiblePieces = visiblePieces,
                    };
                }
            }

            return vm;
        }

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

            ulong h = kFnvOffset;
            var points = new List<SegmentPointVM>();

            if (hasLines)
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!MarkingCurveBuilder.TryBuild(endpoints, lines[i], out var fullBez)) continue;
                    int perLineCounter = 0;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        if (seg.lineIndex != i) continue;
                        int segmentIndex = perLineCounter++;
                        var midWorld = MathUtils.Position(fullBez, (seg.tStart + seg.tEnd) * 0.5f);
                        var screen = cam.WorldToScreenPoint(midWorld);
                        if (screen.z <= 0f) continue; // behind camera
                        float x = screen.x;
                        float y = screenH - screen.y;
                        float scale = PopoverScale(screen.z);
                        points.Add(new SegmentPointVM
                        {
                            lineIndex = i, segmentIndex = segmentIndex, x = x, y = y, scale = scale,
                        });
                        h = Fold(h, i);
                        h = Fold(h, segmentIndex);
                        // Quantised so sub-pixel camera jitter does not force pushes.
                        h = Fold(h, (int)math.round(x * 10f));
                        h = Fold(h, (int)math.round(y * 10f));
                        h = Fold(h, (int)math.round(scale * 100f));
                    }
                }
            }

            if (hasAreas)
            {
                var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                var verts = EntityManager.GetBuffer<MarkingAreaVertex>(node, isReadOnly: true);
                var corners = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);
                // Crossing vertices (kind 2) resolve through an IReadOnlyList, which
                // DynamicBuffer is not, so the lines are copied.
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
                        lineIndex = -1, segmentIndex = -1, areaIndex = a, x = x, y = y, scale = scale,
                    });
                    h = Fold(h, unchecked((int)0x41524541)); // 'AREA', keeps area points apart from (line, seg) pairs
                    h = Fold(h, a);
                    h = Fold(h, (int)math.round(x * 10f));
                    h = Fold(h, (int)math.round(y * 10f));
                    h = Fold(h, (int)math.round(scale * 100f));
                }
            }

            if (h != _lastPointsHash)
            {
                _lastPointsHash = h;
                _screenPoints.Value = points.ToArray();
            }
        }

        private void ClearScreenPoints()
        {
            if (_lastPointsHash != kFnvOffset)
            {
                _lastPointsHash = kFnvOffset;
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
                if (av.kind == 0)
                {
                    int epIdx = MarkingEndpointExtractor.ResolveEndpointIndex(endpoints, av);
                    if (epIdx < 0) return false;
                    sum += endpoints[epIdx].position;
                }
                else if (av.kind == 1)
                {
                    int cIdx = MarkingEndpointExtractor.ResolveCornerIndex(corners, av);
                    if (cIdx < 0) return false;
                    sum += corners[cIdx].position;
                }
                else if (av.kind == 2) // line crossing: refIndex is the packed (lineA, lineB, hit)
                {
                    if (!MarkingIntersectionExtractor.TryResolve(endpoints, lines, av.refIndex, out var p)) return false;
                    sum += p;
                }
                else return false;
            }
            centroid = sum / area.vertexCount;
            return true;
        }

        // Commands

        /// <summary>Not validated: -1 or any out-of-range index simply highlights nothing.</summary>
        private void OnSetHoveredLine(int lineIndex)
        {
            _uiHoveredLineIndex = lineIndex;
        }

        /// <summary>Pass (-1, -1) to clear.</summary>
        private void OnSetHoveredSegment(int lineIndex, int segmentIndex)
        {
            _uiHoveredSegmentLine = lineIndex;
            _uiHoveredSegmentIndex = segmentIndex;
        }

        private void OnSetHoveredArea(int areaIndex)
        {
            _uiHoveredAreaIndex = areaIndex;
        }

        /// <summary>cohtml can fire mouseenter of the next row before mouseleave of the previous
        /// one, so an unconditional clear on leave would wipe the new hover. Leave passes its own
        /// index and clears only while that index is still the hovered one.</summary>
        private void OnClearHoveredArea(int areaIndex)
        {
            if (_uiHoveredAreaIndex == areaIndex)
                _uiHoveredAreaIndex = -1;
        }

        /// <summary>Drops every UI hover index. Has to run on any structural change (deleting a
        /// line or area, node reset): rows shift under a still cursor and cohtml does not re-fire
        /// mouseenter/leave, so a stale index would highlight a different object than the one
        /// the next click acts on.</summary>
        private void ClearUIHover()
        {
            _uiHoveredLineIndex = -1;
            _uiHoveredSegmentLine = -1;
            _uiHoveredSegmentIndex = -1;
            _uiHoveredAreaIndex = -1;
        }

        /// <summary>Toolbar button: toggles the tool, same as the Ctrl+M hotkey.</summary>
        private void OnActivateTool()
        {
            if (_toolSystem == null || _tool == null) return;
            if (_toolSystem.activeTool == _tool)
            {
                _toolSystem.activeTool = _defaultTool;
                log.Debug("UI: toolbar button deactivated tool");
            }
            else
            {
                _toolSystem.activeTool = _tool;
                log.Debug("UI: toolbar button activated tool");
            }
        }

        private void OnToggleSegment(int lineIndex, int segmentIndexPerLine)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn($"ToggleSegment({lineIndex},{segmentIndexPerLine}) ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingSegment>(node)) return;

            var segs = EntityManager.GetBuffer<MarkingSegment>(node);
            int perLineCounter = 0;
            for (int s = 0; s < segs.Length; s++)
            {
                var seg = segs[s];
                if (seg.lineIndex != lineIndex) continue;
                if (perLineCounter == segmentIndexPerLine)
                {
                    seg.visible = !seg.visible;
                    segs[s] = seg;
                    if (!EntityManager.HasComponent<Updated>(node))
                        EntityManager.AddComponent<Updated>(node);
                    log.Debug($"UI: toggled line#{lineIndex} seg#{segmentIndexPerLine} → visible={seg.visible}");
                    return;
                }
                perLineCounter++;
            }
            log.Warn($"ToggleSegment: line#{lineIndex} seg#{segmentIndexPerLine} not found");
        }

        /// <summary>Overrides the style of one segment. As in <see cref="OnToggleSegment"/>, the
        /// per-line counter maps the UI's segment index to the flat buffer position.</summary>
        private void OnSetSegmentStyle(int lineIndex, int segmentIndexPerLine, int style)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("SetSegmentStyle ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingSegment>(node)) return;

            var segs = EntityManager.GetBuffer<MarkingSegment>(node);
            int perLineCounter = 0;
            for (int s = 0; s < segs.Length; s++)
            {
                var seg = segs[s];
                if (seg.lineIndex != lineIndex) continue;
                if (perLineCounter == segmentIndexPerLine)
                {
                    seg.style = style;
                    segs[s] = seg;
                    if (!EntityManager.HasComponent<Updated>(node))
                        EntityManager.AddComponent<Updated>(node);
                    log.Debug($"UI: set line#{lineIndex} seg#{segmentIndexPerLine} style → {(MarkingStyle)style}");
                    return;
                }
                perLineCounter++;
            }
            log.Warn($"SetSegmentStyle: line#{lineIndex} seg#{segmentIndexPerLine} not found");
        }

        private void OnSetLineStyle(int lineIndex, int style)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("SetLineStyle ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingLine>(node)) return;

            var lines = EntityManager.GetBuffer<MarkingLine>(node);
            if (lineIndex < 0 || lineIndex >= lines.Length) return;
            var ln = lines[lineIndex];
            ln.style = style;
            lines[lineIndex] = ln;
            // Style does not move segment boundaries, so the segments are restyled in place
            // rather than rebuilt.
            if (EntityManager.HasBuffer<MarkingSegment>(node))
            {
                var segs = EntityManager.GetBuffer<MarkingSegment>(node);
                for (int s = 0; s < segs.Length; s++)
                {
                    if (segs[s].lineIndex != lineIndex) continue;
                    var seg = segs[s];
                    seg.style = style;
                    segs[s] = seg;
                }
            }
            // Reset the topology hash: the lines' geometry did not change, so without this
            // MarkingTopologySystem would skip the node and nothing would be re-emitted.
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Debug($"UI: set line#{lineIndex} style → {(MarkingStyle)style}");
        }

        /// <summary>Sets a line's pull factor from the panel stepper; percent 0..100 maps onto
        /// [0, kMaxPullFactor]. The topology hash includes curvature, so marking the node Updated
        /// is enough to re-split the line and redraw it.</summary>
        private void OnSetLineCurvature(int lineIndex, int percent)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("SetLineCurvature ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingLine>(node)) return;

            var lines = EntityManager.GetBuffer<MarkingLine>(node);
            if (lineIndex < 0 || lineIndex >= lines.Length) return;
            var ln = lines[lineIndex];
            ln.curvature = math.saturate(percent / 100f) * MarkingCurveBuilder.kMaxPullFactor;
            lines[lineIndex] = ln;
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Debug($"UI: set line#{lineIndex} curvature → {percent}% (pull={ln.curvature:0.###})");
        }

        /// <summary>Toggles the "hide vanilla markings" override (<see cref="MarkingOverride"/>
        /// with All) on the selected node; CustomSecondaryLaneSystem skips vanilla markings while
        /// it is set. User lines already hide vanilla markings; this switch also works on a node
        /// without any.</summary>
        private void OnToggleVanillaMarkings()
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("ToggleVanillaMarkings ignored — no node selected"); return; }

            bool hidden = EntityManager.HasComponent<MarkingOverride>(node)
                && EntityManager.GetComponentData<MarkingOverride>(node).HideAll;
            if (hidden)
            {
                EntityManager.RemoveComponent<MarkingOverride>(node);
            }
            else if (EntityManager.HasComponent<MarkingOverride>(node))
            {
                EntityManager.SetComponentData(node, new MarkingOverride { hide = MarkingCategory.All });
            }
            else
            {
                EntityManager.AddComponentData(node, new MarkingOverride { hide = MarkingCategory.All });
            }
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Debug($"UI: vanilla markings on node#{node.Index} → {(hidden ? "shown" : "hidden")}");
        }

        private void OnDeleteLine(int lineIndex)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("DeleteLine ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingLine>(node)) return;

            var lines = EntityManager.GetBuffer<MarkingLine>(node);
            if (lineIndex < 0 || lineIndex >= lines.Length) return;
            lines.RemoveAt(lineIndex);
            // Reindex rather than rebuild, so the other lines keep their segment overrides and
            // area anchors follow the shifted indices.
            MarkingTopologySystem.OnLineRemoved(EntityManager, node, lineIndex);
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            ClearUIHover();
            log.Debug($"UI: deleted line#{lineIndex} on node#{node.Index} — segment overrides and area anchors reindexed");
        }

        // Panel counterparts of the Y / U / A hotkeys.

        /// <summary>Style for the next line drawn (the Y hotkey cycles the same value).</summary>
        private void OnSetCurrentStyle(int style)
        {
            _tool?.SetCurrentStyle((MarkingStyle)style);
        }

        /// <summary>Fill style for the next area closed (the U hotkey cycles the same value).</summary>
        private void OnSetCurrentAreaStyle(int styleId)
        {
            _tool?.SetCurrentAreaStyle(styleId);
        }

        // Pinned styles. Apply(), not ApplyAndSave(): each ApplyAndSave is a separate async
        // read-modify-write of the settings file that races with the Options screen's own saves
        // and loses edits. Apply() marks the settings dirty and the coalescing saver in
        // TownRoadLaneSetting writes once.
        private void OnTogglePinLineStyle(int style)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.PinnedLineStylesCsv = ToggleIdInCsv(Mod.Settings.PinnedLineStylesCsv, style);
            Mod.Settings.Apply();
            _pinnedStyles.Value = BuildPinnedStylesVM();
        }

        private void OnTogglePinAreaStyle(int styleId)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.PinnedAreaStylesCsv = ToggleIdInCsv(Mod.Settings.PinnedAreaStylesCsv, styleId);
            Mod.Settings.Apply();
            _pinnedStyles.Value = BuildPinnedStylesVM();
        }

        private static PinnedStylesVM BuildPinnedStylesVM() => new()
        {
            lineStyles = ParseCsv(Mod.Settings?.PinnedLineStylesCsv),
            areaStyles = ParseCsv(Mod.Settings?.PinnedAreaStylesCsv),
        };

        private static int[] ParseCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<int>();
            var parts = csv.Split(',');
            var result = new List<int>(parts.Length);
            foreach (var p in parts)
                if (int.TryParse(p.Trim(), out int v) && !result.Contains(v)) result.Add(v);
            return result.ToArray();
        }

        private static string ToggleIdInCsv(string csv, int id)
        {
            var ids = new List<int>(ParseCsv(csv));
            if (!ids.Remove(id)) ids.Add(id);
            return string.Join(",", ids);
        }

        /// <summary>Switches between NodeSelected and AreaSelecting, like the A hotkey. Leaving
        /// area mode drops an unfinished contour.</summary>
        private void OnToggleAreaMode()
        {
            if (_tool == null) return;
            if (_tool.ToolState == MarkingToolState.AreaSelecting)
                _tool.ExitAreaMode();
            else
                _tool.TryEnterAreaMode();
        }

        /// <summary>Emission compares the prefab every frame and respawns the Area entity when it
        /// changes, so no hash reset is needed here.</summary>
        private void OnSetAreaStyle(int areaIndex, int styleId)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("SetAreaStyle ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingArea>(node)) return;

            var areas = EntityManager.GetBuffer<MarkingArea>(node);
            if (areaIndex < 0 || areaIndex >= areas.Length) return;
            var area = areas[areaIndex];
            area.styleId = styleId;
            areas[areaIndex] = area;
            log.Debug($"UI: set area#{areaIndex} style → {styleId} on node#{node.Index}");
        }

        /// <summary>Pieces keep their own visibility flags, so hiding and showing an area again
        /// restores its previous piece pattern.</summary>
        private void OnToggleAreaVisible(int areaIndex)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("ToggleAreaVisible ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingArea>(node)) return;

            var areas = EntityManager.GetBuffer<MarkingArea>(node);
            if (areaIndex < 0 || areaIndex >= areas.Length) return;
            var area = areas[areaIndex];
            area.visible = !area.visible;
            areas[areaIndex] = area;
            log.Debug($"UI: area#{areaIndex} on node#{node.Index} → visible={area.visible}");
        }

        /// <summary>Removes the area and its vertex slice, shifts the firstVertex offsets of the
        /// areas after it, and forces a piece recompute.</summary>
        private void OnDeleteArea(int areaIndex)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("DeleteArea ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingArea>(node)) return;

            var areas = EntityManager.GetBuffer<MarkingArea>(node);
            if (areaIndex < 0 || areaIndex >= areas.Length) return;
            var removed = areas[areaIndex];

            if (EntityManager.HasBuffer<MarkingAreaVertex>(node) && removed.vertexCount > 0)
            {
                var verts = EntityManager.GetBuffer<MarkingAreaVertex>(node);
                if (removed.firstVertex >= 0 && removed.firstVertex + removed.vertexCount <= verts.Length)
                    verts.RemoveRange(removed.firstVertex, removed.vertexCount);
            }
            areas.RemoveAt(areaIndex);
            for (int a = 0; a < areas.Length; a++)
            {
                var other = areas[a];
                if (other.firstVertex > removed.firstVertex)
                {
                    other.firstVertex -= removed.vertexCount;
                    areas[a] = other;
                }
            }

            // Pieces refer to areas by index, so MarkingAreaTopologySystem has to rebuild them
            // against the shifted list.
            if (EntityManager.HasComponent<MarkingAreaTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            ClearUIHover();
            log.Debug($"UI: deleted area#{areaIndex} on node#{node.Index} ({areas.Length} remaining)");
        }

        /// <summary>Removes every line, segment, area and the vanilla override from the selected
        /// node, restoring the stock markings. Buffers are cleared rather than removed: emission
        /// then sees empty sets and despawns all the mod's lanes and Area entities, and
        /// CustomSecondaryLaneSystem regenerates the vanilla markings.</summary>
        private void OnResetNode()
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("ResetNode ignored — no node selected"); return; }

            if (EntityManager.HasBuffer<MarkingLine>(node))
                EntityManager.GetBuffer<MarkingLine>(node).Clear();
            if (EntityManager.HasBuffer<MarkingSegment>(node))
                EntityManager.GetBuffer<MarkingSegment>(node).Clear();
            if (EntityManager.HasBuffer<MarkingArea>(node))
                EntityManager.GetBuffer<MarkingArea>(node).Clear();
            if (EntityManager.HasBuffer<MarkingAreaVertex>(node))
                EntityManager.GetBuffer<MarkingAreaVertex>(node).Clear();
            if (EntityManager.HasBuffer<MarkingAreaPiece>(node))
                EntityManager.GetBuffer<MarkingAreaPiece>(node).Clear();
            if (EntityManager.HasBuffer<MarkingAreaPieceVertex>(node))
                EntityManager.GetBuffer<MarkingAreaPieceVertex>(node).Clear();
            if (EntityManager.HasComponent<MarkingOverride>(node))
                EntityManager.RemoveComponent<MarkingOverride>(node);
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            if (EntityManager.HasComponent<MarkingAreaTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            ClearUIHover();
            log.Debug($"UI: full reset of node#{node.Index} — lines, areas and vanilla override cleared");
        }
    }

    // Binding payloads. GenericUIWriter serializes field names verbatim, so they are camelCase
    // and must match the interfaces in useToolState.ts and usePinnedStyles.ts.

    /// <summary>Pinned style ids for the UI dropdowns.</summary>
    public class PinnedStylesVM
    {
        public int[] lineStyles = Array.Empty<int>();
        public int[] areaStyles = Array.Empty<int>();
    }

    /// <summary>Everything the panel renders except the camera-dependent popover anchors
    /// (those travel as <see cref="SegmentPointVM"/>).</summary>
    public class PanelStateVM
    {
        public bool isActive;
        public int toolState;
        public int areaVertexCount;
        public int currentAreaStyle;
        public int selectedNodeIndex = -1;
        public int currentStyle;
        public bool vanillaHidden;
        public int lastClickedLine = -1;
        public int lastClickedTick;
        public int hoveredLineInGame = -1;
        public int hoveredAreaInGame = -1;
        public LineVM[] lines = Array.Empty<LineVM>();
        public AreaVM[] areas = Array.Empty<AreaVM>();
    }

    public class LineVM
    {
        public int lineIndex;
        public int style;
        public int curv;
        public SegmentVM[] segments = Array.Empty<SegmentVM>();
    }

    public class SegmentVM
    {
        public int lineIndex;
        public int segmentIndex; // dense per-line counter, stable within one topology pass
        public float tStart;
        public float tEnd;
        public bool visible;
        public int style;
        public float lengthM;
    }

    public class AreaVM
    {
        public int areaIndex;
        public int styleId;
        public bool visible;
        public int vertexCount;
        public int pieceCount;
        public int visiblePieces;
    }

    /// <summary>Screen anchor of one in-world popover (CSS px, origin top-left). Segment
    /// anchors carry (lineIndex, segmentIndex); area anchors carry areaIndex with the segment
    /// fields at -1. A popover without an anchor is hidden.</summary>
    public class SegmentPointVM
    {
        public int lineIndex;
        public int segmentIndex;
        public int areaIndex = -1;
        public float x;
        public float y;
        // Camera-distance popover scale in [0.65, 1], see PopoverScale.
        public float scale = 1f;
    }
}
