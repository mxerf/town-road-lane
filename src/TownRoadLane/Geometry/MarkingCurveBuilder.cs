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
        // Default for new lines and the drag preview; existing lines carry their own
        // per-line factor in MarkingLine.curvature (0 = straight chord).
        public const float kPullFactor = 0.4f;

        // Upper bound for the per-line factor — beyond ~0.8 the curve starts looping back on
        // itself for short chords. UI maps its 0..100% stepper onto [0, kMaxPullFactor].
        public const float kMaxPullFactor = 0.8f;

        public static Bezier4x3 Build(float3 a, float2 ta, float3 b, float2 tb)
            => Build(a, ta, b, tb, kPullFactor);

        public static Bezier4x3 Build(float3 a, float2 ta, float3 b, float2 tb, float pullFactor)
        {
            float chord = math.distance(a, b);
            float pull = chord * math.clamp(pullFactor, 0f, kMaxPullFactor);
            ResolveTravelTangents(a, ta, b, tb, chord, out float3 ta3, out float3 tb3);
            return new Bezier4x3(a, a + ta3 * pull, b + tb3 * pull, b);
        }

        /// <summary>Default pull factor for a NEW line between two endpoints: the cubic-Bezier
        /// control offset that best approximates a circular arc matching the endpoint tangents.
        /// The historic constant <see cref="kPullFactor"/> (0.4) is this formula's value at a 90°
        /// turn; sharper connections (diverging ramp lanes, gores) need a larger pull or the
        /// default curve runs visibly flatter than the carriageway edge. Existing lines keep
        /// their stored per-line factor — this only seeds new ones.</summary>
        public static float AdaptivePullFactor(float3 a, float2 ta, float3 b, float2 tb)
        {
            float chord = math.distance(a, b);
            if (chord <= 1e-4f) return kPullFactor;
            ResolveTravelTangents(a, ta, b, tb, chord, out float3 ta3, out float3 tb3);
            float3 dirA = math.normalizesafe(ta3);
            float3 dirB = math.normalizesafe(-tb3); // travel direction at b (tb3 points backward)
            float cos = math.clamp(math.dot(dirA, dirB), -1f, 1f);
            float theta = math.acos(cos); // turn angle of the approximated arc
            if (theta < 1e-3f) return 1f / 3f; // straight-line limit of the formula below
            // Circular-arc approximation: control offset (4/3)·tan(θ/4)·R over chord 2·R·sin(θ/2).
            float f = (4f / 3f) * math.tan(theta * 0.25f) / (2f * math.sin(theta * 0.5f));
            return math.clamp(f, 0f, kMaxPullFactor);
        }

        public static float AdaptivePullFactor(MarkingEndpoint src, MarkingEndpoint dst)
            => AdaptivePullFactor(src.position, src.tangent, dst.position, dst.tangent);

        // MarkingEndpoint stores tangent pointing INTO the edge (away from the intersection).
        // For a classic cross-junction pair the curve should leave each dot the other way —
        // into the intersection — so the historic behaviour is to negate. But a SAME-LANE
        // longitudinal pair (a setback dot with its node-cap dot) has one tangent already
        // pointing straight at the partner; negating that one would loop the curve through
        // the intersection and back. Per endpoint: if the into-edge tangent aligns strongly
        // with the direction to the partner (cos > 0.5, i.e. within 60°), keep it; otherwise
        // negate as before. Lateral pairs (stop lines across one road, cos ≈ 0) and all
        // cross-junction pairs (cos < 0) keep their historic shape.
        private static void ResolveTravelTangents(float3 a, float2 ta, float3 b, float2 tb, float chord, out float3 ta3, out float3 tb3)
        {
            ta3 = new float3(ta.x, 0f, ta.y);
            tb3 = new float3(tb.x, 0f, tb.y);
            float3 abDir = chord > 1e-4f ? (b - a) / chord : float3.zero;
            if (math.dot(ta3, abDir) <= 0.5f) ta3 = -ta3;
            if (math.dot(tb3, -abDir) <= 0.5f) tb3 = -tb3;
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
            bez = Build(src.position, src.tangent, dst.position, dst.tangent, line.curvature);
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
            bez = Build(src.position, src.tangent, dst.position, dst.tangent, line.curvature);
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
