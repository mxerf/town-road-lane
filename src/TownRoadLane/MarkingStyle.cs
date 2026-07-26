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
    ///      clone per (style × theme) — EU + NA). Emission resolves clones generically via
    ///      <see cref="EdgeLineCloneSystem.GetCloneEntity"/> — no per-style mapping needed.
    ///   3. UI: STYLE_VALUES + STYLE_KEYS in town-road-lane-panel.tsx, the i18n strings, and
    ///      a preview in stylePreviews.tsx.
    ///
    /// The emission system falls back to <see cref="Solid"/> for unknown values, so old saves
    /// with future-style numbers degrade gracefully (a line drawn in a not-yet-installed style
    /// just renders as solid until the style is added).
    /// </summary>
    public enum MarkingStyle : int
    {
        Solid           = 0,
        Dashed          = 1,
        G87Solid        = 2,
        G87Dashed       = 3,
        DoubleSolid     = 4,
        // UI polish pass (2.3.0): short/long vanilla dashes + G87 yellow pair.
        DashedDense     = 5,
        G87Yellow       = 6,
        G87YellowDashed = 7,
        DashedLong      = 8,
        // Vanilla curb texture from the "[G87] Vanilla Curb" pack (elGendo87) — a hard PDX
        // dependency since 2.4.0, so it's always present in practice; PickMesh still degrades
        // to the source prefab's own mesh if it somehow isn't. Candidate history (2026-07-19):
        // VA "Road Border VFX GND" — Terrain-only decal mask, invisible on the road deck;
        // PVM "Vanilla Pavement 01 XS" — renders, but 0.88 m reads as a pavement strip.
        Curb            = 9,
        // Chevron median-fill band from the "[G87] Road Markings SC" pack (elGendo87),
        // optional — degrades to the source prefab's dashed mesh when the pack is missing.
        // Forum request (KeanZera, 2026-07-22).
        Chevron         = 10,
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
            MarkingStyle.G87Solid        => 3,
            MarkingStyle.G87Dashed       => 3,
            MarkingStyle.G87Yellow       => 3,
            MarkingStyle.G87YellowDashed => 3,
            _                            => 1,
        };
    }
}
