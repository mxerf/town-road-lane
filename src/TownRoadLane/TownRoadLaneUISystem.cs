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
    /// Stage 5d bridge between the in-game React panel and the C# tool / topology / emission stack.
    ///
    /// Publishes two value bindings, split by update frequency (Stage 5e rework):
    ///   - <c>TownRoadLane.GetPanelState</c> — typed <see cref="PanelStateVM"/> snapshot of the
    ///     panel structure (tool state, lines, segments, areas). Rebuilt + pushed ONLY when a
    ///     content hash of the authoritative buffers changes, so camera movement and idle frames
    ///     cost neither serialization nor a React re-render.
    ///   - <c>TownRoadLane.GetScreenPoints</c> — world→screen anchors for the per-segment
    ///     popovers. Camera-dependent, so it refreshes every tick while the tool is active
    ///     (gated by its own hash — a static camera pushes nothing). Consumed on the JS side
    ///     imperatively (positionRegistry), bypassing React re-render entirely.
    ///
    /// Commands arrive from React as TriggerBindings (see OnCreate). All structural changes mark
    /// the node Updated so the next-tick recompute + emission picks them up. None of these
    /// commands bypass the existing pipelines — they just edit the authoritative buffers, then
    /// let MarkingTopologySystem + MarkingSegmentEmissionSystem do their normal jobs. This means
    /// the same invariants (PathNode slot allocation, archetype sourcing, GC protection) hold
    /// automatically.
    ///
    /// UI bundle loading: handled by the game's normal UIModuleAsset pipeline. Our .mjs ships
    /// next to the .dll, exports a default ModRegistrar that appends components into
    /// GameTopLeft (toolbar button) + GameTopRight (panel) slots. No ExecuteScript hack
    /// needed — that was carried over from SystemTimeMod and caused a NullReferenceException
    /// in UIModuleAsset.PostCreate when AssetDatabase tried to register tags from an empty
    /// mod.json manifest.
    /// </summary>
    public partial class TownRoadLaneUISystem : ExtendedUISystemBase
    {
        // Shadowing the inherited UISystemBase.log on purpose — Mod.log is the one wired into
        // CS2's mod-aware logger (file name, prefix) and matches the rest of the code in this
        // project. Using `new` to silence CS0108.
        private static new readonly ILog log = Mod.log;

        private ValueBindingHelper<PanelStateVM> _panelState;
        private ValueBindingHelper<SegmentPointVM[]> _screenPoints;
        // Pinned "favourite" styles for the dropdowns — persisted as CSV in settings, pushed
        // to React as int arrays. Toggled by the pin buttons on dropdown options.
        private ValueBindingHelper<PinnedStylesVM> _pinnedStyles;
        // Content hashes gating the pushes above — see RebuildBindings. 0 = "nothing published
        // yet"; both start at the FNV offset so an all-default state still differs and pushes once.
        private ulong _lastStateHash;
        private ulong _lastPointsHash;
        private MarkingNodeToolSystem _tool;
        private DefaultToolSystem _defaultTool;
        private ToolSystem _toolSystem;

        // Stage 5d hover-bridge: which line is currently hovered in the React panel. -1 = none.
        // Read by MarkingOverlaySystem to draw that line thicker/brighter so the user can
        // visually correlate UI row ↔ on-road line. Republished into the state JSON so React
        // can be the source of truth (single dispatcher) and overlay just reads it back.
        private int _uiHoveredLineIndex = -1;
        public int UIHoveredLineIndex => _uiHoveredLineIndex;

        // Phase C3: per-segment hover. Set by React when the cursor is over a segment popover.
        // When >= 0, MarkingOverlaySystem highlights only this specific segment (brighter
        // than the rest of its line) so popover hover correlates with a single in-world
        // segment rather than the whole line.
        private int _uiHoveredSegmentLine = -1;
        private int _uiHoveredSegmentIndex = -1;
        public int UIHoveredSegmentLineIndex => _uiHoveredSegmentLine;
        public int UIHoveredSegmentIndex => _uiHoveredSegmentIndex;

        // Phase 7c: which AREA is hovered in the React panel (row or its world popover).
        // Read by MarkingOverlaySystem to outline every piece of that area — the area
        // counterpart of _uiHoveredLineIndex.
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

            // i18n locale binding — React reads this to pick which dictionary
            // (en-US, ru-RU, ...) to render strings from. Re-evaluated every
            // tick; the binding only fires when the value actually changes, so
            // this is cheap. Falls back to en-US if the locale manager isn't
            // initialized yet (early load / unit-test contexts).
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
            // Recompute hashes + stage new values on the dirty-buffered helpers, THEN let the
            // base flush them — one binding push per frame max, and only on real change.
            RebuildBindings();
            base.OnUpdate();
        }

        /// <summary>Pull the current game locale id from CS2's localization manager. Returns the
        /// BCP-47-ish code the game uses (e.g. "en-US", "ru-RU"). React resolves unsupported
        /// locales to en-US via i18n.resolveLocale, so we don't need to translate here.</summary>
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

        // --- State publishing ---

        // FNV-1a 64-bit — cheap incremental hash for the change gates below.
        private const ulong kFnvOffset = 14695981039346656037UL;
        private const ulong kFnvPrime = 1099511628211UL;

        private static ulong Fold(ulong h, uint v) => (h ^ v) * kFnvPrime;
        private static ulong Fold(ulong h, int v) => Fold(h, unchecked((uint)v));
        private static ulong Fold(ulong h, bool v) => Fold(h, v ? 1u : 0u);
        private static ulong Fold(ulong h, float v) => Fold(h, math.asuint(v));

        /// <summary>Per-frame binding refresh. Pass 1 folds every UI-relevant scalar into a
        /// content hash (zero allocation); only when the hash moved does pass 2 allocate and
        /// stage a fresh <see cref="PanelStateVM"/>. Screen anchors are camera-dependent, so
        /// they get their own hash + push cadence (camera pans push points, nothing else).</summary>
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
                // Game→UI hover bridge (Phase B5): which line the cursor is currently hovering
                // in the world; React highlights the matching row so the panel ↔ world
                // correlation works both ways. -1 = nothing hovered.
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

                // Per-line Beziers so segment length comes out right (segment length is the
                // arc length of the cut-out Bezier slice, in metres).
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
                        // Curvature exposed to the UI as an integer percent of the stepper range
                        // [0, kMaxPullFactor] — 50% = the 0.4 default pull.
                        curv = (int)math.round(math.saturate(line.curvature / MarkingCurveBuilder.kMaxPullFactor) * 100f),
                        segments = segScratch.ToArray(),
                    };
                }
            }

            // Areas list — one entry per user-closed polygon on the selected node. Piece counts
            // come from the topology buffer so the panel can show "K piece(s)" when lines cut
            // the area apart. Style + visibility mirror the MarkingArea buffer directly.
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

        /// <summary>World→screen anchors for the in-world popovers (segment midpoints + area
        /// centroids), refreshed every tick while a node is selected. Off-screen / behind-camera
        /// anchors are simply omitted — the JS positionRegistry hides popovers whose key received
        /// no point this sync. The hash gate (positions quantised to 0.1 px, scale to 0.01) keeps
        /// a static camera from pushing anything.</summary>
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
                        // Midpoint world position of the segment — anchor for the popover.
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
                        // Quantise to 0.1 px so sub-pixel camera jitter doesn't force pushes.
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
                // Snapshot lines for intersection-vertex resolve (kind 2) — TryResolve wants an
                // IReadOnlyList, which DynamicBuffer isn't.
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
                    h = Fold(h, unchecked((int)0x41524541)); // 'AREA' — keys area points apart from (line, seg) pairs
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

        /// <summary>Camera-distance scale for the in-world popovers: full authored size while the
        /// camera is near the intersection, shrinking gently (floor 0.65) as it pulls away so a
        /// zoomed-out view isn't wallpapered with full-size chrome. Never grows above 1 — the JS
        /// side clamps back UP to 1 while a popover is hover-expanded. screenZ is the world-unit
        /// distance WorldToScreenPoint returned in its z component.</summary>
        private static float PopoverScale(float screenZ)
            => math.clamp(math.sqrt(120f / math.max(screenZ, 1f)), 0.65f, 1f);

        /// <summary>Popover anchor for one area: the average of its resolved vertex positions.
        /// (Not the true polygon centroid — for the small convex-ish contours users draw at an
        /// intersection the vertex average is indistinguishable and much cheaper.) Mirrors
        /// MarkingAreaTopologySystem.ResolveOuterRing's lookup; false when any vertex fails to
        /// resolve (line removed, road demolished — topology will clean the area up shortly).</summary>
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
                else if (av.kind == 2) // line crossing — refIndex is the packed (lineA, lineB, hit)
                {
                    if (!MarkingIntersectionExtractor.TryResolve(endpoints, lines, av.refIndex, out var p)) return false;
                    sum += p;
                }
                else return false;
            }
            centroid = sum / area.vertexCount;
            return true;
        }

        // --- Commands ---

        /// <summary>UI hover-bridge: React notifies us which line row the user is hovering over
        /// in the panel. Stored as plain int — no validation; -1 (or any out-of-range value)
        /// means "no hover" and overlay falls back to normal rendering. Cheap, no buffer needed.</summary>
        private void OnSetHoveredLine(int lineIndex)
        {
            _uiHoveredLineIndex = lineIndex;
        }

        /// <summary>Phase C3 — per-segment hover bridge. React calls this when the cursor enters
        /// a segment popover, so we can highlight only that segment in the overlay (rather than
        /// the whole line). Pass (-1, -1) to clear.</summary>
        private void OnSetHoveredSegment(int lineIndex, int segmentIndex)
        {
            _uiHoveredSegmentLine = lineIndex;
            _uiHoveredSegmentIndex = segmentIndex;
        }

        /// <summary>Phase 7c — area hover bridge. React calls this from the area row and the
        /// area popover; the overlay outlines every piece of that area.</summary>
        private void OnSetHoveredArea(int areaIndex)
        {
            _uiHoveredAreaIndex = areaIndex;
        }

        /// <summary>Race-safe hover clear: cohtml can fire mouseenter of the NEXT row before
        /// mouseleave of the previous one — an unconditional "-1" on leave would then wipe the
        /// fresh hover. Leave passes its OWN index and only clears while it still owns it.</summary>
        private void OnClearHoveredArea(int areaIndex)
        {
            if (_uiHoveredAreaIndex == areaIndex)
                _uiHoveredAreaIndex = -1;
        }

        /// <summary>Drop every UI-driven hover index. Must run on ANY structural mutation
        /// (delete line/area, node reset): rows shift under a stationary cursor and cohtml does
        /// not re-fire mouseenter/leave for the reshuffled rows, so a stale index would keep
        /// outlining — and visually "selecting" — a DIFFERENT object than the one the next
        /// click acts on (7c bug report: hover shows one area, delete removes another).</summary>
        private void ClearUIHover()
        {
            _uiHoveredLineIndex = -1;
            _uiHoveredSegmentLine = -1;
            _uiHoveredSegmentIndex = -1;
            _uiHoveredAreaIndex = -1;
        }

        /// <summary>Toolbar-button command: toggle our tool active/inactive. Same semantics as
        /// the Ctrl+M hotkey path — flip activeTool between ours and DefaultToolSystem.</summary>
        private void OnActivateTool()
        {
            if (_toolSystem == null || _tool == null) return;
            if (_toolSystem.activeTool == _tool)
            {
                _toolSystem.activeTool = _defaultTool;
                log.Info("UI: toolbar button deactivated tool");
            }
            else
            {
                _toolSystem.activeTool = _tool;
                log.Info("UI: toolbar button activated tool");
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
                    log.Info($"UI: toggled line#{lineIndex} seg#{segmentIndexPerLine} → visible={seg.visible}");
                    return;
                }
                perLineCounter++;
            }
            log.Warn($"ToggleSegment: line#{lineIndex} seg#{segmentIndexPerLine} not found");
        }

        /// <summary>Override the style of a single segment. Same walk-and-count strategy as
        /// <see cref="OnToggleSegment"/> — the per-line counter maps the React-side segmentIndex
        /// to the flat buffer position. Other segments of the line keep their previous style.</summary>
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
                    log.Info($"UI: set line#{lineIndex} seg#{segmentIndexPerLine} style → {(MarkingStyle)style}");
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
            // Sweep every existing segment of this line over to the new style. Topology won't
            // re-split here (boundaries are unaffected by style), so we don't wipe segments;
            // we just rewrite the per-segment style field in-place. Emission picks up the new
            // value next tick via the Updated marker below.
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
            // Bust the topology hash so MarkingTopologySystem re-emits on next tick. Without
            // this the hash equality short-circuits because lineIndex+endpoints didn't change.
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Info($"UI: set line#{lineIndex} style → {(MarkingStyle)style}");
        }

        /// <summary>Set the Bezier pull factor of one line from the panel stepper. Percent is
        /// the UI-side 0..100 value mapped onto [0, kMaxPullFactor] (50% = the 0.4 default).
        /// The topology hash includes curvature, so marking the node Updated is enough — the
        /// next tick re-splits intersections against the new curve and the vanilla-side sublane
        /// wipe + emission respawn redraws the decals.</summary>
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
            log.Info($"UI: set line#{lineIndex} curvature → {percent}% (pull={ln.curvature:0.###})");
        }

        /// <summary>Toggle the "hide vanilla markings" override on the selected node. Sets or
        /// removes <see cref="MarkingOverride"/>{All}; CustomSecondaryLaneSystem reads it and
        /// skips (or resumes) vanilla marking generation on the next rebuild. Works with zero
        /// user lines drawn — this is the standalone hide switch. Note: a node with user lines
        /// already suppresses vanilla markings implicitly; the override simply makes that state
        /// explicit and independent of the lines.</summary>
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
            log.Info($"UI: vanilla markings on node#{node.Index} → {(hidden ? "shown" : "hidden")}");
        }

        private void OnDeleteLine(int lineIndex)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("DeleteLine ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingLine>(node)) return;

            var lines = EntityManager.GetBuffer<MarkingLine>(node);
            if (lineIndex < 0 || lineIndex >= lines.Length) return;
            lines.RemoveAt(lineIndex);
            // Surgical reindex (segments keep their overrides, area anchors shift) — the old
            // wipe-everything reset every user tweak on EVERY line when one was deleted.
            MarkingTopologySystem.OnLineRemoved(EntityManager, node, lineIndex);
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            ClearUIHover();
            log.Info($"UI: deleted line#{lineIndex} on node#{node.Index} — segment overrides and area anchors reindexed");
        }

        // --- Mode + next-style commands (panel mirrors of the Y / U / A hotkeys) ---

        /// <summary>Panel dropdown: style for the NEXT line drawn. Same state the Y hotkey cycles.</summary>
        private void OnSetCurrentStyle(int style)
        {
            _tool?.SetCurrentStyle((MarkingStyle)style);
        }

        /// <summary>Panel dropdown: fill style for the NEXT area closed. Same state the U hotkey cycles.</summary>
        private void OnSetCurrentAreaStyle(int styleId)
        {
            _tool?.SetCurrentAreaStyle(styleId);
        }

        // --- Pinned favourite styles (dropdown pin buttons) ---

        private void OnTogglePinLineStyle(int style)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.PinnedLineStylesCsv = ToggleIdInCsv(Mod.Settings.PinnedLineStylesCsv, style);
            Mod.Settings.ApplyAndSave();
            _pinnedStyles.Value = BuildPinnedStylesVM();
        }

        private void OnTogglePinAreaStyle(int styleId)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.PinnedAreaStylesCsv = ToggleIdInCsv(Mod.Settings.PinnedAreaStylesCsv, styleId);
            Mod.Settings.ApplyAndSave();
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

        /// <summary>Panel mode switch: NodeSelected ⇄ AreaSelecting. Entering clears any running
        /// contour; leaving drops a partial contour without committing (same as the A hotkey).</summary>
        private void OnToggleAreaMode()
        {
            if (_tool == null) return;
            if (_tool.ToolState == MarkingNodeToolSystem.State.AreaSelecting)
                _tool.ExitAreaMode();
            else
                _tool.TryEnterAreaMode();
        }

        // --- Area commands (list rows in the panel) ---

        /// <summary>Change the fill style of a committed area. Emission diffs prefab per tick and
        /// respawns the vanilla Area entity when the style prefab changes — no hash bust needed.</summary>
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
            log.Info($"UI: set area#{areaIndex} style → {styleId} on node#{node.Index}");
        }

        /// <summary>Hide/show a committed area without deleting it. Pieces keep their own
        /// visibility flags, so hide → show restores the previous piece pattern.</summary>
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
            log.Info($"UI: area#{areaIndex} on node#{node.Index} → visible={area.visible}");
        }

        /// <summary>Delete a committed area: drop its buffer entry + vertex slice, remap the
        /// firstVertex offsets of the areas after it, then force a piece recompute (piece
        /// headers reference areas by index, so every index after the deleted one shifts).</summary>
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

            // Piece headers address areas by index — bust the combined hash so
            // MarkingAreaTopologySystem rebuilds them against the shifted list.
            if (EntityManager.HasComponent<MarkingAreaTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            ClearUIHover();
            log.Info($"UI: deleted area#{areaIndex} on node#{node.Index} ({areas.Length} remaining)");
        }

        /// <summary>Full reset of the selected node: every line, segment, area and the vanilla
        /// override go away in one shot, restoring stock game markings. Buffers are cleared (not
        /// removed) — emission systems diff against the now-empty desired sets and despawn all
        /// our sublanes / Area entities on the next tick, and the absence of user lines plus the
        /// removed MarkingOverride lets CustomSecondaryLaneSystem regenerate vanilla markings.</summary>
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
            log.Info($"UI: full reset of node#{node.Index} — lines, areas and vanilla override cleared");
        }
    }

    // --- Binding payloads (serialized by GenericUIWriter; field names ARE the JS contract,
    //     hence camelCase — they must match the interfaces in useToolState.ts) ---

    /// <summary>Panel structure snapshot — everything the React panel renders except the
    /// camera-dependent popover anchors (those travel via <see cref="SegmentPointVM"/>).</summary>
    /// <summary>Pinned favourite style ids for the UI dropdowns. Field names are the React
    /// contract (GenericUIWriter serializes them verbatim) — see usePinnedStyles.ts.</summary>
    public class PinnedStylesVM
    {
        public int[] lineStyles = Array.Empty<int>();
        public int[] areaStyles = Array.Empty<int>();
    }

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

    /// <summary>Screen-space anchor of one in-world popover (CSS px, origin top-left).
    /// Segment midpoints carry (lineIndex, segmentIndex); area centroids carry areaIndex
    /// with the segment fields at -1 — both travel on the one GetScreenPoints binding.
    /// Only on-screen anchors are sent — absence means "hide the popover".</summary>
    public class SegmentPointVM
    {
        public int lineIndex;
        public int segmentIndex;
        public int areaIndex = -1;
        public float x;
        public float y;
        // Camera-distance popover scale, [0.65, 1] — see PopoverScale.
        public float scale = 1f;
    }
}
