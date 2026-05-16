using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Common;
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
            log.Info("MarkingNodeToolSystem: activated");
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
            // Polish: log hover transitions only (avoid per-frame spam). Useful for triage of
            // "I'm hovering but the dot doesn't react" reports.
            if (_hoverIdx != _lastLoggedHoverIdx)
            {
                if (_hoverIdx >= 0)
                {
                    var ep = _endpoints[_hoverIdx];
                    log.Info($"tool: hover endpoint idx={_hoverIdx} edge=#{ep.edge.Index} lane={ep.laneIndex} isRight={ep.isRight}");
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

            // Secondary apply (RMB): delete pair whose source OR target is the hovered endpoint
            // (only meaningful in NodeSelected).
            if (secondaryApplyAction.WasPressedThisFrame() && _state == State.NodeSelected && _hoverIdx >= 0)
            {
                TryDeletePairAt(_endpoints[_hoverIdx]);
                return inputDeps;
            }

            // Primary apply (LMB): state-machine transitions.
            if (applyAction.WasPressedThisFrame())
            {
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
                        log.Info($"tool: source endpoint chosen — idx={_sourceIdx} edge=#{_endpoints[_sourceIdx].edge.Index} laneIdx={_endpoints[_sourceIdx].laneIndex} isRight={_endpoints[_sourceIdx].isRight}");
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
                        CommitPair(_endpoints[_sourceIdx], _endpoints[_hoverIdx]);
                        // Stay in SourceSelected? Match Traffic UX: drop back to NodeSelected so the
                        // user can pick a new source for the next pair.
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
            _endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);
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

        private void CommitPair(MarkingEndpoint src, MarkingEndpoint dst)
        {
            if (!EntityManager.HasBuffer<MarkingPair>(_selectedNode))
            {
                EntityManager.AddBuffer<MarkingPair>(_selectedNode);
            }
            var buf = EntityManager.GetBuffer<MarkingPair>(_selectedNode);
            buf.Add(new MarkingPair
            {
                sourceEdge = src.edge, sourceLaneIndex = src.laneIndex, sourceIsRight = src.isRight,
                targetEdge = dst.edge, targetLaneIndex = dst.laneIndex, targetIsRight = dst.isRight,
            });
            // Mark the node Updated so CustomSecondaryLaneSystem reprocesses it on the next tick
            // and the user sees the new state immediately (vanilla suppression kicks in).
            if (!EntityManager.HasComponent<Updated>(_selectedNode))
                EntityManager.AddComponent<Updated>(_selectedNode);
            log.Info($"tool: committed pair #{buf.Length - 1} on node #{_selectedNode.Index} — "
                + $"src(edge=#{src.edge.Index} lane={src.laneIndex} right={src.isRight}) → "
                + $"dst(edge=#{dst.edge.Index} lane={dst.laneIndex} right={dst.isRight})");
        }

        private void TryDeletePairAt(MarkingEndpoint ep)
        {
            if (!EntityManager.HasBuffer<MarkingPair>(_selectedNode)) return;
            var buf = EntityManager.GetBuffer<MarkingPair>(_selectedNode);
            for (int i = 0; i < buf.Length; i++)
            {
                var p = buf[i];
                bool matchSrc = p.sourceEdge == ep.edge && p.sourceLaneIndex == ep.laneIndex && p.sourceIsRight == ep.isRight;
                bool matchDst = p.targetEdge == ep.edge && p.targetLaneIndex == ep.laneIndex && p.targetIsRight == ep.isRight;
                if (matchSrc || matchDst)
                {
                    log.Info($"tool: deleting pair #{i} on node #{_selectedNode.Index} (matched {(matchSrc ? "source" : "target")} side)");
                    buf.RemoveAt(i);
                    if (!EntityManager.HasComponent<Updated>(_selectedNode))
                        EntityManager.AddComponent<Updated>(_selectedNode);
                    return;
                }
            }
            log.Info($"tool: RMB at endpoint idx={_hoverIdx} but no pair found to delete");
        }
    }
}
