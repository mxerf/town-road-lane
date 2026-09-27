namespace TownRoadLane.Systems.Tool
{
    /// <summary>State of <see cref="MarkingNodeToolSystem"/>. Esc steps back one state.</summary>
    public enum MarkingToolState
    {
        Default,
        NodeSelected,
        SourceSelected,
        // Entered from NodeSelected via the area hotkey or the panel button. A click adds a
        // vertex; clicking the start vertex with 3+ placed closes and commits the area.
        // Right-click removes the last vertex, or leaves the mode if there is none. Esc cancels.
        AreaSelecting,
    }
}
