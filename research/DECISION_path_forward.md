# Decision matrix — choosing the rendering path forward

> **Context for future-self (post-/compact):** at the end of 2026-05-17 session
> we have a working PoC (commit `512149c`) that renders custom marking ribbons
> via `Graphics.DrawMesh + HDRP/Unlit + HDMaterial.ValidateMaterial`. Three
> visual gaps remain (floating, no lighting, weird texture). We've explored
> deeply and surveyed all reasonable rendering approaches; see
> `research/RESEARCH_rendering_approaches.md` (approach catalogue) and
> `research/RESEARCH_vanilla_decal_pipeline.md` (deep vanilla decomp dive).
> This document distils that into a decision matrix optimised for the
> long-term goal: **IMT-equivalent intersection marking customisation with
> line-line intersection segmentation**.

## North-star — what the mod must ultimately do

User scenario from session: *"4-lane avenue with median, 2-lane crossing road,
left-turn from far side. Median segment crossing the turn-lane must be
deletable. The killer feature is making the chaos of vanilla markings on
complex intersections into managed beautiful markings."*

This requires:

1. **Per-pair custom geometry** — every user-drawn pair has its own Bezier shape
2. **Per-segment delete** — must be able to render `(line, t_start, t_end)`
   ranges, not whole lines
3. **Multiple line styles** — solid, dashed, double-solid, stop, crosswalk
4. **Default-generate from vanilla** — one-click to auto-fill standard markings,
   then user edits
5. **Save/reload persistence** — already handled by `MarkingPair` buffer schema v2
6. **Performance** — needs to scale to dozens of pairs per intersection × dozens
   of edited intersections per city
7. **Visual quality** — looks like real paint on real road (not floating geometry)

The decision must be evaluated against ALL of these, not just current PoC gaps.

---

## Path comparison matrix

| Path | Per-pair shape | Per-segment delete | Custom styles | Visual quality | Performance | Complexity to build | Risk of failure | Future ceiling |
|---|---|---|---|---|---|---|---|---|
| **A. ECS sublane re-investigate** | ❌ tiled fixed mesh | ❌ would need separate prefab per range | ⚠ one prefab = one style; can add more | ✅✅ full vanilla | ✅✅ vanilla BRG | ⚠ 1-2 sessions diagnostic + unknown fix | high (we already failed once) | low — locked to vanilla mesh shapes |
| **B. Extruded box + DefaultDecalShader** | ✅ procedural | ✅ split mesh on delete | ✅✅ any shader-graph asset | ✅ if works | ✅ same as current | low (1 session) | medium (untested combo, may share Q1 bug) | medium — depends on shader behaviour |
| **11. EAI recipe + DrawMesh** | ✅ procedural | ✅ same as B | ✅ via SurfaceAsset templates | ✅ if works | ✅ same as current | medium (2-3h reading + porting) | medium-high (EAI internals may not fit per-frame draw) | high — vanilla material pipeline |
| **2. HDRP/Lit swap** | ✅ procedural | ✅ same as current | ❌ Lit shader doesn't look like paint | ⚠ partial (fixes night-glow only) | ✅ same | trivial (30 min) | low | low — known compromise |
| **C. Freeze + segmentation** | ✅ procedural | ✅ | ❌ stuck on Unlit white-only | ❌ current state | ✅ same | zero (it's done) | n/a | none — visual ceiling locked |
| **EAI as soft dependency** | ❌ static pre-built assets | ❌ no procedural | ✅ via authored decal packs | ✅✅ vanilla | ✅✅ vanilla | medium | low (proven by other mods) | low — user must install EAI |
| **DecalProjector** | ⚠ rectangular only | ⚠ | ✅ | ⚠ broken per Unity issue tracker | ⚠ GameObject overhead | high | **very high** | n/a |
| **AssetBundle custom shader** | ✅ procedural | ✅ | ✅✅✅ unlimited | ✅ | ✅ | very high (days, needs Unity Editor) | medium | very high — full creative freedom |

Legend: ✅✅ excellent, ✅ good, ⚠ partial/risky, ❌ blocker

---

## Detailed analysis — top 3 candidates

### 🥇 Path B — Extruded box + DefaultDecalShader

**What it is:** keep our `MarkingMeshRenderSystem` exactly as today, but change
two things:

1. `BuildRibbonMesh` extrudes the Bezier ribbon vertically into a thin box
   (~0.5m tall) instead of leaving it as a flat strip — 8 vertices per segment
   instead of 2
2. Material becomes a `new Material(BH/Decals/DefaultDecalShader)` (borrowed
   shader, not material) + ValidateMaterial; this shader projects the texture
   onto whatever surface the box volume contains

**Why this is the leading candidate:**

- **All the killer-feature requirements are already met by current architecture
  and don't change.** Per-pair geometry, per-segment delete (just clip the
  Bezier range and rebuild mesh), styles (one mesh-gen function per style),
  default generator, persistence, performance — none need to change to support
  the segmentation feature
