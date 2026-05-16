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
        private int _heartbeatCounter; // logs raycast outcome periodically when active

        public State ToolState => _state;
        public Entity SelectedNode => _selectedNode;
        public IReadOnlyList<MarkingEndpoint> Endpoints => _endpoints;
        public int SourceEndpointIndex => _sourceIdx;
        public int HoveredEndpointIndex => _hoverIdx;
        public float3 CursorWorldPos => _cursorWorldPos;

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
            log.Info($"MarkingNodeToolSystem: OnCreate — toolID='{toolID}' registered with ToolSystem.tools (count={m_ToolSystem.tools.Count})");
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
            log.Info($"MarkingNodeToolSystem: activated, actions enabled (apply={applyAction != null}, cancel={cancelAction != null})");
        }

        protected override void OnStopRunning()
        {
            log.Info($"MarkingNodeToolSystem: deactivated (state was {_state}, selectedNode #{_selectedNode.Index})");
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
            _hoverIdx = (_state != State.Default && hitSomething) ? FindHoveredEndpoint(_cursorWorldPos) : -1;

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
                    else
                    {
                        log.Info("tool: click in NodeSelected — no dot hovered, ignored");
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
            int existingPairs = EntityManager.HasBuffer<MarkingPair>(node)
                ? EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true).Length
                : 0;
            log.Info($"tool: selected node #{node.Index} — {_endpoints.Count} endpoint(s), {existingPairs} existing pair(s)");
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

        /// <summary>Create-or-delete: if a pair with matching endpoints (order-insensitive) already
        /// exists, remove it; otherwise append a new one. Matches Traffic-style toggle UX.</summary>
        private void TogglePair(MarkingEndpoint src, MarkingEndpoint dst)
        {
            if (!EntityManager.HasBuffer<MarkingPair>(_selectedNode))
            {
                EntityManager.AddBuffer<MarkingPair>(_selectedNode);
            }
            var buf = EntityManager.GetBuffer<MarkingPair>(_selectedNode);

            for (int i = 0; i < buf.Length; i++)
            {
                var p = buf[i];
                bool sameDirection  = p.sourceEdge == src.edge && p.sourceGapIndex == src.gapIndex && p.targetEdge == dst.edge && p.targetGapIndex == dst.gapIndex;
                bool swappedSides   = p.sourceEdge == dst.edge && p.sourceGapIndex == dst.gapIndex && p.targetEdge == src.edge && p.targetGapIndex == src.gapIndex;
                if (sameDirection || swappedSides)
                {
                    log.Info($"tool: toggled OFF pair #{i} on node #{_selectedNode.Index}");
                    buf.RemoveAt(i);
                    if (!EntityManager.HasComponent<Updated>(_selectedNode))
                        EntityManager.AddComponent<Updated>(_selectedNode);
                    return;
                }
            }

            buf.Add(new MarkingPair
            {
                sourceEdge = src.edge, sourceGapIndex = src.gapIndex,
                targetEdge = dst.edge, targetGapIndex = dst.gapIndex,
            });
            if (!EntityManager.HasComponent<Updated>(_selectedNode))
                EntityManager.AddComponent<Updated>(_selectedNode);
            log.Info($"tool: toggled ON pair #{buf.Length - 1} on node #{_selectedNode.Index} — "
                + $"src(edge=#{src.edge.Index} gap={src.gapIndex}) → "
                + $"dst(edge=#{dst.edge.Index} gap={dst.gapIndex})");
        }

    }
}
