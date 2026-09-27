using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using TownRoadLane.Components;
using TownRoadLane.Geometry;
using TownRoadLane.Systems.Tool;
using TownRoadLane.Systems.Topology;
using TownRoadLane.Utilities;

namespace TownRoadLane.Systems.UI
{
    public partial class TownRoadLaneUISystem
    {
        /// <summary>The selected node, or false with a warning naming the ignored command.</summary>
        private bool TryGetSelectedNode(string command, out Entity node)
        {
            node = _tool.SelectedNode;
            if (node != Entity.Null) return true;
            log.Warn($"{command} ignored — no node selected");
            return false;
        }

        /// <summary>The selected node and its writable buffer. A node without the buffer has
        /// nothing to edit, so that case returns false silently.</summary>
        private bool TryGetSelectedBuffer<T>(string command, out Entity node, out DynamicBuffer<T> buffer)
            where T : unmanaged, IBufferElementData
        {
            buffer = default;
            if (!TryGetSelectedNode(command, out node) || !EntityManager.HasBuffer<T>(node)) return false;
            buffer = EntityManager.GetBuffer<T>(node);
            return true;
        }

        /// <summary>Indices come from the last published panel state and can be stale by the time
        /// the command arrives; out-of-range ones are ignored.</summary>
        private static bool IsInRange<T>(DynamicBuffer<T> buffer, int index) where T : unmanaged
            => index >= 0 && index < buffer.Length;

        /// <summary>Not validated: -1 or any out-of-range index simply highlights nothing.</summary>
        private void OnSetHoveredLine(int lineIndex)
        {
            _uiHoveredLineIndex = lineIndex;
        }

        /// <summary>Pass (-1, -1) to clear.</summary>
        private void OnSetHoveredSegment(int lineIndex, int segmentIndex)
        {
            _uiHoveredSegmentLine = lineIndex;
            _uiHoveredSegmentIndex = segmentIndex;
        }

        private void OnSetHoveredArea(int areaIndex)
        {
            _uiHoveredAreaIndex = areaIndex;
        }

        /// <summary>cohtml can fire mouseenter of the next row before mouseleave of the previous
        /// one, so an unconditional clear on leave would wipe the new hover. Leave passes its own
        /// index and clears only while that index is still the hovered one.</summary>
        private void OnClearHoveredArea(int areaIndex)
        {
            if (_uiHoveredAreaIndex == areaIndex)
                _uiHoveredAreaIndex = -1;
        }

        /// <summary>Drops every UI hover index. Has to run on any structural change (deleting a
        /// line or area, node reset): rows shift under a still cursor and cohtml does not re-fire
        /// mouseenter/leave, so a stale index would highlight a different object than the one
        /// the next click acts on.</summary>
        private void ClearUIHover()
        {
            _uiHoveredLineIndex = -1;
            _uiHoveredSegmentLine = -1;
            _uiHoveredSegmentIndex = -1;
            _uiHoveredAreaIndex = -1;
        }

        /// <summary>Toolbar button: toggles the tool, same as the Ctrl+M hotkey.</summary>
        private void OnActivateTool()
        {
            MarkingToolHotkeySystem.RequestToggle();
        }

        private void OnToggleSegment(int lineIndex, int segmentIndexPerLine)
        {
            if (!TryGetSelectedBuffer<MarkingSegment>("ToggleSegment", out var node, out var segs)) return;

            int s = MarkingSegment.FindIndex(segs, lineIndex, segmentIndexPerLine);
            if (s < 0) { log.Warn($"ToggleSegment: line#{lineIndex} seg#{segmentIndexPerLine} not found"); return; }
            var seg = segs[s];
            seg.visible = !seg.visible;
            segs[s] = seg;
            EntityManager.MarkUpdated(node);
            log.Debug($"UI: toggled line#{lineIndex} seg#{segmentIndexPerLine} → visible={seg.visible}");
        }

