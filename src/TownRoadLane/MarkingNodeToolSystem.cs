using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 4 tool: per-node marking customisation. Activated via hotkey
    /// (<see cref="MarkingToolHotkeySystem"/>). State machine mirrors Traffic's
    /// LaneConnectorToolSystem (RESEARCH_traffic.md §5):
    ///
    ///   Default          — hovering over the world; click a node to select it.
    ///   SourceSelected   — a node + source endpoint connector are picked; drag to a target.
    ///   TargetHovered    — currently hovering a valid target endpoint; click commits the pair.
    ///
    /// Phase 4b ships ONLY the scaffold: raycast to nodes, log the selected entity on click,
    /// cancel on Esc. Endpoint extraction (4c), overlay rendering (4d), and the actual
    /// click-to-connect mechanic (4e) come in their own commits.
    /// </summary>
    public partial class MarkingNodeToolSystem : ToolBaseSystem
    {
        private static readonly ILog log = Mod.log;

        public enum State
        {
            Default,
            SourceSelected,
            TargetHovered,
        }

        public override string toolID => "MarkingNodeTool";

        private State _state;
        private Entity _selectedNode;

        public State ToolState => _state;
        public Entity SelectedNode => _selectedNode;

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
            // Disable by default — activeTool gets flipped to us only via the hotkey path.
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            _state = State.Default;
            _selectedNode = Entity.Null;
            log.Info("MarkingNodeToolSystem: activated");
        }

        protected override void OnStopRunning()
        {
            log.Info($"MarkingNodeToolSystem: deactivated (state was {_state}, selectedNode #{_selectedNode.Index})");
            _state = State.Default;
            _selectedNode = Entity.Null;
            base.OnStopRunning();
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            // We want to hit road network entities. Filter to nodes at GetRaycastResult time
            // (TypeMask.Net covers edges + nodes + sublanes; we only accept Node-tagged hits).
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.PublicTransportRoad | Layer.SubwayTrack | Layer.TrainTrack | Layer.TramTrack;
            m_ToolRaycastSystem.raycastFlags |= RaycastFlags.SubElements;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Cancel: Esc or right-click → deactivate the tool altogether (back to default tool).
            if (cancelAction.WasPressedThisFrame())
            {
                log.Info("MarkingNodeToolSystem: cancel pressed → returning to default tool");
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return inputDeps;
            }

            // Phase 4b: just log node click. Real selection logic lands in 4c+4e.
            if (applyAction.WasPressedThisFrame())
            {
                RaycastHit _hit;
                if (GetRaycastResult(out Entity hitEntity, out _hit))
                {
                    if (EntityManager.HasComponent<Node>(hitEntity))
                    {
                        _selectedNode = hitEntity;
                        log.Info($"MarkingNodeToolSystem: clicked node #{hitEntity.Index} (state={_state})");
                    }
                    else
                    {
                        log.Info($"MarkingNodeToolSystem: clicked non-node entity #{hitEntity.Index} — ignored (only nodes are valid)");
                    }
                }
            }

            return inputDeps;
        }
    }
}
