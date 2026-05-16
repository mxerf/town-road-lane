using System;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Categories of marking the per-entity override can suppress. Bit-flag layout so the struct can grow
    /// across phases without breaking saves — saved data is just a uint, new bits are zero on old saves.
    ///
    /// Phase 1: only <see cref="All"/> was meaningful (the global "hide all markings" toggle wrote that).
    /// Phase 2a: separate bits per mod-added category so we can later opt out of just edge-line on a
    /// segment but keep parking line. Future phases add more bits; the call sites just test the bit.
    /// </summary>
    [Flags]
    public enum MarkingCategory : uint
    {
        None             = 0,
        // Categories we ADD on top of vanilla:
        EdgeLine         = 1u << 0,   // curb-side edge line on 3 m city drive lanes
        ParkingLine      = 1u << 1,   // longitudinal line along parallel street parking
        ParkingEnd       = 1u << 2,   // perpendicular tick at start+end of a parking block
        // Reserved bits for phase 3 (vanilla categories we suppress on the entity):
        // VanillaDividers   = 1u << 8,
        // VanillaCrosswalk  = 1u << 9,
        All              = 0xFFFFFFFFu,
    }

    /// <summary>
    /// Per-edge / per-node user override read by <see cref="CustomSecondaryLaneSystem"/> when deciding
    /// which marking lanes to spawn for the entity.
    ///
    /// Semantics: <see cref="hide"/> is a bit mask — any bit set means "do NOT draw this category on
    /// this entity, regardless of the global setting." When the bit is clear, the global setting wins.
    ///
    /// Phase 1's "hide all markings" button sets <c>hide = MarkingCategory.All</c>. Phase 3 onward
    /// will give the upgrade tool the ability to toggle individual bits per segment.
    /// </summary>
    public struct MarkingOverride : IComponentData
    {
        public MarkingCategory hide;

        /// <summary>True if every category is suppressed (the phase-1 "hide everything" state).</summary>
        public bool HideAll => hide == MarkingCategory.All;

        /// <summary>Convenience predicate: is this category currently suppressed on the entity?</summary>
        public bool Hides(MarkingCategory cat) => (hide & cat) != 0;
    }
}
