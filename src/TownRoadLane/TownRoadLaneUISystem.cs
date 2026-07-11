using System.Text;
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

        protected override void OnCreate()
        {
            base.OnCreate();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            AddBinding(_stateBinding = new GetterValueBinding<string>(
                "TownRoadLane", "GetToolState", BuildStateJson));

            // i18n locale binding — React reads this to pick which dictionary
            // (en-US, ru-RU, ...) to render strings from. We re-publish on every
            // tick; the binding only fires when the value actually changes, so
            // this is cheap. Falls back to en-US if the locale manager isn't
            // initialized yet (early load / unit-test contexts).
            AddBinding(new GetterValueBinding<string>(
                "TownRoadLane", "GetLocale", GetActiveLocale));

            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "ToggleSegment", OnToggleSegment));
            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "SetLineStyle", OnSetLineStyle));
            AddBinding(new TriggerBinding<int, int, int>(
                "TownRoadLane", "SetSegmentStyle", OnSetSegmentStyle));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "DeleteLine", OnDeleteLine));
            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "SetLineCurvature", OnSetLineCurvature));
            AddBinding(new TriggerBinding(
                "TownRoadLane", "ToggleVanillaMarkings", OnToggleVanillaMarkings));
            AddBinding(new TriggerBinding(
                "TownRoadLane", "ActivateTool", OnActivateTool));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "SetCurrentStyle", OnSetCurrentStyle));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "SetCurrentAreaStyle", OnSetCurrentAreaStyle));
            AddBinding(new TriggerBinding(
                "TownRoadLane", "ToggleAreaMode", OnToggleAreaMode));
            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "SetAreaStyle", OnSetAreaStyle));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "ToggleAreaVisible", OnToggleAreaVisible));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "DeleteArea", OnDeleteArea));
            AddBinding(new TriggerBinding(
                "TownRoadLane", "ResetNode", OnResetNode));
            AddBinding(new TriggerBinding<int>(
                "TownRoadLane", "SetHoveredLine", OnSetHoveredLine));
            AddBinding(new TriggerBinding<int, int>(
                "TownRoadLane", "SetHoveredSegment", OnSetHoveredSegment));

            log.Info("TownRoadLaneUISystem: bindings registered");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            // Republish state every tick — cheap, the binding only fires on JSON change.
            _stateBinding?.Update();
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

        private string BuildStateJson()
        {
            bool isActive = _toolSystem != null && _tool != null && _toolSystem.activeTool == _tool;
            int nodeIdx = (isActive && _tool.SelectedNode != Entity.Null) ? _tool.SelectedNode.Index : -1;
            int currentStyle = (int)(_tool?.CurrentStyle ?? MarkingStyle.Solid);

            var sb = new StringBuilder(512);
            int clickedLine = _tool?.LastClickedLine ?? -1;
            int clickedTick = _tool?.LastClickedTick ?? 0;
            // Game→UI hover bridge (Phase B5): which line the cursor is currently
            // hovering in the world. React mirrors this to highlight the matching
            // line row in the panel, so the panel ↔ world correlation works both
            // ways. -1 = nothing hovered.
            int gameHoveredLine = _tool?.HoveredLineInGame ?? -1;

            // Vanilla-marking override state on the selected node — drives the panel toggle.
            bool vanillaHidden = false;
            if (isActive && _tool.SelectedNode != Entity.Null
                && EntityManager.HasComponent<MarkingOverride>(_tool.SelectedNode))
            {
                vanillaHidden = EntityManager.GetComponentData<MarkingOverride>(_tool.SelectedNode).HideAll;
            }

            // Tool sub-state for the panel's mode UI: 0 Default, 1 NodeSelected,
            // 2 SourceSelected, 3 AreaSelecting (mirrors MarkingNodeToolSystem.State).
            int toolState = isActive ? (int)_tool.ToolState : 0;
            int areaVertexCount = isActive ? (_tool.AreaPolygon?.Count ?? 0) : 0;
            int currentAreaStyle = _tool?.CurrentAreaStyle ?? 0;

            sb.Append("{");
            sb.Append("\"isActive\":").Append(isActive ? "true" : "false").Append(",");
            sb.Append("\"toolState\":").Append(toolState).Append(",");
            sb.Append("\"areaVertexCount\":").Append(areaVertexCount).Append(",");
            sb.Append("\"currentAreaStyle\":").Append(currentAreaStyle).Append(",");
            sb.Append("\"selectedNodeIndex\":").Append(nodeIdx).Append(",");
            sb.Append("\"currentStyle\":").Append(currentStyle).Append(",");
            sb.Append("\"vanillaHidden\":").Append(vanillaHidden ? "true" : "false").Append(",");
            sb.Append("\"lastClickedLine\":").Append(clickedLine).Append(",");
            sb.Append("\"lastClickedTick\":").Append(clickedTick).Append(",");
            sb.Append("\"hoveredLineInGame\":").Append(gameHoveredLine).Append(",");
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
                    // Curvature exposed to the UI as an integer percent of the stepper range
                    // [0, kMaxPullFactor] — 50% = the 0.4 default pull.
                    int curvPercent = (int)math.round(math.saturate(line.curvature / MarkingCurveBuilder.kMaxPullFactor) * 100f);
                    sb.Append("{\"lineIndex\":").Append(i)
                        .Append(",\"style\":").Append(line.style)
                        .Append(",\"curv\":").Append(curvPercent)
                        .Append(",\"segments\":[");

                    bool bezOk = MarkingCurveBuilder.TryBuild(endpoints, line, out var fullBez);
                    int segCount = 0;
                    // Camera for world→screen projection. Captured once per line — if null
                    // (paused / unloaded), screenX/Y default to -1 and React hides the popover.
                    var cam = Camera.main;
                    int screenH = Screen.height; // for flipping Y (Unity screen has 0 at bottom, CSS at top)
                    if (segs.IsCreated)
                    {
                        int perLineCounter = 0;
                        for (int s = 0; s < segs.Length; s++)
                        {
                            var seg = segs[s];
                            if (seg.lineIndex != i) continue;
                            if (segCount > 0) sb.Append(",");

                            float lengthM = 0f;
                            float screenX = -1f, screenY = -1f;
                            if (bezOk)
                            {
                                var cut = MathUtils.Cut(fullBez, new float2(seg.tStart, seg.tEnd));
                                lengthM = MathUtils.Length(cut);
                                // Midpoint world position of the segment — anchor for the popover.
                                var midWorld = MathUtils.Position(fullBez, (seg.tStart + seg.tEnd) * 0.5f);
                                if (cam != null)
                                {
                                    var screen = cam.WorldToScreenPoint(midWorld);
                                    // z > 0 = in front of camera. Off-screen / behind camera → skip.
                                    if (screen.z > 0f)
                                    {
                                        screenX = screen.x;
                                        screenY = screenH - screen.y; // Unity → CSS Y flip
                                    }
                                }
                            }

                            sb.Append("{\"lineIndex\":").Append(i)
                                .Append(",\"segmentIndex\":").Append(perLineCounter)
                                .Append(",\"tStart\":").Append(seg.tStart.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                                .Append(",\"tEnd\":").Append(seg.tEnd.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                                .Append(",\"visible\":").Append(seg.visible ? "true" : "false")
                                .Append(",\"style\":").Append(seg.style)
                                .Append(",\"lengthM\":").Append(lengthM.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                                .Append(",\"screenX\":").Append(screenX.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                                .Append(",\"screenY\":").Append(screenY.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                                .Append("}");
                            perLineCounter++;
                            segCount++;
                        }
                    }
                    sb.Append("]}");
                }
            }

            sb.Append("],\"areas\":[");

            // Areas list — one entry per user-closed polygon on the selected node. Piece counts
            // come from the topology buffer so the panel can show "K piece(s)" when lines cut
            // the area apart. Style + visibility mirror the MarkingArea buffer directly.
            if (isActive && _tool.SelectedNode != Entity.Null
                && EntityManager.HasBuffer<MarkingArea>(_tool.SelectedNode))
            {
                var node = _tool.SelectedNode;
                var areas = EntityManager.GetBuffer<MarkingArea>(node, isReadOnly: true);
                var pieces = EntityManager.HasBuffer<MarkingAreaPiece>(node)
                    ? EntityManager.GetBuffer<MarkingAreaPiece>(node, isReadOnly: true)
                    : default;
                for (int a = 0; a < areas.Length; a++)
                {
                    if (a > 0) sb.Append(",");
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
                    sb.Append("{\"areaIndex\":").Append(a)
                        .Append(",\"styleId\":").Append(area.styleId)
                        .Append(",\"visible\":").Append(area.visible ? "true" : "false")
                        .Append(",\"vertexCount\":").Append(area.vertexCount)
                        .Append(",\"pieceCount\":").Append(pieceCount)
                        .Append(",\"visiblePieces\":").Append(visiblePieces)
                        .Append("}");
                }
            }

            sb.Append("]}");
            return sb.ToString();
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
            // Topology rebuild needs to see new lineIndex assignments — wipe segments + hash.
            if (EntityManager.HasBuffer<MarkingSegment>(node))
                EntityManager.GetBuffer<MarkingSegment>(node).Clear();
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            if (!EntityManager.HasComponent<Updated>(node))
                EntityManager.AddComponent<Updated>(node);
            log.Info($"UI: deleted line#{lineIndex} on node#{node.Index}");
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
            log.Info($"UI: full reset of node#{node.Index} — lines, areas and vanilla override cleared");
        }
    }
}
