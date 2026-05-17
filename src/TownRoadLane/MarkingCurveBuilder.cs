using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace TownRoadLane
{
    /// <summary>
    /// Single source of truth for "build the smooth Bezier between two MarkingEndpoint(s)".
    /// Previously this lived in three places (MarkingOverlaySystem.BuildSmoothCurve,
    /// MarkingPairEmissionSystem.SpawnSublane, and the implicit "drag preview must match emission"
    /// invariant). Stage 5b adds a fourth caller (topology recompute), so it's time to centralise.
    ///
    /// Pull factor matches what users see in the drag preview and what gets emitted as a sublane —
    /// changing it here updates all three call sites at once.
    /// </summary>
    public static class MarkingCurveBuilder
    {
        // Control-point offset = chord * kPullFactor along each endpoint's inward tangent.
        // 0.55 ≈ quarter circle; 0.4 = softer arc, matches vanilla divider feel.
        public const float kPullFactor = 0.4f;

        public static Bezier4x3 Build(float3 a, float2 ta, float3 b, float2 tb)
        {
            float chord = math.distance(a, b);
            float pull = chord * kPullFactor;
            // MarkingEndpoint stores tangent pointing INTO the edge (away from the intersection).
            // The curve should leave the dot the other way — into the intersection. Negate.
            float3 ta3 = new float3(-ta.x, 0f, -ta.y);
            float3 tb3 = new float3(-tb.x, 0f, -tb.y);
            return new Bezier4x3(a, a + ta3 * pull, b + tb3 * pull, b);
        }

        public static Bezier4x3 Build(MarkingEndpoint src, MarkingEndpoint dst)
            => Build(src.position, src.tangent, dst.position, dst.tangent);

        /// <summary>Resolve the full Bezier for a MarkingLine on a given node. Returns false if
        /// either endpoint can't be matched against the node's current endpoint set (e.g. road
        /// was demolished after the line was saved).</summary>
        public static bool TryBuild(EntityManager em, Entity node, MarkingLine line, out Bezier4x3 bez)
        {
            bez = default;
            var endpoints = MarkingEndpointExtractor.Extract(em, node);
            if (!TryFind(endpoints, line.sourceEdge, line.sourceGapIndex, out var src)) return false;
            if (!TryFind(endpoints, line.targetEdge, line.targetGapIndex, out var dst)) return false;
            bez = Build(src, dst);
            return true;
        }

        /// <summary>Variant that reuses an already-extracted endpoint list — cheaper when looking
        /// up many lines on the same node (avoid re-extracting per line). Accepts
        /// <see cref="IReadOnlyList{T}"/> so callers can pass either the extractor's
        /// <c>List&lt;MarkingEndpoint&gt;</c> or the tool's exposed <see cref="IReadOnlyList{T}"/>.</summary>
        public static bool TryBuild(IReadOnlyList<MarkingEndpoint> endpoints, MarkingLine line, out Bezier4x3 bez)
        {
            bez = default;
            if (endpoints == null) return false;
            if (!TryFind(endpoints, line.sourceEdge, line.sourceGapIndex, out var src)) return false;
            if (!TryFind(endpoints, line.targetEdge, line.targetGapIndex, out var dst)) return false;
            bez = Build(src, dst);
            return true;
        }

        private static bool TryFind(IReadOnlyList<MarkingEndpoint> endpoints, Entity edge, int gap, out MarkingEndpoint ep)
        {
            for (int i = 0; i < endpoints.Count; i++)
            {
                if (endpoints[i].edge == edge && endpoints[i].gapIndex == gap)
                {
                    ep = endpoints[i];
                    return true;
                }
            }
            ep = default;
            return false;
        }
    }
}
