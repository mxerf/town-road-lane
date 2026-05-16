using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Per-edge / per-node user override for marking generation. Read by
    /// <see cref="CustomSecondaryLaneSystem"/> when it decides which secondary (marking) lanes to spawn.
    ///
    /// Phase 1: a single boolean — when present and <c>hideAll</c> is true, no marking lanes are
    /// generated for the entity. Existing markings still get removed by the vanilla deletion pass at the
    /// top of <c>UpdateLanes</c>, so toggling off mid-game cleanly clears them.
    ///
    /// Phase 2+ will replace this with flags / per-pair data; keeping the struct intentionally tiny so
    /// we can grow it without breaking saves (deserialization tolerates trailing zeros).
    /// </summary>
    public struct MarkingOverride : IComponentData
    {
        public bool hideAll;
    }
}
