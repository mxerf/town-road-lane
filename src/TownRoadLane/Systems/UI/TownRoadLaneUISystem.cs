using System;
using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game.SceneFlow;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.Tool;
using TownRoadLane.Utilities;

namespace TownRoadLane.Systems.UI
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

        /// <summary>Per-frame binding refresh. Hashes every UI-relevant value without allocating,
        /// and builds a new <see cref="PanelStateVM"/> only when the hash changed. Screen anchors
        /// have their own hash, so a camera pan pushes only the points.</summary>
        private void RebuildBindings()
        {
            bool isActive = _toolSystem.activeTool == _tool;
            Entity node = isActive ? _tool.SelectedNode : Entity.Null;

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
            var h = Fnv1a.Create();
            h.Add(isActive);
            h.Add(isActive ? (int)_tool.ToolState : 0);
            h.Add(isActive ? _tool.AreaPolygon.Count : 0);
            h.Add(_tool.CurrentAreaStyle);
            h.Add(node != Entity.Null ? node.Index : -1);
            h.Add((int)_tool.CurrentStyle);
            h.Add(IsVanillaHidden(node));
            h.Add(_tool.LastClickedLine);
            h.Add(_tool.LastClickedTick);
            h.Add(_tool.HoveredLineInGame);
            h.Add(_tool.HoveredAreaInGame);

            if (node != Entity.Null && EntityManager.HasBuffer<MarkingLine>(node))
            {
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                h.Add(lines.Length);
                for (int i = 0; i < lines.Length; i++)
                {
                    h.Add(lines[i].style);
                    h.Add(lines[i].curvature);
                }
                if (EntityManager.HasBuffer<MarkingSegment>(node))
                {
                    var segs = EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true);
                    h.Add(segs.Length);
                    for (int s = 0; s < segs.Length; s++)
                    {
                        var seg = segs[s];
                        h.Add(seg.lineIndex);
                        h.Add(seg.tStart);
                        h.Add(seg.tEnd);
                        h.Add(seg.visible);
                        h.Add(seg.style);
                    }
                }
            }

            if (node != Entity.Null && EntityManager.HasBuffer<MarkingArea>(node))
            {
                var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                h.Add(areas.Length);
                for (int a = 0; a < areas.Length; a++)
                {
                    h.Add(areas[a].styleId);
                    h.Add(areas[a].visible);
                    h.Add(areas[a].vertexCount);
                }
                if (EntityManager.HasBuffer<MarkingAreaPiece>(node))
                {
                    var pieces = EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true);
                    h.Add(pieces.Length);
                    for (int p = 0; p < pieces.Length; p++)
                    {
                        h.Add(pieces[p].areaIndex);
                        h.Add(pieces[p].visible);
                    }
                }
            }

            return h.Value;
        }

        private PanelStateVM BuildPanelState(bool isActive, Entity node)
        {
            var vm = new PanelStateVM
            {
                isActive = isActive,
                toolState = isActive ? (int)_tool.ToolState : 0,
                areaVertexCount = isActive ? _tool.AreaPolygon.Count : 0,
                currentAreaStyle = _tool.CurrentAreaStyle,
                selectedNodeIndex = node != Entity.Null ? node.Index : -1,
                currentStyle = (int)_tool.CurrentStyle,
                vanillaHidden = IsVanillaHidden(node),
                // Line or area under the cursor in the world (-1 = none), so React can highlight
                // the matching row.
                lastClickedLine = _tool.LastClickedLine,
                lastClickedTick = _tool.LastClickedTick,
                hoveredLineInGame = _tool.HoveredLineInGame,
                hoveredAreaInGame = _tool.HoveredAreaInGame,
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
                                segmentIndex = segScratch.Count,
                                tStart = seg.tStart,
                                tEnd = seg.tEnd,
                                visible = seg.visible,
                                style = seg.style,
                                lengthM = lengthM,
                            });
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

        private bool IsVanillaHidden(Entity node)
            => node != Entity.Null
               && EntityManager.HasComponent<MarkingOverride>(node)
               && EntityManager.GetComponentData<MarkingOverride>(node).HideAll;
    }
}
