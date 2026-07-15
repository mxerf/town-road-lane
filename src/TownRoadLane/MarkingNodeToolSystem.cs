using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 4 tool: per-node marking customisation. Activated via hotkey
    /// (<see cref="MarkingToolHotkeySystem"/>). State machine mirrors Traffic's
    /// LaneConnectorToolSystem (RESEARCH_traffic.md §5):
    ///
    ///   Default          — no node selected; click any node to select it.
    ///   NodeSelected     — node picked, dots shown; click a dot to start a pair.
    ///   SourceSelected   — source dot selected; click a target dot to commit a pair.
    ///
    /// Phase 4e additions over 4b/4c:
    ///   - hover hit-test (screen-distance) on every endpoint each frame.
    ///   - apply (LMB): node → NodeSelected; dot → source → target (write pair).
    ///   - secondaryApply (RMB): delete the pair under cursor on a selected node.
    ///   - cancel (Esc): step back one state (SourceSelected → NodeSelected → Default).
    ///
    /// Diagnostics: every state transition logs. Helps post-test triage without UI.
    /// </summary>
    public partial class MarkingNodeToolSystem : ToolBaseSystem
    {
        private static readonly ILog log = Mod.log;

        public enum State
        {
            Default,
            NodeSelected,
            SourceSelected,
            // Phase 6b: collecting vertices for a polygon area. Entered from NodeSelected via the
            // 'A' hotkey or the panel's "+ Area" button. Click adds a vertex to the running
            // contour; click on the start vertex with 3+ collected → close + commit. Right-click
            // pops the last vertex (or exits the mode entirely if the contour is empty). Esc
            // cancels regardless of progress.
            AreaSelecting,
        }

        // Phase 6b: kind of anchor a single area-polygon vertex references. Combined index space
        // — see AreaCandidate / GetAreaCandidate below — lets the user click either a lane
        // endpoint OR a corner anchor without separate hit-test passes.
        public enum AreaAnchorKind
        {
            LaneEndpoint,     // MarkingEndpoint index in _endpoints
            NodeCorner,       // MarkingCornerAnchor index in _cornerAnchors
            // Phase 7a: a line×line crossing. refIndex holds the PACKED (lineA, lineB,
            // hitIndex) value from MarkingIntersectionExtractor.Pack — NOT a list index —
            // so it can go into MarkingAreaVertex verbatim and stay stable when lines are
            // added or the crossing moves under a curvature edit.
            LineIntersection,
        }

        // Phase 6b: a single vertex collected so far in the running area polygon. Stored as the
        // anchor reference (so we can rebuild positions after a topology change) plus the edge
        // kind to the NEXT vertex once it's known. EdgeToNext is set when the user picks the
        // following vertex; the LAST entry in the list always has an unresolved EdgeToNext (it
        // gets filled in either at closure or at the next click).
        public struct AreaPolygonVertex
        {
            public AreaAnchorKind kind;
            public int refIndex;
            public AreaEdgeKind edgeToNext;
            public float3 position;  // cached at click-time to keep overlay cheap
        }

        // Phase 6b: kind of edge connecting two consecutive area-polygon vertices. Stays in
        // logical form here; the actual polyline sampling happens at emission time (6c).
        public enum AreaEdgeKind
        {
            Straight,    // direct chord between the two anchor positions
            LineBezier,  // both anchors lie on the same MarkingLine — follow that line's curve
        }

        // Phase 6b: hover/pick target while collecting an area polygon. Sentinel "None" has
        // kind == LaneEndpoint and refIndex == -1.
        public struct AreaCandidate : System.IEquatable<AreaCandidate>
        {
            public AreaAnchorKind kind;
            public int refIndex;
            public static readonly AreaCandidate None = new AreaCandidate { kind = AreaAnchorKind.LaneEndpoint, refIndex = -1 };
            public bool IsValid => refIndex >= 0;
            public bool Equals(AreaCandidate other) => kind == other.kind && refIndex == other.refIndex;
            public override bool Equals(object obj) => obj is AreaCandidate c && Equals(c);
            public override int GetHashCode() => ((int)kind << 24) ^ refIndex;
        }

        public override string toolID => "MarkingNodeTool";

        // Selection radius for screen-space dot pick: square of meters in world space, applied
        // after projecting the cursor's terrain hit onto the dot's plane. ~1.5m matches the
        // visual dot diameter (1.4m) with a small tolerance.
        private const float kDotPickRadiusSq = 1.5f * 1.5f;

        private State _state;
        private Entity _selectedNode;
        private List<MarkingEndpoint> _endpoints = new List<MarkingEndpoint>();
        // Phase 6a: corner anchors at intersection kerb meeting points. Independent from
        // _endpoints — corners are area-tool fodder, not part of line construction. Refreshed
        // by SelectNode alongside _endpoints.
        private List<MarkingCornerAnchor> _cornerAnchors = new List<MarkingCornerAnchor>();

        // Phase 7a: line-crossing anchors for the area tool. Extracted on SelectNode and
        // re-extracted whenever the node's line topology hash changes (drawing/deleting a line
        // or editing curvature from the panel moves the crossings mid-draft — the cached draft
        // vertex positions are refreshed along with it, see RefreshIntersectionAnchorsIfStale).
        private List<MarkingIntersectionAnchor> _lineIntersections = new List<MarkingIntersectionAnchor>();
        private int _lineIntersectionsHash;

        // Phase 6b: running polygon contour. Filled while State == AreaSelecting. Closed +
        // emitted as a MarkingArea on a successful close click (start vertex re-clicked with
        // 3+ vertices). Cleared on exit, cancel, or commit.
        private List<AreaPolygonVertex> _areaPolygon = new List<AreaPolygonVertex>();
        // Phase 6b: which candidate dot the cursor is over while in AreaSelecting. Encodes both
        // kind + refIndex via the AreaCandidate struct. (-1, default) = none.
        private AreaCandidate _areaHover = AreaCandidate.None;
        private int _sourceIdx = -1;
        private int _hoverIdx  = -1;
        private int _lastLoggedHoverIdx = -2; // -2 = "never logged"; -1 = "no hover"
        private float3 _cursorWorldPos;
        private Entity _hoveredNode; // raycast result while tool is active; Entity.Null when no node under cursor

        // Stage 5c: current style for the next line the user draws. Cycled via the
        // CycleMarkingStyle hotkey (default Y). Stays across tool deactivate/reactivate inside
        // a session — feels right to a user who picks "I want dashed" once at the start.
        private MarkingStyle _currentStyle = MarkingStyle.Solid;
        private ProxyAction _cycleStyleAction;

        // Phase 6b: hotkey resolver. Activated only while the tool is running.
        private ProxyAction _enterAreaAction;
        // Phase 6d: cycle style of the next area to be closed (default U). Persistent across
        // AreaSelecting / NodeSelected — same UX as CycleMarkingStyle for lines.
        private ProxyAction _cycleAreaStyleAction;
        private int _currentAreaStyle = 0;
        public int CurrentAreaStyle => _currentAreaStyle;

        // Stage 5d reverse hover-bridge: when the user clicks somewhere in NodeSelected state
        // that ISN'T an endpoint dot, we hit-test against committed lines and surface the
        // closest one's index to React via a bumped "tick" counter. Counter is what React
        // watches — same lineIndex twice in a row still triggers an effect because the tick
        // increments. -1 lineIndex means "user clicked outside any line, collapse all".
        private int _lastClickedLine = -1;
        private int _lastClickedTick;

        // Stage 5d in-game hover: result of HitTestLines on every tick while in NodeSelected.
        // Drives the overlay highlight (cursor → line). Differs from _uiHoveredLineIndex
        // (which is the UI-driven hover) — they're separate so a user hovering the UI doesn't
        // override their in-game hover. Both are pushed into MarkingOverlaySystem for joint
        // rendering — first non-negative wins.
        private int _hoveredLineInGame = -1;

        // Phase 7c: areaIndex of the committed area the cursor is inside (NodeSelected only,
        // and only when no dot/line is hovered — those are more specific). -1 = none. Mirrors
        // _hoveredLineInGame: pushed to the panel (row highlight) and read by the overlay.
        private int _hoveredAreaInGame = -1;
        // Scratch ring for the per-frame point-in-polygon test — reused to stay alloc-free.
        private readonly List<float3> _areaHitScratch = new List<float3>();

        public State ToolState => _state;
        public Entity SelectedNode => _selectedNode;
        public Entity HoveredNode => _hoveredNode;
        public IReadOnlyList<MarkingEndpoint> Endpoints => _endpoints;
        public IReadOnlyList<MarkingCornerAnchor> CornerAnchors => _cornerAnchors;
        public IReadOnlyList<MarkingIntersectionAnchor> LineIntersections => _lineIntersections;
        public IReadOnlyList<AreaPolygonVertex> AreaPolygon => _areaPolygon;
        public AreaCandidate AreaHover => _areaHover;
        public int SourceEndpointIndex => _sourceIdx;
        public int HoveredEndpointIndex => _hoverIdx;
        public float3 CursorWorldPos => _cursorWorldPos;
        public MarkingStyle CurrentStyle => _currentStyle;
        public int LastClickedLine => _lastClickedLine;
        public int LastClickedTick => _lastClickedTick;
        public int HoveredLineInGame => _hoveredLineInGame;
        public int HoveredAreaInGame => _hoveredAreaInGame;

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        // --- UI-facing mutators (TownRoadLaneUISystem) ------------------------------------
        // The panel mirrors the hotkey flows (Y / U / A) with clickable controls; these
        // methods are the click-side entry points. All of them are main-thread calls from
        // TriggerBinding handlers, same phase as our own OnUpdate — no synchronisation needed.

        /// <summary>Set the style used for the NEXT line the user draws (panel dropdown —
        /// same state the Y hotkey cycles).</summary>
        public void SetCurrentStyle(MarkingStyle style)
        {
            _currentStyle = style;
            log.Info($"tool: UI set next-line style → {_currentStyle}");
        }

        /// <summary>Set the fill style for the NEXT area the user closes (panel dropdown —
        /// same state the U hotkey cycles).</summary>
        public void SetCurrentAreaStyle(int styleId)
        {
            _currentAreaStyle = math.clamp(styleId, 0, MarkingAreaEmissionSystem.kStyleCount - 1);
            log.Info($"tool: UI set next-area style → {_currentAreaStyle}");
        }

        /// <summary>Panel "Area" mode button — NodeSelected → AreaSelecting. Also aborts a
        /// half-picked line pair first (SourceSelected → NodeSelected → AreaSelecting) so the
        /// button works from any node-scoped state. No-op in Default (no node to draw on).</summary>
        public bool TryEnterAreaMode()
        {
            if (_state == State.SourceSelected)
            {
                _sourceIdx = -1;
                _state = State.NodeSelected;
            }
            if (_state != State.NodeSelected) return false;
            _state = State.AreaSelecting;
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            log.Info($"area: entered AreaSelecting via UI on node #{_selectedNode.Index}");
            return true;
        }

        /// <summary>Panel "Lines" mode button / area-mode cancel — AreaSelecting → NodeSelected,
        /// dropping any partially collected contour.</summary>
        public void ExitAreaMode()
        {
            if (_state != State.AreaSelecting) return;
            log.Info($"area: exited AreaSelecting via UI (had {_areaPolygon.Count} vertices)");
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _state = State.NodeSelected;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            // Resolve the cycle-style action once (same pattern MarkingToolHotkeySystem uses for
            // ToggleMarkingTool). Action stays disabled until OnStartRunning so the binding
            // doesn't intercept Y when our tool isn't active.
            if (Mod.Settings != null)
            {
                _cycleStyleAction = Mod.Settings.GetAction(Setting.CycleMarkingStyle);
                _enterAreaAction = Mod.Settings.GetAction(Setting.EnterAreaMode);
                _cycleAreaStyleAction = Mod.Settings.GetAction(Setting.CycleAreaStyle);
            }
            log.Info($"MarkingNodeToolSystem: OnCreate — toolID='{toolID}' registered with ToolSystem.tools (count={m_ToolSystem.tools.Count}), cycleStyleAction={(_cycleStyleAction != null ? "OK" : "NULL")}, enterAreaAction={(_enterAreaAction != null ? "OK" : "NULL")}, cycleAreaStyleAction={(_cycleAreaStyleAction != null ? "OK" : "NULL")}");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            ResetSelection();
            // Base class OnStartRunning → SetActions → UpdateActions (virtual). Default impl is
            // empty, which leaves applyAction.shouldBeEnabled = false (from ResetActions on last
            // OnStopRunning). That made our clicks no-op. Enable them explicitly here — same
            // pattern DefaultToolSystem.cs:735-743 uses (sans the internal DeferStateUpdating
            // batch wrapper, which we can't reach from outside Game.dll — three separate sets
            // are equally correct, just slightly less efficient).
            applyAction.shouldBeEnabled = true;
            secondaryApplyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            if (_cycleStyleAction != null) _cycleStyleAction.shouldBeEnabled = true;
            if (_enterAreaAction != null) _enterAreaAction.shouldBeEnabled = true;
            if (_cycleAreaStyleAction != null) _cycleAreaStyleAction.shouldBeEnabled = true;
            log.Info($"MarkingNodeToolSystem: activated, actions enabled (apply={applyAction != null}, cancel={cancelAction != null}, cycleStyle={_cycleStyleAction != null})");
        }

        protected override void OnStopRunning()
        {
            log.Info($"MarkingNodeToolSystem: deactivated (state was {_state}, selectedNode #{_selectedNode.Index})");
            if (_cycleStyleAction != null) _cycleStyleAction.shouldBeEnabled = false;
            if (_enterAreaAction != null) _enterAreaAction.shouldBeEnabled = false;
            if (_cycleAreaStyleAction != null) _cycleAreaStyleAction.shouldBeEnabled = false;
            ResetSelection();
            base.OnStopRunning();
        }

        private void ResetSelection()
        {
            _state = State.Default;
            _selectedNode = Entity.Null;
            _endpoints.Clear();
            _cornerAnchors.Clear();
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _sourceIdx = -1;
            _hoverIdx = -1;
            _lastLoggedHoverIdx = -2;
            _hoveredNode = Entity.Null;
            _lastClickedLine = -1; // new node selection should not auto-expand a stale row
            _lastClickedTick++;
            _hoveredLineInGame = -1;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Net | TypeMask.Terrain;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.PublicTransportRoad | Layer.SubwayTrack | Layer.TrainTrack | Layer.TramTrack;
            m_ToolRaycastSystem.raycastFlags |= RaycastFlags.SubElements;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Each frame: figure out cursor world position + which endpoint (if any) is hovered.
            // Used by Phase-4d overlay (drag-line target, hover highlight) and 4e clicks.
            RaycastHit hit;
            bool hitSomething = GetRaycastResult(out Entity hitEntity, out hit);
            _cursorWorldPos = hitSomething ? hit.m_HitPosition : float3.zero;
            _hoveredNode = (hitSomething && EntityManager.HasComponent<Node>(hitEntity)) ? hitEntity : Entity.Null;
            _hoverIdx = (_state != State.Default && _state != State.AreaSelecting && hitSomething) ? FindHoveredEndpoint(_cursorWorldPos) : -1;
            // Phase 7a: crossings move when lines are drawn/deleted or curvature is edited from
            // the panel mid-draft — keep the anchor list (and cached draft positions) in sync.
            if (_state == State.AreaSelecting) RefreshIntersectionAnchorsIfStale();
            // Phase 6b: separate hit-test for area mode — covers lane endpoints, corner anchors
            // and line crossings with a single picked AreaCandidate.
            _areaHover = (_state == State.AreaSelecting && hitSomething) ? FindHoveredAreaCandidate(_cursorWorldPos) : AreaCandidate.None;

            // In-game line hover: cursor near a committed line (and not already on a dot) →
            // highlight that line. Skipped in SourceSelected because the cursor is busy aiming
            // at a target dot — would feel noisy. Cheap: HitTestLines is O(lines × samples)
            // with both factors tiny in practice.
            _hoveredLineInGame = (_state == State.NodeSelected && _hoverIdx < 0 && hitSomething)
                ? HitTestLines(_cursorWorldPos)
                : -1;

            // Phase 7c: in-game area hover — cursor inside one of the committed areas' pieces.
            // Lines/dots are more specific targets, so they win; point-in-polygon runs over the
            // precomputed piece rings (both counts tiny).
            _hoveredAreaInGame = (_state == State.NodeSelected && _hoverIdx < 0 && _hoveredLineInGame < 0 && hitSomething)
                ? HitTestAreas(_cursorWorldPos)
                : -1;

            // Polish: log hover transitions only (avoid per-frame spam). Useful for triage of
            // "I'm hovering but the dot doesn't react" reports.
            if (_hoverIdx != _lastLoggedHoverIdx)
            {
                if (_hoverIdx >= 0)
                {
                    var ep = _endpoints[_hoverIdx];
                    log.Info($"tool: hover endpoint idx={_hoverIdx} edge=#{ep.edge.Index} gap={ep.gapIndex}");
                }
                else if (_lastLoggedHoverIdx >= 0)
                {
                    log.Info($"tool: hover cleared (was idx={_lastLoggedHoverIdx})");
                }
                _lastLoggedHoverIdx = _hoverIdx;
            }

            // Cycle marking style (Stage 5c). Independent of selection state — user can pre-pick
            // a style before clicking the first dot, or change mid-flow. New value applies to the
            // NEXT line created; doesn't retroactively restyle existing lines.
            if (_cycleStyleAction != null && _cycleStyleAction.WasPerformedThisFrame())
            {
                _currentStyle = NextStyle(_currentStyle);
                log.Info($"tool: cycled style → {_currentStyle}");
            }

            // Phase 6d: cycle area style. Applies to the NEXT polygon the user closes — same
            // pattern as line styles. Range 0..MarkingAreaEmissionSystem.kStyleCount-1.
            if (_cycleAreaStyleAction != null && _cycleAreaStyleAction.WasPerformedThisFrame())
            {
                _currentAreaStyle = (_currentAreaStyle + 1) % MarkingAreaEmissionSystem.kStyleCount;
                log.Info($"tool: cycled area style → {_currentAreaStyle}");
            }

            // Phase 6b: enter / leave area polygon mode (default A). NodeSelected → AreaSelecting,
            // and pressing A again from AreaSelecting bails out without committing.
            if (_enterAreaAction != null && _enterAreaAction.WasPerformedThisFrame())
            {
                if (_state == State.NodeSelected)
                {
                    _state = State.AreaSelecting;
                    _areaPolygon.Clear();
                    _areaHover = AreaCandidate.None;
                    log.Info($"area: entered AreaSelecting on node #{_selectedNode.Index}");
                }
                else if (_state == State.AreaSelecting)
                {
                    log.Info($"area: cancelled AreaSelecting via hotkey (had {_areaPolygon.Count} vertices)");
                    _areaPolygon.Clear();
                    _areaHover = AreaCandidate.None;
                    _state = State.NodeSelected;
                }
            }

            // Cancel (Esc / RMB-as-cancel): step back one state.
            if (cancelAction.WasPressedThisFrame())
            {
                if (_state == State.AreaSelecting)
                {
                    log.Info($"area: cancelled AreaSelecting via Esc (had {_areaPolygon.Count} vertices)");
                    _areaPolygon.Clear();
                    _areaHover = AreaCandidate.None;
                    _state = State.NodeSelected;
                }
                else if (_state == State.SourceSelected)
                {
                    log.Info($"tool: cancel — clearing source #{_sourceIdx}");
                    _sourceIdx = -1;
                    _state = State.NodeSelected;
                }
                else if (_state == State.NodeSelected)
                {
                    log.Info($"tool: cancel — deselecting node #{_selectedNode.Index}");
                    ResetSelection();
                }
                else
                {
                    log.Info("tool: cancel from Default — deactivating tool");
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                }
                return inputDeps;
            }

            // Phase 6b: secondary apply (RMB) in AreaSelecting pops the last placed vertex (or
            // exits the mode if the contour is empty). Matches IMT's right-click-to-undo UX.
            if (_state == State.AreaSelecting && secondaryApplyAction.WasPressedThisFrame())
            {
                if (_areaPolygon.Count > 0)
                {
                    _areaPolygon.RemoveAt(_areaPolygon.Count - 1);
                    log.Info($"area: popped last vertex, {_areaPolygon.Count} remaining");
                }
                else
                {
                    log.Info("area: RMB on empty contour → leave AreaSelecting");
                    _state = State.NodeSelected;
                }
                return inputDeps;
            }

            // Note: secondary apply (RMB) is no longer used for deletion — the create gesture is
            // now a toggle (see SourceSelected branch below), matching Traffic's UX. RMB is left
            // to vanilla cancelAction mapping where applicable.

            // Primary apply (LMB): state-machine transitions.
            if (applyAction.WasPressedThisFrame())
            {
                // Phase 4 debug: log raycast outcome on every click so we can see whether Apply
                // even fires and what raycast returns. "ничего не происходит" diagnostic — these
                // logs distinguish (no Apply event) vs (Apply but no raycast) vs (Apply + hit
                // but not Node).
                log.Info($"tool: LMB fired — state={_state}, hitSomething={hitSomething}, hitEntity=#{(hitSomething ? hitEntity.Index : -1)}, hasNode={(hitSomething && EntityManager.HasComponent<Node>(hitEntity))}");
                if (_state == State.Default)
                {
                    if (hitSomething && EntityManager.HasComponent<Node>(hitEntity))
                    {
                        SelectNode(hitEntity);
                    }
                    else
                    {
                        log.Info($"tool: click in Default ignored (hit #{(hitSomething ? hitEntity.Index : -1)}, no Node)");
                    }
                }
                else if (_state == State.NodeSelected)
                {
                    if (_hoverIdx >= 0)
                    {
                        _sourceIdx = _hoverIdx;
                        _state = State.SourceSelected;
                        log.Info($"tool: source endpoint chosen — idx={_sourceIdx} edge=#{_endpoints[_sourceIdx].edge.Index} gap={_endpoints[_sourceIdx].gapIndex}");
                    }
                    else if (hitSomething && EntityManager.HasComponent<Node>(hitEntity) && hitEntity != _selectedNode)
                    {
                        // Click on a different node — switch selection.
                        SelectNode(hitEntity);
                    }
                    else if (hitSomething)
                    {
                        // Stage 5d reverse hover-bridge: no dot hit, no other node — try a
                        // distance-to-curve hit-test against committed lines. If a line is
                        // close to the cursor, expand its accordion row in the React panel.
                        int clickedLine = HitTestLines(_cursorWorldPos);
                        _lastClickedLine = clickedLine;
                        _lastClickedTick++;
                        log.Info($"tool: click on line #{clickedLine} (or -1 = empty space)");
                    }
                    else
                    {
                        log.Info("tool: click in NodeSelected — no dot/raycast, ignored");
                    }
                }
                else if (_state == State.SourceSelected)
                {
                    if (_hoverIdx >= 0 && _hoverIdx != _sourceIdx)
                    {
                        // Traffic-like toggle: if a pair already exists between source and target,
                        // delete it. Otherwise create. Same gesture creates AND removes — no need
                        // for a separate "delete mode" or RMB.
                        TogglePair(_endpoints[_sourceIdx], _endpoints[_hoverIdx]);
                        _sourceIdx = -1;
                        _state = State.NodeSelected;
                    }
                    else
                    {
                        log.Info("tool: click in SourceSelected — no different target dot hovered, ignored");
                    }
                }
                else if (_state == State.AreaSelecting)
                {
                    if (!_areaHover.IsValid)
                    {
                        log.Info("area: LMB with no hovered candidate, ignored");
                    }
                    else if (AreaCanCloseOn(_areaHover))
                    {
                        AreaClose();
                    }
                    else
                    {
                        AreaAddVertex(_areaHover);
                    }
                }
            }

            return inputDeps;
        }

        private void SelectNode(Entity node)
        {
            _selectedNode = node;
            // Verbose extract — writes a per-edge / per-lane breakdown so we can debug missing
            // endpoints without re-deploying. Cheap (only fires on node click).
            _endpoints = MarkingEndpointExtractor.Extract(EntityManager, node, log: true);
            // Phase 6a: corner anchors for the polygon area tool. Cheap (re-walks the same
            // ConnectedEdges as Extract) and only runs on node click.
            _cornerAnchors = MarkingEndpointExtractor.ExtractCornerAnchors(EntityManager, node);
            RefreshIntersectionAnchors();
            _sourceIdx = -1;
            _state = State.NodeSelected;
            int existingLines = EntityManager.HasBuffer<MarkingLine>(node)
                ? EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true).Length
                : 0;
            int existingSegs = EntityManager.HasBuffer<MarkingSegment>(node)
                ? EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true).Length
                : 0;
            log.Info($"tool: selected node #{node.Index} — {_endpoints.Count} endpoint(s), {_cornerAnchors.Count} corner(s), {existingLines} line(s), {existingSegs} segment(s)");
        }

        private int FindHoveredEndpoint(float3 cursor)
        {
            int best = -1;
            float bestSq = kDotPickRadiusSq;
            for (int i = 0; i < _endpoints.Count; i++)
            {
                float3 d = _endpoints[i].position - cursor;
                // Compare in XZ plane only — terrain height varies, the dot sits at lane height.
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }

        // Hit-test radius (XZ, world units²) for the reverse hover-bridge — the cursor needs to
        // land within ~2m of a line's painted geometry for it to count as a click on that line.
        private const float kLinePickRadiusSq = 2.0f * 2.0f;
        // Number of evenly-spaced t samples taken along each Bezier to approximate distance.
        // 12 samples → ~1m resolution on a 10-12m line; cheap enough for per-click hit-test.
        private const int   kLineSampleCount  = 12;

        /// <summary>Pick the index of the MarkingLine whose Bezier passes closest to the cursor,
        /// or -1 if no line is within <see cref="kLinePickRadiusSq"/>. Sampling-based — not
        /// analytic, but the lines are short and the radius is generous so accuracy is fine for
        /// UI hit-test purposes.</summary>
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
                    float3 p = Colossal.Mathematics.MathUtils.Position(bez, t);
                    float dx = p.x - cursor.x;
                    float dz = p.z - cursor.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
            }
            return best;
        }

        // --- Phase 6b helpers --------------------------------------------------------------

        /// <summary>World-space position of an area-polygon anchor candidate. Bridge between the
        /// AreaAnchorKind/refIndex pair and the underlying lane endpoint / corner anchor list.</summary>
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
            // LineIntersection — refIndex is the packed pair ref, not a list index; find it in
            // the current extraction (linear, the list is tiny).
            for (int i = 0; i < _lineIntersections.Count; i++)
            {
                if (_lineIntersections[i].PackedRef == c.refIndex)
                {
                    pos = _lineIntersections[i].position;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Pick the closest area-tool candidate (lane endpoint, corner anchor or line
        /// crossing) to the cursor. Same XZ-only metric as <see cref="FindHoveredEndpoint"/>.</summary>
        private AreaCandidate FindHoveredAreaCandidate(float3 cursor)
        {
            AreaCandidate best = AreaCandidate.None;
            float bestSq = kDotPickRadiusSq;
            for (int i = 0; i < _endpoints.Count; i++)
            {
                float3 d = _endpoints[i].position - cursor;
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq) { bestSq = sq; best = new AreaCandidate { kind = AreaAnchorKind.LaneEndpoint, refIndex = i }; }
            }
            for (int i = 0; i < _cornerAnchors.Count; i++)
            {
                float3 d = _cornerAnchors[i].position - cursor;
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq) { bestSq = sq; best = new AreaCandidate { kind = AreaAnchorKind.NodeCorner, refIndex = i }; }
            }
            for (int i = 0; i < _lineIntersections.Count; i++)
            {
                float3 d = _lineIntersections[i].position - cursor;
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq) { bestSq = sq; best = new AreaCandidate { kind = AreaAnchorKind.LineIntersection, refIndex = _lineIntersections[i].PackedRef }; }
            }
            return best;
        }

        /// <summary>Re-extract the line-crossing anchors and remember the topology hash they
        /// were built against.</summary>
        private void RefreshIntersectionAnchors()
        {
            _lineIntersections = MarkingIntersectionExtractor.ExtractAll(EntityManager, _selectedNode);
            _lineIntersectionsHash = _selectedNode != Entity.Null && EntityManager.HasComponent<MarkingTopologyState>(_selectedNode)
                ? EntityManager.GetComponentData<MarkingTopologyState>(_selectedNode).linesHash
                : 0;
        }

        /// <summary>Cheap per-frame guard for area mode: when the line topology hash moved
        /// (line added/deleted, curvature edited from the panel), re-extract the crossings AND
        /// re-resolve the cached positions of already-placed draft vertices so the contour
        /// follows the lines instead of pointing at where they used to be.</summary>
        private void RefreshIntersectionAnchorsIfStale()
        {
            if (_selectedNode == Entity.Null) return;
            int current = EntityManager.HasComponent<MarkingTopologyState>(_selectedNode)
                ? EntityManager.GetComponentData<MarkingTopologyState>(_selectedNode).linesHash
                : 0;
            if (current == _lineIntersectionsHash) return;
            RefreshIntersectionAnchors();
            for (int i = 0; i < _areaPolygon.Count; i++)
            {
                var pv = _areaPolygon[i];
                var cand = new AreaCandidate { kind = pv.kind, refIndex = pv.refIndex };
                if (TryGetAreaAnchorPos(cand, out var pos))
                {
                    pv.position = pos;
                    _areaPolygon[i] = pv;
                }
            }
        }

        /// <summary>Determine which kind of edge connects <paramref name="from"/> to
        /// <paramref name="to"/>. IMT-style: if both anchors sit on the same MarkingLine (its
        /// endpoints, or a crossing whose pair includes it), the edge follows that line's Bezier
        /// (LineBezier). Otherwise a straight chord (Straight). Sampling of curved edges is still
        /// deferred (rings render as chords today) — the metadata is kept correct for when it lands.</summary>
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

        /// <summary>True when the anchor geometrically sits on the given line: a lane endpoint
        /// that is the line's source/target, or a crossing whose packed pair includes the line.</summary>
        private bool AnchorLiesOnLine(AreaCandidate c, MarkingLine ln, int lineIndex)
        {
            if (c.kind == AreaAnchorKind.LaneEndpoint)
            {
                if (c.refIndex >= _endpoints.Count) return false;
                var ep = _endpoints[c.refIndex];
                return (ln.sourceEdge == ep.edge && ln.sourceGapIndex == ep.gapIndex)
                    || (ln.targetEdge == ep.edge && ln.targetGapIndex == ep.gapIndex);
            }
            if (c.kind == AreaAnchorKind.LineIntersection)
            {
                MarkingIntersectionExtractor.Unpack(c.refIndex, out int a, out int b, out _);
                return lineIndex == a || lineIndex == b;
            }
            return false;
        }

        // Curved draft edges sample with MarkingAreaTopologySystem.SampleCurvedEdge so the
        // preview shows exactly the polyline the committed ring will get (its sparseness is
        // deliberate — see the triangulation notes there).

        /// <summary>Phase 7b: the draft contour as a drawable polyline — vertex positions with
        /// LineBezier edges sampled along their shared line, so the overlay preview shows the
        /// curve the committed area will actually follow. Open path (no closing edge; the
        /// last→cursor preview stays a chord — the closing edge kind isn't known until the
        /// closing click classifies it).</summary>
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

        /// <summary>Sample the shared line's sub-curve between two draft vertices into
        /// <paramref name="into"/> (interior points only). Falls back to nothing (= chord)
        /// when the shared line can't be found/built anymore.</summary>
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

        /// <summary>Phase 7c: areaIndex of the piece the cursor is inside, or -1. Runs over the
        /// precomputed MarkingAreaPiece rings (positions already resolved by the topology).</summary>
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
                if (ok && PolygonSplitter.ContainsXZ(_areaHitScratch, cursor)) return pd.areaIndex;
            }
            return -1;
        }

        /// <summary>t parameter of a draft anchor on the given line: endpoint → source (0) /
        /// target (1); crossing → the pair member's parameter from the current extraction.</summary>
        private bool TryDraftAnchorParamOnLine(AreaAnchorKind kind, int refIndex, MarkingLine ln, int lineIndex, out float t)
        {
            t = 0f;
            if (kind == AreaAnchorKind.LaneEndpoint)
            {
                if (refIndex < 0 || refIndex >= _endpoints.Count) return false;
                var ep = _endpoints[refIndex];
                if (ln.sourceEdge == ep.edge && ln.sourceGapIndex == ep.gapIndex) { t = 0f; return true; }
                if (ln.targetEdge == ep.edge && ln.targetGapIndex == ep.gapIndex) { t = 1f; return true; }
                return false;
            }
            if (kind == AreaAnchorKind.LineIntersection)
            {
                for (int i = 0; i < _lineIntersections.Count; i++)
                {
                    var x = _lineIntersections[i];
                    if (x.PackedRef != refIndex) continue;
                    if (x.lineA == lineIndex) { t = x.tA; return true; }
                    if (x.lineB == lineIndex) { t = x.tB; return true; }
                    return false;
                }
            }
            return false;
        }

        /// <summary>Add a vertex to the running polygon contour. Fills in the previous vertex's
        /// edgeToNext now that we know which anchor it connects to.</summary>
        private void AreaAddVertex(AreaCandidate c)
        {
            if (!c.IsValid) return;
            if (!TryGetAreaAnchorPos(c, out var pos)) return;

            // Set previous vertex's edge kind now that the next vertex is known.
            if (_areaPolygon.Count > 0)
            {
                var prev = _areaPolygon[_areaPolygon.Count - 1];
                AreaCandidate prevCand = new AreaCandidate { kind = prev.kind, refIndex = prev.refIndex };
                prev.edgeToNext = ClassifyEdge(prevCand, c);
                _areaPolygon[_areaPolygon.Count - 1] = prev;
            }

            _areaPolygon.Add(new AreaPolygonVertex
            {
                kind = c.kind,
                refIndex = c.refIndex,
                position = pos,
                edgeToNext = AreaEdgeKind.Straight,  // placeholder — filled on next click or closure
            });
            log.Info($"area: vertex {_areaPolygon.Count} added (kind={c.kind}, ref={c.refIndex})");
        }

        /// <summary>True when the running contour can be closed: 3+ vertices AND the candidate
        /// matches the very first vertex.</summary>
        private bool AreaCanCloseOn(AreaCandidate c)
        {
            if (_areaPolygon.Count < 3 || !c.IsValid) return false;
            var first = _areaPolygon[0];
            return first.kind == c.kind && first.refIndex == c.refIndex;
        }

        /// <summary>Close the polygon (3+ vertices, last-click matched first). Phase 6c: commit
        /// the contour to the host node's <see cref="MarkingArea"/> + <see cref="MarkingAreaVertex"/>
        /// buffers. <c>MarkingAreaEmissionSystem</c> picks it up next frame and spawns the
        /// vanilla Area entity.</summary>
        private void AreaClose()
        {
            // Fill in the LAST→FIRST edge kind from the last placed vertex back to the start.
            var last = _areaPolygon[_areaPolygon.Count - 1];
            AreaCandidate lastCand = new AreaCandidate { kind = last.kind, refIndex = last.refIndex };
            AreaCandidate firstCand = new AreaCandidate { kind = _areaPolygon[0].kind, refIndex = _areaPolygon[0].refIndex };
            last.edgeToNext = ClassifyEdge(lastCand, firstCand);
            _areaPolygon[_areaPolygon.Count - 1] = last;

            // Commit to per-node buffers. Create them on demand — most nodes never get an area.
            if (!EntityManager.HasBuffer<MarkingArea>(_selectedNode))
                EntityManager.AddBuffer<MarkingArea>(_selectedNode);
            if (!EntityManager.HasBuffer<MarkingAreaVertex>(_selectedNode))
                EntityManager.AddBuffer<MarkingAreaVertex>(_selectedNode);

            var areas = EntityManager.GetBuffer<MarkingArea>(_selectedNode);
            var verts = EntityManager.GetBuffer<MarkingAreaVertex>(_selectedNode);
            int firstVertex = verts.Length;
            for (int i = 0; i < _areaPolygon.Count; i++)
            {
                var pv = _areaPolygon[i];
                var av = new MarkingAreaVertex
                {
                    kind = (byte)pv.kind,
                    refIndex = pv.refIndex,
                    edgeToNext = (byte)pv.edgeToNext,
                    refPos = pv.position,
                };
                // v2 stable identity — raw list indexes don't survive save/load (lane rebuild
                // reorders extraction), so store the same edge/gap keys MarkingLine uses.
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
                verts.Add(av);
            }
            areas.Add(new MarkingArea
            {
                styleId = _currentAreaStyle,
                visible = true,
                firstVertex = firstVertex,
                vertexCount = _areaPolygon.Count,
            });

            // Mark the node Updated so MarkingAreaEmissionSystem sees the change next frame.
            if (!EntityManager.HasComponent<Updated>(_selectedNode))
                EntityManager.AddComponent<Updated>(_selectedNode);

            log.Info($"area: closed with {_areaPolygon.Count} vertices on node #{_selectedNode.Index} — buffer now has {areas.Length} area(s)");
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _state = State.NodeSelected;
        }

        // --- end Phase 6b helpers ----------------------------------------------------------

        /// <summary>Create-or-delete: if a line with matching endpoints (order-insensitive) already
        /// exists, remove it; otherwise append a new one. Matches Traffic-style toggle UX.
        /// On delete the segment buffer is reindexed surgically (MarkingTopologySystem
        /// .OnLineRemoved) so per-segment overrides on OTHER lines survive.</summary>
        private void TogglePair(MarkingEndpoint src, MarkingEndpoint dst)
        {
            if (!EntityManager.HasBuffer<MarkingLine>(_selectedNode))
            {
                EntityManager.AddBuffer<MarkingLine>(_selectedNode);
            }
            var buf = EntityManager.GetBuffer<MarkingLine>(_selectedNode);

            for (int i = 0; i < buf.Length; i++)
            {
                var p = buf[i];
                bool sameDirection  = p.sourceEdge == src.edge && p.sourceGapIndex == src.gapIndex && p.targetEdge == dst.edge && p.targetGapIndex == dst.gapIndex;
                bool swappedSides   = p.sourceEdge == dst.edge && p.sourceGapIndex == dst.gapIndex && p.targetEdge == src.edge && p.targetGapIndex == src.gapIndex;
                if (sameDirection || swappedSides)
                {
                    log.Info($"tool: toggled OFF line #{i} on node #{_selectedNode.Index}");
                    buf.RemoveAt(i);
                    MarkingTopologySystem.OnLineRemoved(EntityManager, _selectedNode, i);
                    if (!EntityManager.HasComponent<Updated>(_selectedNode))
                        EntityManager.AddComponent<Updated>(_selectedNode);
                    return;
                }
            }

            buf.Add(new MarkingLine
            {
                sourceEdge = src.edge, sourceGapIndex = src.gapIndex,
                targetEdge = dst.edge, targetGapIndex = dst.gapIndex,
                style = (int)_currentStyle,
                curvature = MarkingCurveBuilder.kPullFactor,
            });
            if (!EntityManager.HasComponent<Updated>(_selectedNode))
                EntityManager.AddComponent<Updated>(_selectedNode);
            log.Info($"tool: toggled ON line #{buf.Length - 1} on node #{_selectedNode.Index} style={_currentStyle} — "
                + $"src(edge=#{src.edge.Index} gap={src.gapIndex}) → "
                + $"dst(edge=#{dst.edge.Index} gap={dst.gapIndex})");
        }

        /// <summary>Cycle to the next defined style. Wraps around at the end. Add new values to
        /// <see cref="MarkingStyle"/> and they automatically participate — the cycle uses
        /// <see cref="System.Enum.GetValues"/>.</summary>
        private static MarkingStyle NextStyle(MarkingStyle current)
        {
            var values = (MarkingStyle[])System.Enum.GetValues(typeof(MarkingStyle));
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == current) return values[(i + 1) % values.Length];
            }
            return MarkingStyle.Solid;
        }
    }
}
