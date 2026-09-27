using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>64-bit FNV-1a over 32-bit words. Used for change detection only: a cheap
    /// fingerprint of buffer contents, compared against the one from the last rebuild.</summary>
    public struct Fnv1a
    {
        public const ulong kOffset = 14695981039346656037UL;
        private const ulong kPrime = 1099511628211UL;

        public ulong Value { get; private set; }

        /// <summary>Folded to 32 bits for components that store an int.</summary>
        public int Value32 => (int)(Value ^ (Value >> 32));

        /// <summary>Use as a local and call Add on it: it is a mutable struct.</summary>
        public static Fnv1a Create() => new Fnv1a { Value = kOffset };

        public void Add(uint v) => Value = (Value ^ v) * kPrime;
        public void Add(int v) => Add((uint)v);
        public void Add(float v) => Add(math.asuint(v));
        public void Add(bool v) => Add(v ? 1u : 0u);
        public void Add(Unity.Entities.Entity e) => Add(e.Index);
    }
}