        /// <summary>Overrides the style of one segment. The UI addresses segments by their index
        /// within the line, which <see cref="MarkingSegment.FindIndex"/> maps to the buffer.</summary>
        private void OnSetSegmentStyle(int lineIndex, int segmentIndexPerLine, int style)
        {
            if (!TryGetSelectedBuffer<MarkingSegment>("SetSegmentStyle", out var node, out var segs)) return;

            int s = MarkingSegment.FindIndex(segs, lineIndex, segmentIndexPerLine);
            if (s < 0) { log.Warn($"SetSegmentStyle: line#{lineIndex} seg#{segmentIndexPerLine} not found"); return; }
            var seg = segs[s];
            seg.style = style;
            segs[s] = seg;
            EntityManager.MarkUpdated(node);
            log.Debug($"UI: set line#{lineIndex} seg#{segmentIndexPerLine} style → {(MarkingStyle)style}");
        }

        private void OnSetLineStyle(int lineIndex, int style)
        {
            if (!TryGetSelectedBuffer<MarkingLine>("SetLineStyle", out var node, out var lines)
                || !IsInRange(lines, lineIndex)) return;

            var ln = lines[lineIndex];
            ln.style = style;
            lines[lineIndex] = ln;
            // Style does not move segment boundaries, so the segments are restyled in place
            // rather than rebuilt.
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
            // Reset the topology hash: the lines' geometry did not change, so without this
            // MarkingTopologySystem would skip the node and nothing would be re-emitted.
            if (EntityManager.HasComponent<MarkingTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingTopologyState { linesHash = 0 });
            EntityManager.MarkUpdated(node);
            log.Debug($"UI: set line#{lineIndex} style → {(MarkingStyle)style}");
        }

        /// <summary>Sets a line's pull factor from the panel stepper; percent 0..100 maps onto
        /// [0, kMaxPullFactor]. The topology hash includes curvature, so marking the node Updated
        /// is enough to re-split the line and redraw it.</summary>
        private void OnSetLineCurvature(int lineIndex, int percent)
        {
            if (!TryGetSelectedBuffer<MarkingLine>("SetLineCurvature", out var node, out var lines)
                || !IsInRange(lines, lineIndex)) return;

            var ln = lines[lineIndex];
            ln.curvature = math.saturate(percent / 100f) * MarkingCurveBuilder.kMaxPullFactor;
            lines[lineIndex] = ln;
            EntityManager.MarkUpdated(node);
            log.Debug($"UI: set line#{lineIndex} curvature → {percent}% (pull={ln.curvature:0.###})");
        }

        /// <summary>Toggles the "hide vanilla markings" override (<see cref="MarkingOverride"/>
        /// with All) on the selected node; CustomSecondaryLaneSystem skips vanilla markings while
        /// it is set. User lines already hide vanilla markings; this switch also works on a node
        /// without any.</summary>
        private void OnToggleVanillaMarkings()
        {
            if (!TryGetSelectedNode("ToggleVanillaMarkings", out var node)) return;

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
            EntityManager.MarkUpdated(node);
            log.Debug($"UI: vanilla markings on node#{node.Index} → {(hidden ? "shown" : "hidden")}");
        }

        private void OnDeleteLine(int lineIndex)
        {
            if (!TryGetSelectedBuffer<MarkingLine>("DeleteLine", out var node, out var lines)
                || !IsInRange(lines, lineIndex)) return;

            lines.RemoveAt(lineIndex);
            // Reindex rather than rebuild, so the other lines keep their segment overrides and
            // area anchors follow the shifted indices.
            MarkingTopologySystem.OnLineRemoved(EntityManager, node, lineIndex);
            EntityManager.MarkUpdated(node);
            ClearUIHover();
            log.Debug($"UI: deleted line#{lineIndex} on node#{node.Index} — segment overrides and area anchors reindexed");
        }

        // Panel counterparts of the Y / U / A hotkeys.

        /// <summary>Style for the next line drawn (the Y hotkey cycles the same value).</summary>
        private void OnSetCurrentStyle(int style)
        {
            _tool.SetCurrentStyle((MarkingStyle)style);
        }

        /// <summary>Fill style for the next area closed (the U hotkey cycles the same value).</summary>
        private void OnSetCurrentAreaStyle(int styleId)
        {
            _tool.SetCurrentAreaStyle(styleId);
        }

        /// <summary>Switches between NodeSelected and AreaSelecting, like the A hotkey. Leaving
        /// area mode drops an unfinished contour.</summary>
        private void OnToggleAreaMode()
        {
            if (_tool.ToolState == MarkingToolState.AreaSelecting)
                _tool.ExitAreaMode();
            else
                _tool.TryEnterAreaMode();
        }

