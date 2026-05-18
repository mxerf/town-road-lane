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
        }

        public override string toolID => "MarkingNodeTool";

        // Selection radius for screen-space dot pick: square of meters in world space, applied
        // after projecting the cursor's terrain hit onto the dot's plane. ~1.5m matches the
        // visual dot diameter (1.4m) with a small tolerance.
        private const float kDotPickRadiusSq = 1.5f * 1.5f;

        private State _state;
        private Entity _selectedNode;
        private List<MarkingEndpoint> _endpoints = new List<MarkingEndpoint>();
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
                _cycleStyleAction = Mod.Settings.GetAction(Setting.CycleMarkingStyle);
            log.Info($"MarkingNodeToolSystem: OnCreate — toolID='{toolID}' registered with ToolSystem.tools (count={m_ToolSystem.tools.Count}), cycleStyleAction={(_cycleStyleAction != null ? "OK" : "NULL")}");
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
            log.Info($"MarkingNodeToolSystem: activated, actions enabled (apply={applyAction != null}, cancel={cancelAction != null}, cycleStyle={_cycleStyleAction != null})");
        }

        protected override void OnStopRunning()
        {
            log.Info($"MarkingNodeToolSystem: deactivated (state was {_state}, selectedNode #{_selectedNode.Index})");
            if (_cycleStyleAction != null) _cycleStyleAction.shouldBeEnabled = false;
            ResetSelection();
            base.OnStopRunning();
        }

        private void ResetSelection()
        {
            _state = State.Default;
            _selectedNode = Entity.Null;
            _endpoints.Clear();
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
            _hoverIdx = (_state != State.Default && hitSomething) ? FindHoveredEndpoint(_cursorWorldPos) : -1;

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

            // Cancel (Esc / RMB-as-cancel): step back one state.
            if (cancelAction.WasPressedThisFrame())
            {
                if (_state == State.SourceSelected)
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
            }

            return inputDeps;
        }

        private void SelectNode(Entity node)
        {
            _selectedNode = node;
            // Verbose extract — writes a per-edge / per-lane breakdown so we can debug missing
            // endpoints without re-deploying. Cheap (only fires on node click).
            _endpoints = MarkingEndpointExtractor.Extract(EntityManager, node, log: true);
            _sourceIdx = -1;
            _state = State.NodeSelected;
            int existingLines = EntityManager.HasBuffer<MarkingLine>(node)
                ? EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true).Length
                : 0;
            int existingSegs = EntityManager.HasBuffer<MarkingSegment>(node)
                ? EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true).Length
                : 0;
            log.Info($"tool: selected node #{node.Index} — {_endpoints.Count} endpoint(s), {existingLines} line(s), {existingSegs} segment(s)");
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
