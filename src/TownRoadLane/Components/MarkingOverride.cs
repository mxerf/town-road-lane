using System;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Marking categories a <see cref="MarkingOverride"/> can suppress. Saved as a plain uint, so
    /// new bits can be added without a format change: old saves read them as zero.
    /// </summary>
    [Flags]
    public enum MarkingCategory : uint
    {
        None             = 0,
        // Categories the mod adds on top of vanilla.
        EdgeLine         = 1u << 0,   // curb-side edge line on 3 m city drive lanes
        ParkingLine      = 1u << 1,   // longitudinal line along parallel street parking
        ParkingEnd       = 1u << 2,   // perpendicular tick at start+end of a parking block
        All              = 0xFFFFFFFFu,
    }

    /// <summary>
    /// Per-edge or per-node override read by <see cref="CustomSecondaryLaneSystem"/> when it
    /// generates marking lanes. A set bit in <see cref="hide"/> suppresses that category on the
    /// entity regardless of the global settings; a clear bit leaves the global setting in charge.
    /// The panel's "hide vanilla markings" toggle sets <c>MarkingCategory.All</c>.
    /// </summary>
    public struct MarkingOverride : IComponentData, ISerializable
    {
        public MarkingCategory hide;

        /// <summary>True when every category is suppressed.</summary>
        public bool HideAll => hide == MarkingCategory.All;

        public bool Hides(MarkingCategory cat) => (hide & cat) != 0;

        private const int kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write((uint)hide);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out uint h);
            hide = (MarkingCategory)h;
        }
    }
}
