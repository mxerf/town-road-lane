namespace TownRoadLane
{
    /// <summary>
    /// User-pickable visual style for a <see cref="MarkingLine"/>. Stored as <c>int</c> in the
    /// line buffer (see <see cref="MarkingLine.style"/>) for serialisation stability — never
    /// reuse a numeric value for a different style or saves break.
    ///
    /// Adding a new style is a three-step change:
    ///   1. Append an entry here (do NOT reorder or reuse — append only).
    ///   2. Register the corresponding prefab clone in <see cref="EdgeLineCloneSystem"/> (one
    ///      clone per (style × theme) — EU + NA).
    ///   3. Register the style → prefab mapping in <see cref="MarkingSegmentEmissionSystem"/>
    ///      (one line in the resolver dictionary).
    ///
    /// The emission system falls back to <see cref="Solid"/> for unknown values, so old saves
    /// with future-style numbers degrade gracefully (a line drawn in a not-yet-installed style
    /// just renders as solid until the style is added).
    /// </summary>
    public enum MarkingStyle : int
    {
        Solid       = 0,
        Dashed      = 1,
        G87Solid    = 2,
        G87Dashed   = 3,
        DoubleSolid = 4,
    }

    public static class MarkingStyleExtensions
    {
        /// <summary>
        /// How many overlapping draw passes a style needs to look correct. Vanilla decals (Solid,
        /// Dashed) use a single pass — they're opaque enough on their own. G87 decals are
        /// semi-transparent and need 2 overlapping passes to match the brightness vanilla
        /// parking markings achieve (their SecondaryLane hosts on both left+right lanes, which
        /// implicitly causes vanilla to draw the same prefab twice along the boundary). Without
        /// this, G87 styles look ~30% dimmer than their parking counterparts.
        /// </summary>
        public static int DrawPasses(this MarkingStyle style) => style switch
        {
            MarkingStyle.G87Solid  => 3,
            MarkingStyle.G87Dashed => 3,
            _                      => 1,
        };
    }
}
