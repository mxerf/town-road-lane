using System.Text;
using Colossal.Logging;
using Colossal.Mathematics;
using Colossal.UI.Binding;
using Game.Common;
using Game.Tools;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Stage 5d bridge between the in-game React panel and the C# tool / topology / emission stack.
    ///
    /// Publishes <c>TownRoadLane.GetToolState</c> — JSON snapshot of "what the panel needs to render":
    /// is the tool active, which node is selected, the line buffer, the segment buffer, the current
    /// default style. Updated every frame; React's useValue auto-resyncs.
    ///
    /// Accepts three commands from React:
    ///   - <c>ToggleSegment(lineIndex, segmentIndex)</c> — flip MarkingSegment.visible
    ///   - <c>SetLineStyle(lineIndex, style)</c>          — change MarkingLine.style
    ///   - <c>DeleteLine(lineIndex)</c>                    — remove a MarkingLine + its segments
    ///
    /// All structural changes mark the node Updated so the next-tick recompute + emission picks
    /// them up. None of these commands bypass the existing pipelines — they just edit the
    /// authoritative buffers, then let MarkingTopologySystem + MarkingSegmentEmissionSystem do
    /// their normal jobs. This means the same invariants (PathNode slot allocation, archetype
    /// sourcing, GC protection) hold automatically.
    ///
    /// UI bundle loading: handled by the game's normal UIModuleAsset pipeline. Our .mjs ships
    /// next to the .dll, exports a default ModRegistrar that appends components into
    /// GameTopLeft (toolbar button) + GameTopRight (panel) slots. No ExecuteScript hack
    /// needed — that was carried over from SystemTimeMod and caused a NullReferenceException
    /// in UIModuleAsset.PostCreate when AssetDatabase tried to register tags from an empty
    /// mod.json manifest.
    /// </summary>
    public partial class TownRoadLaneUISystem : UISystemBase
    {
        // Shadowing the inherited UISystemBase.log on purpose — Mod.log is the one wired into
        // CS2's mod-aware logger (file name, prefix) and matches the rest of the code in this
        // project. Using `new` to silence CS0108.
        private static new readonly ILog log = Mod.log;

        private GetterValueBinding<string> _stateBinding;
        private MarkingNodeToolSystem _tool;
        private DefaultToolSystem _defaultTool;
        private ToolSystem _toolSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            AddBinding(_stateBinding = new GetterValueBinding<string>(
                "TownRoadLane", "GetToolState", BuildStateJson));

            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "ToggleSegment", OnToggleSegment));
            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "SetLineStyle", OnSetLineStyle));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "DeleteLine", OnDeleteLine));
            AddBinding(new TriggerBinding(
                "TownRoadLane", "ActivateTool", OnActivateTool));

            log.Info("TownRoadLaneUISystem: bindings registered");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            // Republish state every tick — cheap, the binding only fires on JSON change.
            _stateBinding?.Update();
        }

        // --- State publishing ---

        private string BuildStateJson()
        {
            bool isActive = _toolSystem != null && _tool != null && _toolSystem.activeTool == _tool;
            int nodeIdx = (isActive && _tool.SelectedNode != Entity.Null) ? _tool.SelectedNode.Index : -1;
            int currentStyle = (int)(_tool?.CurrentStyle ?? MarkingStyle.Solid);

            var sb = new StringBuilder(512);
            sb.Append("{");
            sb.Append("\"isActive\":").Append(isActive ? "true" : "false").Append(",");
            sb.Append("\"selectedNodeIndex\":").Append(nodeIdx).Append(",");
            sb.Append("\"currentStyle\":").Append(currentStyle).Append(",");
            sb.Append("\"lines\":[");

            if (isActive && _tool.SelectedNode != Entity.Null
                && EntityManager.HasBuffer<MarkingLine>(_tool.SelectedNode))
            {
                var node = _tool.SelectedNode;
                var lines = EntityManager.GetBuffer<MarkingLine>(node, isReadOnly: true);
                var segs = EntityManager.HasBuffer<MarkingSegment>(node)
                    ? EntityManager.GetBuffer<MarkingSegment>(node, isReadOnly: true)
                    : default;

                // Pre-build per-line Beziers once so segment length comes out right (segment
                // length is the chord of the cut-out Bezier slice, in metres).
                var endpoints = MarkingEndpointExtractor.Extract(EntityManager, node);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    var line = lines[i];
                    sb.Append("{\"lineIndex\":").Append(i)
                        .Append(",\"style\":").Append(line.style)
                        .Append(",\"segments\":[");

                    bool bezOk = MarkingCurveBuilder.TryBuild(endpoints, line, out var fullBez);
                    int segCount = 0;
                    if (segs.IsCreated)
                    {
                        int perLineCounter = 0;
                        for (int s = 0; s < segs.Length; s++)
                        {
                            var seg = segs[s];
                            if (seg.lineIndex != i) continue;
                            if (segCount > 0) sb.Append(",");

                            float lengthM = 0f;
                            if (bezOk)
                            {
                                var cut = MathUtils.Cut(fullBez, new float2(seg.tStart, seg.tEnd));
                                lengthM = MathUtils.Length(cut);
                            }

                            sb.Append("{\"lineIndex\":").Append(i)
                                .Append(",\"segmentIndex\":").Append(perLineCounter)
                                .Append(",\"tStart\":").Append(seg.tStart.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                                .Append(",\"tEnd\":").Append(seg.tEnd.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                                .Append(",\"visible\":").Append(seg.visible ? "true" : "false")
                                .Append(",\"lengthM\":").Append(lengthM.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                                .Append("}");
                            perLineCounter++;
                            segCount++;
                        }
                    }
                    sb.Append("]}");
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        // --- Commands ---

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
            // Wipe segments — topology will rebuild with new style. The boundaries don't change
            // (style is visual-only), but emission re-targets segments to the new prefab archetype.
            if (EntityManager.HasBuffer<MarkingSegment>(node))
                EntityManager.GetBuffer<MarkingSegment>(node).Clear();
            // Bust the topology hash so MarkingTopologySystem re-emits on next tick. Without
            // this the hash equality short-circuits because lineIndex+endpoints didn't change.
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Info($"UI: set line#{lineIndex} style → {(MarkingStyle)style}");
        }

        private void OnDeleteLine(int lineIndex)
        {
            var node = _tool?.SelectedNode ?? Entity.Null;
            if (node == Entity.Null) { log.Warn("DeleteLine ignored — no node selected"); return; }
            if (!EntityManager.HasBuffer<MarkingLine>(node)) return;

            var lines = EntityManager.GetBuffer<MarkingLine>(node);
            if (lineIndex < 0 || lineIndex >= lines.Length) return;
            lines.RemoveAt(lineIndex);
            // Topology rebuild needs to see new lineIndex assignments — wipe segments + hash.
            if (EntityManager.HasBuffer<MarkingSegment>(node))
                EntityManager.GetBuffer<MarkingSegment>(node).Clear();
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Info($"UI: deleted line#{lineIndex} on node#{node.Index}");
        }
    }
}
