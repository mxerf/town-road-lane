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
            LaneEndpoint,   // MarkingEndpoint index in _endpoints
            NodeCorner,     // MarkingCornerAnchor index in _cornerAnchors
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
        private int _heartbeatCounter; // logs raycast outcome periodically when active

        // Stage 5c: current style for the next line the user draws. Cycled via the
        // CycleMarkingStyle hotkey (default Y). Stays across tool deactivate/reactivate inside
        // a session — feels right to a user who picks "I want dashed" once at the start.
        private MarkingStyle _currentStyle = MarkingStyle.Solid;
        private ProxyAction _cycleStyleAction;

        // Phase 6b: hotkey resolver. Activated only while the tool is running.
        private ProxyAction _enterAreaAction;

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

        public State ToolState => _state;
        public Entity SelectedNode => _selectedNode;
        public Entity HoveredNode => _hoveredNode;
        public IReadOnlyList<MarkingEndpoint> Endpoints => _endpoints;
        public IReadOnlyList<MarkingCornerAnchor> CornerAnchors => _cornerAnchors;
        public IReadOnlyList<AreaPolygonVertex> AreaPolygon => _areaPolygon;
        public AreaCandidate AreaHover => _areaHover;
        public int SourceEndpointIndex => _sourceIdx;
        public int HoveredEndpointIndex => _hoverIdx;
        public float3 CursorWorldPos => _cursorWorldPos;
        public MarkingStyle CurrentStyle => _currentStyle;
        public int LastClickedLine => _lastClickedLine;
        public int LastClickedTick => _lastClickedTick;
        public int HoveredLineInGame => _hoveredLineInGame;

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

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
            }
            log.Info($"MarkingNodeToolSystem: OnCreate — toolID='{toolID}' registered with ToolSystem.tools (count={m_ToolSystem.tools.Count}), cycleStyleAction={(_cycleStyleAction != null ? "OK" : "NULL")}, enterAreaAction={(_enterAreaAction != null ? "OK" : "NULL")}");
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
            log.Info($"MarkingNodeToolSystem: activated, actions enabled (apply={applyAction != null}, cancel={cancelAction != null}, cycleStyle={_cycleStyleAction != null})");
        }

        protected override void OnStopRunning()
        {
            log.Info($"MarkingNodeToolSystem: deactivated (state was {_state}, selectedNode #{_selectedNode.Index})");
            if (_cycleStyleAction != null) _cycleStyleAction.shouldBeEnabled = false;
            if (_enterAreaAction != null) _enterAreaAction.shouldBeEnabled = false;
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
            // Phase 6b: separate hit-test for area mode — covers both lane endpoints + corner
            // anchors with a single picked AreaCandidate.
            _areaHover = (_state == State.AreaSelecting && hitSomething) ? FindHoveredAreaCandidate(_cursorWorldPos) : AreaCandidate.None;

            // In-game line hover: cursor near a committed line (and not already on a dot) →
            // highlight that line. Skipped in SourceSelected because the cursor is busy aiming
            // at a target dot — would feel noisy. Cheap: HitTestLines is O(lines × samples)
            // with both factors tiny in practice.
            _hoveredLineInGame = (_state == State.NodeSelected && _hoverIdx < 0 && hitSomething)
                ? HitTestLines(_cursorWorldPos)
                : -1;

            // Heartbeat every ~120 frames (~2 sec @60fps) — confirms tool is actually running and
            // raycast is hitting things. Without this we can't tell "tool not ticking" apart from
            // "tool ticking but raycast empty".
            if ((++_heartbeatCounter % 120) == 0)
            {
                string ent = hitSomething ? $"#{hitEntity.Index}" : "<none>";
                string hasNode = hitSomething && EntityManager.HasComponent<Node>(hitEntity) ? "YES" : "no";
                log.Info($"tool heartbeat: state={_state}, raycast hit={ent} hasNode={hasNode}, cursor={_cursorWorldPos}");
            }
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
            else // NodeCorner
            {
                if (c.refIndex >= _cornerAnchors.Count) return false;
                pos = _cornerAnchors[c.refIndex].position;
                return true;
            }
        }

        /// <summary>Pick the closest area-tool candidate (lane endpoint OR corner anchor) to the
        /// cursor. Same XZ-only metric as <see cref="FindHoveredEndpoint"/>.</summary>
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
            return best;
        }

        /// <summary>Determine which kind of edge connects <paramref name="from"/> to
        /// <paramref name="to"/>. IMT-style: if both anchors are lane endpoints belonging to the
        /// same MarkingLine in the current node's buffer, the edge follows that line's Bezier
        /// (LineBezier). Otherwise a straight chord (Straight). Phase 6 MVP — defers the rarer
        /// IMT cases (cross-alignment EnterLine, intersection-vertex chains).</summary>
        private AreaEdgeKind ClassifyEdge(AreaCandidate from, AreaCandidate to)
        {
            if (from.kind != AreaAnchorKind.LaneEndpoint || to.kind != AreaAnchorKind.LaneEndpoint)
                return AreaEdgeKind.Straight;
            if (_selectedNode == Entity.Null) return AreaEdgeKind.Straight;
            if (!EntityManager.HasBuffer<MarkingLine>(_selectedNode)) return AreaEdgeKind.Straight;

            var fromEp = _endpoints[from.refIndex];
            var toEp   = _endpoints[to.refIndex];
            var lines = EntityManager.GetBuffer<MarkingLine>(_selectedNode, isReadOnly: true);
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = lines[i];
                bool fromOnLine = (ln.sourceEdge == fromEp.edge && ln.sourceGapIndex == fromEp.gapIndex)
                               || (ln.targetEdge == fromEp.edge && ln.targetGapIndex == fromEp.gapIndex);
                bool toOnLine   = (ln.sourceEdge == toEp.edge   && ln.sourceGapIndex == toEp.gapIndex)
                               || (ln.targetEdge == toEp.edge   && ln.targetGapIndex == toEp.gapIndex);
                if (fromOnLine && toOnLine) return AreaEdgeKind.LineBezier;
            }
            return AreaEdgeKind.Straight;
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

        /// <summary>Close the polygon (3+ vertices, last-click matched first). For 6b this only
        /// logs the contour — actual MarkingArea persistence + vanilla Area spawn happens in 6c.
        /// </summary>
        private void AreaClose()
        {
            // Fill in the LAST→FIRST edge kind from the last placed vertex back to the start.
            var last = _areaPolygon[_areaPolygon.Count - 1];
            AreaCandidate lastCand = new AreaCandidate { kind = last.kind, refIndex = last.refIndex };
            AreaCandidate firstCand = new AreaCandidate { kind = _areaPolygon[0].kind, refIndex = _areaPolygon[0].refIndex };
            last.edgeToNext = ClassifyEdge(lastCand, firstCand);
            _areaPolygon[_areaPolygon.Count - 1] = last;

            log.Info($"area: closed with {_areaPolygon.Count} vertices on node #{_selectedNode.Index} — TODO 6c: spawn Area entity");
            // Stage 6c will replace the line below with an actual spawn. For now we just exit the
            // mode and clear so the user can build another polygon.
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _state = State.NodeSelected;
        }

        // --- end Phase 6b helpers ----------------------------------------------------------

        /// <summary>Create-or-delete: if a line with matching endpoints (order-insensitive) already
        /// exists, remove it; otherwise append a new one. Matches Traffic-style toggle UX.
        ///
        /// On delete: also clears the MarkingSegment buffer. Otherwise old segments referencing
        /// the removed line's lineIndex would survive into the next topology recompute (which
        /// reads the updated MarkingLine list and would mis-index). TopologySystem rebuilds the
        /// segment buffer from scratch on next tick — cheaper than trying to surgically shift
        /// lineIndex values across the segment buffer.
        /// </summary>
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
                    // Wipe segments so TopologySystem rebuilds from the new line list — see XML
                    // comment above for the lineIndex-shift reason.
                    if (EntityManager.HasBuffer<MarkingSegment>(_selectedNode))
                        EntityManager.GetBuffer<MarkingSegment>(_selectedNode).Clear();
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
