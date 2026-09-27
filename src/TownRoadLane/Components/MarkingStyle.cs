using TownRoadLane.Systems.Prefabs;
namespace TownRoadLane.Components
{
    /// <summary>
    /// Line style, saved as an int in <see cref="MarkingLine.style"/> and
    /// <see cref="MarkingSegment.style"/>. Values are append-only: reusing or reordering one
    /// changes the lines in existing saves.
    ///
    /// Adding a style:
    ///   1. Append a value here.
    ///   2. Register its prefab clones (one per theme, EU and NA) in
    ///      <see cref="EdgeLineCloneSystem"/>; emission finds them through
    ///      <see cref="EdgeLineCloneSystem.GetCloneEntity"/>.
    ///   3. UI: STYLE_VALUES and STYLE_KEYS in town-road-lane-panel.tsx, the i18n strings and a
    ///      preview in stylePreviews.tsx.
    ///
    /// Emission draws unknown values as <see cref="Solid"/>, so a save made with a newer version
    /// of the mod still loads.
    /// </summary>
    public enum MarkingStyle : int
    {
        Solid = 0,
        Dashed = 1,
        G87Solid = 2,
        G87Dashed = 3,
        DoubleSolid = 4,
        DashedDense = 5,
        G87Yellow = 6,
        G87YellowDashed = 7,
        DashedLong = 8,
        // Vanilla curb texture from the "[G87] Vanilla Curb" pack (elGendo87), a required
        // dependency. PickMesh falls back to the source prefab's own mesh if the pack is missing.
        Curb = 9,
        // YellowDashed uses the '- Long' mesh: vanilla has no normal-length yellow dashed line.
        // YellowSolidDashed is the US passing-zone center line (solid on one side, dashed on the
        // other).
        YellowSolid = 10,
        YellowDashed = 11,
        YellowDoubleSolid = 12,
        YellowSolidDashed = 13,
    }

    public static class MarkingStyleExtensions
    {
        /// <summary>
        /// Number of overlapping copies drawn for a style. G87 decals are semi-transparent; in
        /// parking markings vanilla draws them once from each neighbouring lane, so a single
        /// copy of ours looks noticeably dimmer. Vanilla decals are opaque and need one.
        /// </summary>
        public static int DrawPasses(this MarkingStyle style) => style switch
        {
            MarkingStyle.G87Solid => 3,
            MarkingStyle.G87Dashed => 3,
            MarkingStyle.G87Yellow => 3,
            MarkingStyle.G87YellowDashed => 3,
            _ => 1,
        };
    }
}