- **The visual gap is the only thing being addressed.** And it's the right
  diagnosis: agent's deep dive confirmed `DefaultDecalShader` requires a
  cube volume, not flat geometry. Our previous "decal shader gave zero pixels"
  was correctly attributed to mesh-shape mismatch, not shader-incompatibility
- **Fall-back is zero-cost**: if Path B doesn't work, we revert to Path C
  (current state) by changing 3 lines of code

**Risks:**

- We don't know if `DefaultDecalShader` works correctly when fed via a
  user-built material vs through vanilla's `ManagedBatchSystem.CreateMaterial`
  pipeline. Unity issue tracker shows HDRP runtime materials are flaky;
  `DefaultDecalShader` is a Colossal shader, behaviour unknown
- Extruded box creates internal faces that the shader may render as
  artifacts (unless we set up culling properly)
- Performance of 12-24 extruded boxes per pair × N pairs may show up
  (it's still 1 DrawMesh per pair, but 4x the vertices)

**Cost:** 1 session (~3-4h) to spike. Bounded outcome — either works or we
know within an hour it doesn't.

**Future ceiling:** medium-high. Same as today (no architecture change) but
with proper decal look.

---

### 🥈 Path A — Re-investigate ECS sublane

**What it is:** revive `MarkingPairEmissionSystem` (the abandoned Phase 4 step 2)
with diagnostic instrumentation. Spawn one sublane via `CreateEntity(arch) +
SetComponent(...)`. Same frame, find a vanilla edge-line sublane on a nearby
road and dump both entities side-by-side every frame for 60 frames.
Difference reveals the missing flag/component/value.

**Why it's appealing:**

- **Full vanilla quality for free** — if it works. No shader work, no
  z-fight tuning, no decal layer mask figuring. Decal renders exactly like
  any vanilla road marking
- **Performance is whatever vanilla performance is** — BRG-instanced,
  scales to thousands
- **Layer 1 already proves the prefab cloning side works** — we just need
  the spawning side to also work
- **agent's analysis flipped the previous "PrefabSystem doesn't support
  this" claim**. `RequiredBatchesSystem` IS prepared to initialize batch
  groups for any matching entity. We just need the entity to match the
  right query

**Why it loses to Path B nonetheless:**

- **Per-pair custom geometry is fundamentally impossible** in this path.
  Vanilla pipeline uses **one shared mesh per prefab, tiled along curves
  via shader uniforms**. Path A means our marking shape is locked to
  whatever mesh the prefab provides. To get a wider line we'd need a
  separate prefab. To get a dashed line we'd need a separate prefab. To
  segment-delete a portion of a line, we'd need to spawn TWO sublanes
  with different curves
- **Segmentation feature becomes 3x more complex.** Where Path B can
  "just clip the Bezier", Path A needs to manage two separate sublane
  entities per split, manage their lifecycles, deal with vanilla's
  `m_OldLanes` GC, etc. We already burned 4 commits on this exact pain
  in Phase 4 step 2
- **Diagnostic effort is open-ended.** Agent enumerated 7 possible
  failure modes; could take 1-2 sessions to isolate. With no guarantee
  the fix is simple
- **Even if it works, every new style = new prefab clone.** Adding
  "dashed line" = duplicate the prefab, swap mesh asset, register again.
  Doable but cumbersome

**Cost:** 1-2 sessions to diagnose, then ~half session to apply fix.
Bounded by patience for diagnostic iteration

**Future ceiling:** high on visual quality, **low on extensibility**.
Locked to mesh-asset-based styles, painful segmentation.

---

### 🥉 Path 11 — EAI material recipe + our DrawMesh

**What it is:** read EAI's `DecalsImporterNew.cs` + `ImportersUtils.cs` source
to extract the material-construction logic (creates `SurfaceAsset` with
`colossal_DecalLayerMask`/`colossal_TextureArea`/`colossal_MeshSize`
properties, wraps in `RenderPrefab`, registers via `PrefabSystem.AddPrefab`).
Use that pre-built material (which vanilla's pipeline understands) but feed
it our procedurally-built Bezier mesh through `Graphics.DrawMesh` (same as
current PoC).

**Why interesting:**

- **Material is constructed exactly the way vanilla expects.** No "ValidateMaterial
  derives keywords from properties" guessing; we use the same path EAI exercises
  and that works in 5+ shipping mods
- **Decal shader will likely render correctly** because the material is a
  proper SurfaceAsset descendant, not a hand-built `new Material()`
- **Our DrawMesh path is unchanged** — same Bezier ribbon, same render loop

**Why it loses to Path B:**

- **EAI's recipe builds a material expecting to be drawn by vanilla's
  `ManagedBatchSystem` instance loop** with `colossal_GeometryTiling`
  keyword + per-instance Curve uniforms uploaded via BRG. Our DrawMesh
  doesn't set up those uniforms. **The material is built right; the
  invocation context is wrong**
- **Effectively a longer way to discover what Path A already tells us**:
  vanilla materials need vanilla per-instance setup, full-stop
- **No proven precedent.** Path B has Layer 1 as proof that
  DefaultDecalShader CAN be used (via vanilla pipeline). Path 11 would
  be the first time anyone uses a vanilla-style SurfaceAsset material
  through Graphics.DrawMesh

**Cost:** 2-3h reading + 1h implementing = ~half session.

**Future ceiling:** high if it works, but agent's analysis suggests it
won't — material is right, invocation channel wrong.

---

## Other paths (briefly)

- **HDRP/Lit swap (Path 2)** — 30-min quick win on night-glow problem only.
  Useful AS A FALLBACK if Path B fails, not as the destination
- **EAI soft dependency (Path 9)** — destination is wrong: their pipeline
  is for pre-built static decals, not procedural per-pair geometry. Fails
  the killer-feature requirement
- **Custom AssetBundle (Path 6)** — highest ceiling but days of work and
  Unity Editor with matching HDRP installed. Tier-2 polish
- **DecalProjector** — Unity issue tracker confirmed broken for
  runtime-built materials. Skip
- **DecalSystem static API** — same broken pipeline. Skip

---

## Recommendation

**Sequence: B → 11 → A → C**

1. **First try Path B** (~3-4h, one session). Extruded thin-box mesh +
   `DefaultDecalShader` material. Quick to spike, bounded outcome,
   minimal architectural change. If works — solves all visual gaps
   without architecture rewrite
2. **If B fails** (zero pixels or terrible visual) — try **Path 11**
   (~3-4h). Use EAI's material recipe; if SurfaceAsset-built material
   renders via DrawMesh with our ribbon, great; if not, we've learned
   the invocation-channel limit firsthand
3. **If both 11 and B fail** — fall back to **Path 2** (HDRP/Lit swap)
   as cosmetic improvement, then drive forward on segmentation feature
   with current architecture. Visual quality is non-ideal but feature
   set is intact — and that's what differentiates the mod, not the
   polish
4. **Path A** stays in reserve. The diagnostic effort is real and the
   payoff is highest visual quality, but it's the wrong direction for
   the killer feature (per-pair custom geometry locks us out). Only
   consider this if the user decides visual perfection > segmentation
   flexibility

**Path C (freeze current)** is fine if user wants to stop spending time
on rendering polish entirely. Mod ships, visual is functional, focus
moves to features

---

## Cost / time / risk summary

| Path | Time | Risk | Rollback cost | Net value if works | Net value if fails |
|---|---|---|---|---|---|
| B | 3-4h | medium | 30 min | huge | small (learned shader behaviour) |
| 11 | 3-4h | medium-high | 30 min | huge | medium (learned material construction) |
| A | 4-12h | high | 30 min | high but architecturally limiting | huge (learned vanilla pipeline) |
| 2 | 30 min | low | 5 min | small but real | nothing learned |
| C | 0 | none | n/a | nothing | nothing |

---

## What to commit before stopping

Already committed:
- `research/RESEARCH_rendering_approaches.md` (approach catalogue + web findings)
- `research/RESEARCH_vanilla_decal_pipeline.md` (agent deep dive)
- This file: `research/DECISION_path_forward.md` (← decision matrix, current)

To commit before /compact:
- Update `SESSION_2026-05-17.md` with the new top-pick (Path B) and the
  three rejected paths' rationale
- Optionally update `IMPLEMENTATION_PLAN.md` Layer 4 section to mention the
  re-evaluation

---

## Quick-reference: what's currently working (so future-self doesn't break it)

- Commit `512149c` is the working PoC
- `MarkingMeshRenderSystem.cs` is the live system; everything in it works
- `HDMaterial.ValidateMaterial` is mandatory; without it nothing renders
- `mesh.bounds.Expand((0, 1, 0))` is mandatory; without it HDRP frustum
  culling drops flat quads
- `Mod.cs` has `MarkingPairEmissionSystem` + `UserPairEmissionDumpSystem`
  registrations commented out — those are the dead-end Phase 4 step 2
  experiments; can be deleted entirely if cleaning up
