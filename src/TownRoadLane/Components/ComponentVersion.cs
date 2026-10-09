using System.Collections.Generic;

namespace TownRoadLane.Components
{
    internal static class ComponentVersion
    {
        private static readonly HashSet<string> s_warned = new HashSet<string>();

        public static void Note(int version, int min, int known, string component)
        {
            if (version >= min && version <= known) return;
            if (!s_warned.Add(component)) return;
            if (version > known)
                Mod.log.Warn($"save: {component} version {version} is newer than this build ({known}); fields past version {known} were not read");
            else
                Mod.log.Warn($"save: {component} version {version} is older than the layouts this build reads (from {min})");
        }
    }
}
