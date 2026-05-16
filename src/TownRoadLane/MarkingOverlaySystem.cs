using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TownRoadLane
{
    /// <summary>
    /// Phase 4d: draws connector dots, drag line, and confirmed pairs while
    /// <see cref="MarkingNodeToolSystem"/> is the active tool. Idle otherwise.
    ///
    /// All draws go through vanilla <see cref="OverlayRenderSystem"/> — we get a Buffer per
    /// frame, push primitives, and add a JobHandle so the renderer knows when we're done.
    /// Endpoint count is small (~16 per node), so we draw directly on the main thread instead
    /// of scheduling a burst job (Traffic's pattern). Pull this into a job if perf demands.
    /// </summary>
    public partial class MarkingOverlaySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private ToolSystem _toolSystem;
        private MarkingNodeToolSystem _tool;
        private OverlayRenderSystem _overlayRenderSystem;

        // Visual tuning (meters / unity-alpha colours)
        private const float kDotDiameter        = 1.4f;
        private const float kDotOutlineWidth    = 0.15f;
        private const float kDragLineWidth      = 0.35f;
        private const float kPairCurveWidth     = 0.30f;

        // Phase 4d uses these. 4e wires source-selected highlight.
        private static readonly Color kColRightDot    = new Color(0.30f, 0.85f, 1.00f, 0.95f); // cyan
        private static readonly Color kColLeftDot     = new Color(1.00f, 0.65f, 0.20f, 0.95f); // orange
        private static readonly Color kColOutline     = new Color(0.05f, 0.05f, 0.10f, 0.95f);
        private static readonly Color kColSourceDot   = new Color(1.00f, 1.00f, 1.00f, 1.00f); // white, when selected
        private static readonly Color kColPairCurve   = new Color(0.30f, 1.00f, 0.50f, 0.85f); // green
        private static readonly Color kColDragLine    = new Color(1.00f, 1.00f, 1.00f, 0.85f);

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _tool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnUpdate()
        {
            if (_tool == null || _toolSystem.activeTool != _tool) return;

            var buf = _overlayRenderSystem.GetBuffer(out JobHandle deps);
            JobHandle ourFallback = JobHandle.CombineDependencies(deps, Dependency);

            var endpoints = _tool.Endpoints;
            // Heartbeat: when the tool is active but no node is selected yet, draw a magenta ring
            // at the cursor so the user can SEE the tool is live. Without this, "activated but no
            // node clicked yet" is visually identical to "tool didn't activate at all".
            if (endpoints == null || endpoints.Count == 0)
            {
                float3 cursor = _tool.CursorWorldPos;
                if (math.lengthsq(cursor) > 0.01f)
                {
                    buf.DrawCircle(
                        outlineColor: new Color(1f, 0.2f, 0.8f, 1f),
                        fillColor: new Color(1f, 0.2f, 0.8f, 0.3f),
                        outlineWidth: 0.2f,
                        styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                        direction: new float2(0f, 1f),
                        position: cursor,
                        diameter: 3f);
                }
                _overlayRenderSystem.AddBufferWriter(ourFallback);
                Dependency = ourFallback;
                return;
            }
            // We're not scheduling a job, but the buffer protocol still wants a writer handle.
            JobHandle our = ourFallback;

            int sourceIdx = _tool.SourceEndpointIndex;
            int hoverIdx  = _tool.HoveredEndpointIndex;

            // 1. Confirmed pairs first — they sit underneath everything else, so dots and the
            //    drag-line stay readable when there are several pairs at a busy node.
            var node = _tool.SelectedNode;
            if (node != Entity.Null && EntityManager.HasBuffer<MarkingPair>(node))
            {
                var pairs = EntityManager.GetBuffer<MarkingPair>(node, isReadOnly: true);
                for (int p = 0; p < pairs.Length; p++)
                {
                    if (TryResolvePair(pairs[p], out float3 a, out float3 b))
                    {
                        // Straight-segment "curve" — Bezier with control points evenly spaced.
                        var bezier = new Bezier4x3(a, math.lerp(a, b, 0.33f), math.lerp(a, b, 0.66f), b);
                        buf.DrawCurve(kColPairCurve, bezier, kPairCurveWidth);
                    }
                }
            }

            // 2. Drag-line from source to cursor / hovered target (middle layer).
            if (sourceIdx >= 0 && sourceIdx < endpoints.Count)
            {
                float3 from = endpoints[sourceIdx].position;
                float3 to = (hoverIdx >= 0 && hoverIdx < endpoints.Count)
                    ? endpoints[hoverIdx].position
                    : _tool.CursorWorldPos;
                if (math.lengthsq(to - from) > 0.01f)
                {
                    buf.DrawLine(kColDragLine, new Line3.Segment(from, to), kDragLineWidth);
                }
            }

            // 3. Dots last so they're always on top. Source = white, hover = brightened, others
            //    coloured by side (right = cyan, left = orange).
            for (int i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                Color fill = ep.isRight ? kColRightDot : kColLeftDot;
                if (i == sourceIdx)      fill = kColSourceDot;
                else if (i == hoverIdx)  fill = Color.Lerp(fill, Color.white, 0.5f);

                buf.DrawCircle(
                    outlineColor: kColOutline,
                    fillColor: fill,
                    outlineWidth: kDotOutlineWidth,
                    styleFlags: OverlayRenderSystem.StyleFlags.Projected,
                    direction: new float2(0f, 1f),
                    position: ep.position,
                    diameter: kDotDiameter);
            }

            _overlayRenderSystem.AddBufferWriter(our);
            Dependency = our;
        }

        /// <summary>
        /// Resolve a MarkingPair entry to its two world-space attach points. We re-derive them
        /// the same way <see cref="MarkingEndpointExtractor"/> does — that keeps source and
        /// rendering consistent without an extra cached struct.
        /// </summary>
        private bool TryResolvePair(MarkingPair pair, out float3 a, out float3 b)
        {
            a = default; b = default;
            if (!TryResolveEndpoint(pair.sourceEdge, pair.sourceLaneIndex, pair.sourceIsRight, out a)) return false;
            if (!TryResolveEndpoint(pair.targetEdge, pair.targetLaneIndex, pair.targetIsRight, out b)) return false;
            return true;
        }

        private bool TryResolveEndpoint(Entity edge, int laneIndex, bool isRight, out float3 pos)
        {
            pos = default;
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(edge)) return false;
            var subs = EntityManager.GetBuffer<Game.Net.SubLane>(edge, isReadOnly: true);
            if (laneIndex < 0 || laneIndex >= subs.Length) return false;
            var lane = subs[laneIndex].m_SubLane;
            if (!EntityManager.HasComponent<Game.Net.Curve>(lane)) return false;
            if (!EntityManager.HasComponent<Game.Prefabs.PrefabRef>(lane)) return false;
            var lanePrefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(lane).m_Prefab;
            if (!EntityManager.HasComponent<Game.Prefabs.NetLaneData>(lanePrefab)) return false;

            // Which end of the lane does our node sit on? Use EdgeLane.m_EdgeDelta + Edge parity,
            // same as the extractor — we don't have the node here in fast-path though, so we
            // accept whichever end matches; if both are valid (full-edge lane) we pick the start.
            var curve = EntityManager.GetComponentData<Game.Net.Curve>(lane);
            float2 width = EntityManager.GetComponentData<Game.Prefabs.NetLaneData>(lanePrefab).m_Width;

            // Determine which end the user picked — we infer it by which end Curve is closer to
            // the selected node's position. Slight overkill but robust to topology changes.
            float3 nodePos = float3.zero;
            var node = _tool.SelectedNode;
            if (EntityManager.HasComponent<Game.Net.Node>(node))
                nodePos = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
            bool atStart = math.lengthsq(curve.m_Bezier.a - nodePos) <= math.lengthsq(curve.m_Bezier.d - nodePos);

            float3 endpoint = atStart ? curve.m_Bezier.a : curve.m_Bezier.d;
            float2 tangent = atStart
                ? math.normalizesafe(MathUtils.StartTangent(curve.m_Bezier).xz)
                : math.normalizesafe(MathUtils.EndTangent(curve.m_Bezier).xz);

            float2 off = isRight ? MathUtils.Right(tangent) : MathUtils.Left(tangent);
            float halfWidth = (atStart ? width.x : (isRight ? width.y : width.x)) * 0.5f;
            pos = endpoint;
            pos.xz += off * halfWidth;
            return true;
        }
    }
}
