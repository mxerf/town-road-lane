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
    /// The marking tool: per-node line and area editing, toggled by
    /// <see cref="MarkingToolHotkeySystem"/>. The state machine follows Traffic's
    /// LaneConnectorToolSystem:
    ///
    ///   Default: no node selected; click a node to select it.
    ///   NodeSelected: dots shown; click a dot to start a line.
    ///   SourceSelected: click a second dot to create the line, or remove it if it exists.
    ///   AreaSelecting: placing the vertices of an area polygon.
    ///
    /// Cancel (Esc) steps back one state. Every state transition is logged.
    /// </summary>
    public partial class MarkingNodeToolSystem : ToolBaseSystem
    {
        private static readonly ILog log = Mod.log;

        public enum State
        {
            Default,
            NodeSelected,
            SourceSelected,
            // Entered from NodeSelected via the area hotkey or the panel button. A click adds a
            // vertex; clicking the start vertex with 3+ placed closes and commits the area.
            // Right-click removes the last vertex, or leaves the mode if there is none. Esc
            // cancels.
            AreaSelecting,
        }

        // What an area-polygon vertex refers to. Kind plus refIndex (see AreaCandidate) lets one
        // hit-test pass cover every anchor type.
        public enum AreaAnchorKind
        {
            LaneEndpoint,     // MarkingEndpoint index in _endpoints
            NodeCorner,       // MarkingCornerAnchor index in _cornerAnchors
            // A crossing of two lines. refIndex is the packed (lineA, lineB, hitIndex) value from
            // MarkingIntersectionExtractor.Pack, not a list index, so it can be stored in
            // MarkingAreaVertex as is and stays valid when lines are added or curvature changes.
            LineIntersection,
        }

        // A placed vertex of the area being drawn. The anchor reference lets positions be rebuilt
        // after a topology change. edgeToNext is set once the following vertex is picked, so on
        // the last vertex it stays unresolved until the next click or the closing click.
        public struct AreaPolygonVertex
        {
            public AreaAnchorKind kind;
            public int refIndex;
            public AreaEdgeKind edgeToNext;
            public float3 position;  // cached at click time to keep the overlay cheap
        }

        // Edge between two consecutive area vertices. Stored in logical form; curved edges are
        // sampled into a polyline later.
        public enum AreaEdgeKind
        {
            Straight,    // direct chord between the two anchor positions
            LineBezier,  // both anchors lie on the same MarkingLine: follow that line's curve
        }

        // Hover or pick target in area mode. None is refIndex == -1.
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

        // Squared pick radius for dots, in metres, measured in the XZ plane from the cursor's
        // raycast hit. Deliberately larger than the drawn dot.
        private const float kDotPickRadiusSq = 1.5f * 1.5f;

        private State _state;
        private Entity _selectedNode;
        private List<MarkingEndpoint> _endpoints = new List<MarkingEndpoint>();
        // Corner anchors where the kerbs of neighbouring edges meet. Used only by area mode.
        private List<MarkingCornerAnchor> _cornerAnchors = new List<MarkingCornerAnchor>();

        // Line-crossing anchors for area mode. Re-extracted whenever the node's line topology
        // hash changes, since adding or deleting a line or editing curvature moves the crossings
        // (see RefreshIntersectionAnchorsIfStale).
        private List<MarkingIntersectionAnchor> _lineIntersections = new List<MarkingIntersectionAnchor>();
        private int _lineIntersectionsHash;

        // Vertices placed so far in AreaSelecting; committed as a MarkingArea on the closing
        // click and cleared on exit, cancel or commit.
        private List<AreaPolygonVertex> _areaPolygon = new List<AreaPolygonVertex>();
        // Candidate under the cursor in AreaSelecting.
        private AreaCandidate _areaHover = AreaCandidate.None;
        private int _sourceIdx = -1;
        private int _hoverIdx  = -1;
        private int _lastLoggedHoverIdx = -2; // -2 = "never logged"; -1 = "no hover"
        private float3 _cursorWorldPos;
        private Entity _hoveredNode; // raycast result while tool is active; Entity.Null when no node under cursor

        // Style for the next line drawn. Kept when the tool is closed and reopened within a
        // session, so a style picked once stays picked.
        private MarkingStyle _currentStyle = MarkingStyle.Solid;
        private ProxyAction _cycleStyleAction;

        private ProxyAction _enterAreaAction;
        // Fill style for the next closed area, the area counterpart of _currentStyle.
        private ProxyAction _cycleAreaStyleAction;
        private int _currentAreaStyle = 0;
        public int CurrentAreaStyle => _currentAreaStyle;

        // A click in NodeSelected that misses every dot is hit-tested against committed lines,
        // and the panel expands the row of the line that was hit. The panel watches the tick,
        // so clicking the same line twice still registers. Line -1 means empty space: collapse
        // all rows.
        private int _lastClickedLine = -1;
        private int _lastClickedTick;

        // Line under the cursor in NodeSelected, for the overlay highlight. Kept apart from the
        // UI hover so hovering the panel doesn't override it; the overlay prefers the UI hover.
        private int _hoveredLineInGame = -1;

        // Area the cursor is inside (NodeSelected only, and only when no dot or line is hovered,
        // since those are more specific). -1 means none. Highlights the panel row and the overlay.
        private int _hoveredAreaInGame = -1;
        // Reused point-in-polygon ring, to avoid per-frame allocations.
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

        // Entry points for the panel controls that mirror the hotkeys (TownRoadLaneUISystem).
        // They are called from TriggerBinding handlers on the main thread, in the same phase as
        // OnUpdate, so no synchronisation is needed.

        /// <summary>Sets the style for the next line drawn (the state the style hotkey cycles).</summary>
        public void SetCurrentStyle(MarkingStyle style)
        {
            _currentStyle = style;
            log.Debug($"tool: UI set next-line style → {_currentStyle}");
        }

        /// <summary>Sets the fill style for the next closed area (the state the area-style hotkey
        /// cycles). A disabled style falls back to solid concrete.</summary>
        public void SetCurrentAreaStyle(int styleId)
        {
            _currentAreaStyle = math.clamp(styleId, 0, MarkingAreaEmissionSystem.kStyleCount - 1);
            if (!MarkingAreaEmissionSystem.IsStyleEnabled(_currentAreaStyle))
                _currentAreaStyle = MarkingAreaEmissionSystem.kStyleSolidConcrete;
            log.Debug($"tool: UI set next-area style → {_currentAreaStyle}");
        }

        /// <summary>Panel "Area" button. Drops a half-picked line first, so it works from any
        /// state with a selected node; returns false in Default.</summary>
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
            log.Debug($"area: entered AreaSelecting via UI on node #{_selectedNode.Index}");
            return true;
        }

        /// <summary>Panel "Lines" button or area-mode cancel: back to NodeSelected, dropping any
        /// placed vertices.</summary>
        public void ExitAreaMode()
        {
            if (_state != State.AreaSelecting) return;
            log.Debug($"area: exited AreaSelecting via UI (had {_areaPolygon.Count} vertices)");
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _state = State.NodeSelected;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            // The actions stay disabled until OnStartRunning, so their keys aren't intercepted
            // while the tool is inactive.
            if (Mod.Settings != null)
            {
                _cycleStyleAction = Mod.Settings.GetAction(TownRoadLaneSetting.CycleMarkingStyle);
                _enterAreaAction = Mod.Settings.GetAction(TownRoadLaneSetting.EnterAreaMode);
                _cycleAreaStyleAction = Mod.Settings.GetAction(TownRoadLaneSetting.CycleAreaStyle);
            }
            log.Info($"MarkingNodeToolSystem: OnCreate — toolID='{toolID}' registered with ToolSystem.tools (count={m_ToolSystem.tools.Count}), cycleStyleAction={(_cycleStyleAction != null ? "OK" : "NULL")}, enterAreaAction={(_enterAreaAction != null ? "OK" : "NULL")}, cycleAreaStyleAction={(_cycleAreaStyleAction != null ? "OK" : "NULL")}");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            ResetSelection();
            // The base UpdateActions is empty, so applyAction and friends stay disabled from the
            // last ResetActions and clicks do nothing. Enable them the way DefaultToolSystem does,
            // minus its internal DeferStateUpdating batching, which isn't accessible from a mod.
            applyAction.shouldBeEnabled = true;
            secondaryApplyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            if (_cycleStyleAction != null) _cycleStyleAction.shouldBeEnabled = true;
            if (_enterAreaAction != null) _enterAreaAction.shouldBeEnabled = true;
            if (_cycleAreaStyleAction != null) _cycleAreaStyleAction.shouldBeEnabled = true;
            log.Debug($"MarkingNodeToolSystem: activated, actions enabled (apply={applyAction != null}, cancel={cancelAction != null}, cycleStyle={_cycleStyleAction != null})");
        }

        protected override void OnStopRunning()
        {
            log.Debug($"MarkingNodeToolSystem: deactivated (state was {_state}, selectedNode #{_selectedNode.Index})");
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
            _lastClickedLine = -1; // so a new selection doesn't expand a stale panel row
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
            RaycastHit hit;
            bool hitSomething = GetRaycastResult(out Entity hitEntity, out hit);
            _cursorWorldPos = hitSomething ? hit.m_HitPosition : float3.zero;
            _hoveredNode = (hitSomething && EntityManager.HasComponent<Node>(hitEntity)) ? hitEntity : Entity.Null;
            _hoverIdx = (_state != State.Default && _state != State.AreaSelecting && hitSomething) ? FindHoveredEndpoint(_cursorWorldPos) : -1;
            // Lines can change from the panel while an area is being drawn.
            if (_state == State.AreaSelecting) RefreshIntersectionAnchorsIfStale();
            _areaHover = (_state == State.AreaSelecting && hitSomething) ? FindHoveredAreaCandidate(_cursorWorldPos) : AreaCandidate.None;

            // Skipped in SourceSelected, where the cursor is aiming at a target dot and line
            // highlights would be noise.
            _hoveredLineInGame = (_state == State.NodeSelected && _hoverIdx < 0 && hitSomething)
                ? HitTestLines(_cursorWorldPos)
                : -1;

            // Dots and lines are more specific targets, so they take priority over areas.
            _hoveredAreaInGame = (_state == State.NodeSelected && _hoverIdx < 0 && _hoveredLineInGame < 0 && hitSomething)
                ? HitTestAreas(_cursorWorldPos)
                : -1;

            // Log hover changes only, not every frame.
            if (_hoverIdx != _lastLoggedHoverIdx)
            {
                if (_hoverIdx >= 0)
                {
                    var ep = _endpoints[_hoverIdx];
                    log.Debug($"tool: hover endpoint idx={_hoverIdx} edge=#{ep.edge.Index} gap={ep.gapIndex}");
                }
                else if (_lastLoggedHoverIdx >= 0)
                {
                    log.Debug($"tool: hover cleared (was idx={_lastLoggedHoverIdx})");
                }
                _lastLoggedHoverIdx = _hoverIdx;
            }

            // Works in any state; affects only lines created afterwards.
            if (_cycleStyleAction != null && _cycleStyleAction.WasPerformedThisFrame())
            {
                _currentStyle = NextStyle(_currentStyle);
                log.Debug($"tool: cycled style → {_currentStyle}");
            }

            if (_cycleAreaStyleAction != null && _cycleAreaStyleAction.WasPerformedThisFrame())
            {
                _currentAreaStyle = MarkingAreaEmissionSystem.NextEnabledStyle(_currentAreaStyle);
                log.Debug($"tool: cycled area style → {_currentAreaStyle}");
            }

            // The area hotkey enters area mode, and pressing it again leaves without committing.
            if (_enterAreaAction != null && _enterAreaAction.WasPerformedThisFrame())
            {
                if (_state == State.NodeSelected)
                {
                    _state = State.AreaSelecting;
                    _areaPolygon.Clear();
                    _areaHover = AreaCandidate.None;
                    log.Debug($"area: entered AreaSelecting on node #{_selectedNode.Index}");
                }
                else if (_state == State.AreaSelecting)
                {
                    log.Debug($"area: cancelled AreaSelecting via hotkey (had {_areaPolygon.Count} vertices)");
                    _areaPolygon.Clear();
                    _areaHover = AreaCandidate.None;
                    _state = State.NodeSelected;
                }
            }

            // Cancel steps back one state; from Default it closes the tool.
            if (cancelAction.WasPressedThisFrame())
            {
                if (_state == State.AreaSelecting)
                {
                    log.Debug($"area: cancelled AreaSelecting via Esc (had {_areaPolygon.Count} vertices)");
                    _areaPolygon.Clear();
                    _areaHover = AreaCandidate.None;
                    _state = State.NodeSelected;
                }
                else if (_state == State.SourceSelected)
                {
                    log.Debug($"tool: cancel — clearing source #{_sourceIdx}");
                    _sourceIdx = -1;
                    _state = State.NodeSelected;
                }
                else if (_state == State.NodeSelected)
                {
                    log.Debug($"tool: cancel — deselecting node #{_selectedNode.Index}");
                    ResetSelection();
                }
                else
                {
                    log.Debug("tool: cancel from Default — deactivating tool");
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                }
                return inputDeps;
            }

            // Right-click in area mode undoes the last vertex, as in IMT.
            if (_state == State.AreaSelecting && secondaryApplyAction.WasPressedThisFrame())
            {
                if (_areaPolygon.Count > 0)
                {
                    _areaPolygon.RemoveAt(_areaPolygon.Count - 1);
                    log.Debug($"area: popped last vertex, {_areaPolygon.Count} remaining");
                }
                else
                {
                    log.Debug("area: RMB on empty contour → leave AreaSelecting");
                    _state = State.NodeSelected;
                }
                return inputDeps;
            }

            // Outside area mode right-click does nothing: lines are removed by repeating the
            // create gesture (see TogglePair).

            if (applyAction.WasPressedThisFrame())
            {
                // Tells apart a missing apply event, a click with no raycast hit, and a hit on
                // something that isn't a node.
                log.Debug($"tool: LMB fired — state={_state}, hitSomething={hitSomething}, hitEntity=#{(hitSomething ? hitEntity.Index : -1)}, hasNode={(hitSomething && EntityManager.HasComponent<Node>(hitEntity))}");
                if (_state == State.Default)
                {
                    if (hitSomething && EntityManager.HasComponent<Node>(hitEntity))
                    {
                        SelectNode(hitEntity);
                    }
                    else
                    {
                        log.Debug($"tool: click in Default ignored (hit #{(hitSomething ? hitEntity.Index : -1)}, no Node)");
                    }
                }
                else if (_state == State.NodeSelected)
                {
                    if (_hoverIdx >= 0)
                    {
                        _sourceIdx = _hoverIdx;
                        _state = State.SourceSelected;
                        log.Debug($"tool: source endpoint chosen — idx={_sourceIdx} edge=#{_endpoints[_sourceIdx].edge.Index} gap={_endpoints[_sourceIdx].gapIndex}");
                    }
                    else if (hitSomething && EntityManager.HasComponent<Node>(hitEntity) && hitEntity != _selectedNode)
                    {
                        SelectNode(hitEntity);
                    }
                    else if (hitSomething)
                    {
                        // Neither a dot nor another node: expand the panel row of the line
                        // under the cursor, if any.
                        int clickedLine = HitTestLines(_cursorWorldPos);
                        _lastClickedLine = clickedLine;
                        _lastClickedTick++;
                        log.Debug($"tool: click on line #{clickedLine} (or -1 = empty space)");
                    }
                    else
                    {
                        log.Debug("tool: click in NodeSelected — no dot/raycast, ignored");
                    }
                }
                else if (_state == State.SourceSelected)
                {
                    if (_hoverIdx >= 0 && _hoverIdx != _sourceIdx)
                    {
                        TogglePair(_endpoints[_sourceIdx], _endpoints[_hoverIdx]);
                        _sourceIdx = -1;
                        _state = State.NodeSelected;
                    }
                    else
                    {
                        log.Debug("tool: click in SourceSelected — no different target dot hovered, ignored");
                    }
                }
                else if (_state == State.AreaSelecting)
                {
                    if (!_areaHover.IsValid)
                    {
                        log.Debug("area: LMB with no hovered candidate, ignored");
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
            // Logs a per-edge, per-lane breakdown, to debug missing endpoints from a user's log.
            // Runs only on node click.
            _endpoints = MarkingEndpointExtractor.Extract(EntityManager, node, log: true);
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
            log.Debug($"tool: selected node #{node.Index} — {_endpoints.Count} endpoint(s), {_cornerAnchors.Count} corner(s), {existingLines} line(s), {existingSegs} segment(s)");
        }

        private int FindHoveredEndpoint(float3 cursor)
        {
            int best = -1;
            float bestSq = kDotPickRadiusSq;
            for (int i = 0; i < _endpoints.Count; i++)
            {
                float3 d = _endpoints[i].position - cursor;
                // XZ only: the raycast hit and the dot can be at different heights.
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }

        // Squared XZ distance within which the cursor counts as being on a line.
        private const float kLinePickRadiusSq = 2.0f * 2.0f;
        // Samples per Bezier: about 1 m spacing on a typical 10-12 m line.
        private const int   kLineSampleCount  = 12;

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
                    float3 p = Colossal.Mathematics.MathUtils.Position(bez, t);
                    float dx = p.x - cursor.x;
                    float dz = p.z - cursor.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
            }
            return best;
        }

        // Area mode.

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

        /// <summary>Closest area candidate (lane endpoint, corner anchor or line crossing) to the
        /// cursor, measured in XZ like <see cref="FindHoveredEndpoint"/>.</summary>
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

        /// <summary>Re-extracts the line-crossing anchors and remembers the topology hash they
        /// were built from.</summary>
        private void RefreshIntersectionAnchors()
        {
            _lineIntersections = MarkingIntersectionExtractor.ExtractAll(EntityManager, _selectedNode);
            _lineIntersectionsHash = _selectedNode != Entity.Null && EntityManager.HasComponent<MarkingTopologyState>(_selectedNode)
                ? EntityManager.GetComponentData<MarkingTopologyState>(_selectedNode).linesHash
                : 0;
        }

        /// <summary>Per-frame check in area mode. When the line topology hash changes (a line
        /// added or deleted, curvature edited), re-extracts the crossings and refreshes the cached
        /// positions of placed vertices, so the contour follows the lines.</summary>
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

        /// <summary>Curve parameter t of a placed anchor on the given line: 0 or 1 for the line's
        /// source or target endpoint, the crossing's own t for a crossing.</summary>
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

        /// <summary>Adds a vertex and resolves the previous vertex's edgeToNext, now that the
        /// anchor it connects to is known.</summary>
        private void AreaAddVertex(AreaCandidate c)
        {
            if (!c.IsValid) return;
            if (!TryGetAreaAnchorPos(c, out var pos)) return;

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
                edgeToNext = AreaEdgeKind.Straight,  // placeholder until the next click or closure
            });
            log.Debug($"area: vertex {_areaPolygon.Count} added (kind={c.kind}, ref={c.refIndex})");
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
            AreaCandidate lastCand = new AreaCandidate { kind = last.kind, refIndex = last.refIndex };
            AreaCandidate firstCand = new AreaCandidate { kind = _areaPolygon[0].kind, refIndex = _areaPolygon[0].refIndex };
            last.edgeToNext = ClassifyEdge(lastCand, firstCand);
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
            {
                var pv = _areaPolygon[i];
                var av = new MarkingAreaVertex
                {
                    kind = (byte)pv.kind,
                    refIndex = pv.refIndex,
                    edgeToNext = (byte)pv.edgeToNext,
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
                verts.Add(av);
            }
            areas.Add(new MarkingArea
            {
                styleId = _currentAreaStyle,
                visible = true,
                firstVertex = firstVertex,
                vertexCount = _areaPolygon.Count,
            });

            // Updated makes MarkingAreaEmissionSystem pick up the change next frame.
            if (!EntityManager.HasComponent<Updated>(_selectedNode))
                EntityManager.AddComponent<Updated>(_selectedNode);

            log.Debug($"area: closed with {_areaPolygon.Count} vertices on node #{_selectedNode.Index} — buffer now has {areas.Length} area(s)");
            _areaPolygon.Clear();
            _areaHover = AreaCandidate.None;
            _state = State.NodeSelected;
        }

        /// <summary>Removes the line between the two endpoints (in either direction) if it
        /// exists, otherwise adds it, as in Traffic. On removal,
        /// MarkingTopologySystem.OnLineRemoved reindexes the segment buffer so per-segment
        /// overrides on the other lines survive.</summary>
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
                    log.Debug($"tool: toggled OFF line #{i} on node #{_selectedNode.Index}");
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
                curvature = MarkingCurveBuilder.AdaptivePullFactor(src, dst),
            });
            if (!EntityManager.HasComponent<Updated>(_selectedNode))
                EntityManager.AddComponent<Updated>(_selectedNode);
            log.Debug($"tool: toggled ON line #{buf.Length - 1} on node #{_selectedNode.Index} style={_currentStyle} — "
                + $"src(edge=#{src.edge.Index} gap={src.gapIndex}) → "
                + $"dst(edge=#{dst.edge.Index} gap={dst.gapIndex})");
        }

        /// <summary>Next <see cref="MarkingStyle"/> value, wrapping around. New enum values join
        /// the cycle automatically.</summary>
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
