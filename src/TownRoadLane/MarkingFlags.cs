using Game.Prefabs;

namespace TownRoadLane
{
    /// <summary>
    /// The single per-edge bit that means "this segment has the Lane Markings upgrade applied → suppress the
    /// city-road markings here".
    ///
    /// We ride bit 0x80000000 of <see cref="CompositionFlags.General"/>. The General enum is a <c>uint</c> whose
    /// highest defined member is <c>StyleBreak = 0x40000000</c>, so 0x80000000 is genuinely unused: nothing in
    /// the base game ever sets it (the <c>NetPieceRequirements → CompositionFlags</c> mapping in
    /// <c>NetCompositionHelpers.GetRequirementFlags</c> can't produce it), and <c>CompositionSelectSystem.GetEdgeFlags</c>
    /// passes all <c>m_General</c> bits through untouched, so it survives onto the edge's selected composition.
    /// An unmatched composition bit just makes a distinct composition cache key with the same pieces — harmless.
    ///
    /// We can't get this bit into an edge's <c>Upgraded.m_Flags</c> via the normal <c>NetUpgrade.m_SetState</c>
    /// path (no <c>NetPieceRequirements</c> maps to it), so the upgrade prefab's baked <c>PlaceableNetData.m_SetUpgradeFlags</c>
    /// gets this bit OR-ed in directly after prefab initialization (see MarkingUpgradePrefabSystem). The same bit
    /// is also OR-ed into the target city roads' <c>NetData.m_GeneralFlagMask</c> so the upgrade tool allows
    /// painting it on those roads.
    /// </summary>
    public static class MarkingFlags
    {
        /// <summary>The spare General bit (0x80000000) used as our "markings off" marker.</summary>
        public const CompositionFlags.General MarkingsOff = (CompositionFlags.General)0x80000000u;

        /// <summary>True if the given composition flags carry our "markings off" marker.</summary>
        public static bool HasMarkingsOff(in CompositionFlags flags) => (flags.m_General & MarkingsOff) != 0;
    }
}