        // Pinned styles. Apply(), not ApplyAndSave(): each ApplyAndSave is a separate async
        // read-modify-write of the settings file that races with the Options screen's own saves
        // and loses edits. Apply() marks the settings dirty and the coalescing saver in
        // TownRoadLaneSetting writes once.
        private void OnTogglePinLineStyle(int style)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.PinnedLineStylesCsv = ToggleIdInCsv(Mod.Settings.PinnedLineStylesCsv, style);
            Mod.Settings.Apply();
            _pinnedStyles.Value = BuildPinnedStylesVM();
        }

        private void OnTogglePinAreaStyle(int styleId)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.PinnedAreaStylesCsv = ToggleIdInCsv(Mod.Settings.PinnedAreaStylesCsv, styleId);
            Mod.Settings.Apply();
            _pinnedStyles.Value = BuildPinnedStylesVM();
        }

        private static PinnedStylesVM BuildPinnedStylesVM() => new()
        {
            lineStyles = ParseCsv(Mod.Settings?.PinnedLineStylesCsv),
            areaStyles = ParseCsv(Mod.Settings?.PinnedAreaStylesCsv),
        };

        private static int[] ParseCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<int>();
            var parts = csv.Split(',');
            var result = new List<int>(parts.Length);
            foreach (var p in parts)
                if (int.TryParse(p.Trim(), out int v) && !result.Contains(v)) result.Add(v);
            return result.ToArray();
        }

        private static string ToggleIdInCsv(string csv, int id)
        {
            var ids = new List<int>(ParseCsv(csv));
            if (!ids.Remove(id)) ids.Add(id);
            return string.Join(",", ids);
        }

        /// <summary>Emission compares the prefab every frame and respawns the Area entity when it
        /// changes, so no hash reset is needed here.</summary>
        private void OnSetAreaStyle(int areaIndex, int styleId)
        {
            if (!TryGetSelectedBuffer<MarkingArea>("SetAreaStyle", out var node, out var areas)
                || !IsInRange(areas, areaIndex)) return;

            var area = areas[areaIndex];
            area.styleId = styleId;
            areas[areaIndex] = area;
            log.Debug($"UI: set area#{areaIndex} style → {styleId} on node#{node.Index}");
        }

        /// <summary>Pieces keep their own visibility flags, so hiding and showing an area again
        /// restores its previous piece pattern.</summary>
        private void OnToggleAreaVisible(int areaIndex)
        {
            if (!TryGetSelectedBuffer<MarkingArea>("ToggleAreaVisible", out var node, out var areas)
                || !IsInRange(areas, areaIndex)) return;

            var area = areas[areaIndex];
            area.visible = !area.visible;
            areas[areaIndex] = area;
            log.Debug($"UI: area#{areaIndex} on node#{node.Index} → visible={area.visible}");
        }

        /// <summary>Removes the area and its vertex slice, shifts the firstVertex offsets of the
        /// areas after it, and forces a piece recompute.</summary>
        private void OnDeleteArea(int areaIndex)
        {
            if (!TryGetSelectedBuffer<MarkingArea>("DeleteArea", out var node, out var areas)
                || !IsInRange(areas, areaIndex)) return;

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

            // Pieces refer to areas by index, so MarkingAreaTopologySystem has to rebuild them
            // against the shifted list.
            if (EntityManager.HasComponent<MarkingAreaTopologyState>(node))
                EntityManager.SetComponentData(node, new MarkingAreaTopologyState { combinedHash = 0 });
            EntityManager.MarkUpdated(node);
            ClearUIHover();
            log.Debug($"UI: deleted area#{areaIndex} on node#{node.Index} ({areas.Length} remaining)");
        }

        /// <summary>Removes every line, segment, area and the vanilla override from the selected
        /// node, restoring the stock markings. Buffers are cleared rather than removed: emission
        /// then sees empty sets and despawns all the mod's lanes and Area entities, and
        /// CustomSecondaryLaneSystem regenerates the vanilla markings.</summary>
        private void OnResetNode()
        {
            if (!TryGetSelectedNode("ResetNode", out var node)) return;

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
            EntityManager.MarkUpdated(node);
            ClearUIHover();
            log.Debug($"UI: full reset of node#{node.Index} — lines, areas and vanilla override cleared");
        }
    }
}
