# IMPLEMENTATION PLAN — TownRoadLane v2

> **Status:** Phase 4 ✅ COMPLETE (commit `cdc96a5` — 2026-05-17 evening).
> **Branch:** `v2`. **Next: Phase 5 (Marking customization MVP)** — see below.
> **Companion docs:** all `RESEARCH_*.md` in `research/`; `SESSION_2026-05-17.md`.

---

## Phase 5 — Marking customization MVP

Phase 4 delivered: per-node user pairs render as proper vanilla decals
via ECS sublane emission. Phase 5 builds the killer feature stack on top.

### Recipe that finally worked (Phase 4 — don't break these in Phase 5)

These are **earned invariants** — each cost real hours of debugging. They
must be preserved by any future emission/lifecycle code:

- **Use `NetLaneArchetypeData.m_LaneArchetype` from a `SecondaryLane`-tagged
  prefab** (our `EdgeLineCloneSystem` clones already qualify). The archetype
  auto-includes `Game.Net.SecondaryLane`, which:
  - Protects from `LaneSystem.RemoveUnusedOldLanes` GC (skips Secondary
    in `FillOldLaneBuffer` at LaneSystem.cs:2022).
  - Triggers `SecondaryLaneReferencesSystem` (Modification5) to auto-register
    us in `node.SubLane` with `PathMethods = ~allKnownMethods` (decoration
    sentinel — pathfinder ignores us).
- **Dedup key = `(node, bufferIndex)`, NEVER a geometry hash.** Earlier rev
  hashed (sourceEdge, sourceGap, targetEdge, targetGap) into a synthetic key;
  the hash collapsed when the same edge pair had matching gap indices on
  both ends, silently dropping ~20% of user pairs. `bufferIndex` is uniquely
  stable per (node, tick) — no collision possible.
- **Do NOT mark owner node `Updated` after spawn.** Cascade fanouts through
  `LaneSystem` → `SecondaryLaneSystem` produced runaway spawn loops in earlier
  attempts.
- **All structural changes via one ECB per OnUpdate, played back at end.**
  Direct `EntityManager.AddComponent` mid-update invalidates query iterators
  and produces "delete one → another appears" thrashing.
- **PathNode slot base ≥ 32768** (`idxBase = 32768 + bufferIdx * 4`) to keep
  3 slots/pair clear of vanilla primary lane allocations (which start at 0).
- **NEVER `Graphics.DrawMesh + decal shader` or `MeshRenderer + decal shader`
  paths.** The vanilla `BH/Decals/CurvedDecalShader` reads `colossal_CurveMatrix`
  as a DOTS InstanceProperty supplied only by BRG. Outside BRG it defaults
  to zero, collapsing geometry. The non-curved `BH/Decals/DefaultDecalShader`
  has the same family of problems. Full dead-end exploration archived in
  `research/RESEARCH_decal_*.md` (3 docs, ~1500 lines). Single moral: route
  decals through ECS sublanes, full stop.

### Stage 5a — Minimal UX polish (~1 session, pure C#)

Goal: lower friction for own testing of later stages. No React yet.

- Toolbar button (use vanilla `ToolbarUISystem` C# API; pattern from
  `decomp/Game/Game.UI.InGame/` or RoadBuilder mod source).
- Hover-highlight selectable nodes when tool is active (raycast through
  `ToolBaseSystem.GetRaycastResult` → overlay through existing
  `MarkingOverlaySystem`).
- ESC/right-click consistent cancel behavior.
- Quick-fix any pain points discovered in Phase 4 testing (overlay polish,
  visual feedback).

### Stage 5b — Segmentation core (~1-2 sessions, no UI)

Goal: data model + intersection math + spawn-per-segment. Test through
dump systems and hardcoded fixtures.

**Data model swap:**
- `MarkingPair` buffer → split into:
  - `MarkingLine` buffer: `{ sourceEndpoint, targetEndpoint, style }` (logical)
  - `MarkingSegment` buffer: `{ lineIndex, tStart, tEnd, visible }` (drawable)
- New line auto-gets one segment `[0, 1, visible=true]`.
- Save/migration: detect old `MarkingPair` on load, transform to new format
  one-time per save (mark MarkingPair as `[Obsolete]` schema v2 → v3 migration).

**Intersection math:**
- `Bezier4x3 ∩ Bezier4x3` via Bezier clipping (recursive subdivision,
  ~50-80 LOC). Result: list of `(tOnLineA, tOnLineB)` intersection points.
- When user adds/edits a line, re-compute intersections with every other
  line on the same node. Update each affected line's `MarkingSegment` buffer:
  insert/merge segment boundaries at intersection `t` values.
- Each new boundary spawns 2 segments by default both `visible=true` (line
  unchanged visually until user explicitly hides a segment).

**Emission update:**
- `MarkingPairEmissionSystem` → renamed `MarkingSegmentEmissionSystem`.
- Iterate `MarkingSegment` buffer instead of `MarkingPair`.
- For each `visible=true` segment, compute `Bezier4x3.Cut(originalBez, tStart, tEnd)`
  (use Colossal math API if available; if not, ~12 LOC De Casteljau subdivision).
- Spawn entity per visible segment.
- Dedup key = `(node, lineIndex, segmentIndex)`.
- PathNode slot base = `32768 + lineIndex * 256 + segmentIndex * 4`
  (max 256 segments/line, 64 lines/node before overflow — far above usable).
- Same archetype, same Owner/Elevation pattern. NO other changes to the
  vanilla-side recipe.

**Testing fixtures:**
- Hardcoded "make 2 crossing lines on a 4-way intersection, verify both
  produce 2 segments after intersection" via debug overlay drawing parametric
  `t` markers.
- Dump system listing all `MarkingSegment` buffers + computed Bezier cuts.

### Stage 5c — Line styles (~1-2 sessions, simple C# UI)

Goal: solid / dashed / double / stop / crosswalk styles.

- Style enum on `MarkingLine` (or per-segment if granularity makes sense
  — open question, decide during implementation).
- Per-style prefab clone in `EdgeLineCloneSystem` (parallel to current EU/NA
  edge-line clones). One prefab per style; mesh choice via existing G87
  fallback chain.
- Style → which prefab to spawn lookup at emission time.
- Inline toolbar mini-buttons for style picker (3-5 icons, pure C# UI).
- Default new line = "Solid".

### Stage 5d — Segmentation UI (React, full UI overhaul)

Goal: hover/click selection of segments, style picker panel, pair management.

**Bootstrap:**
- Clone React+TS+SCSS+i18next setup from user's `SystemTimeMod` reference
  project (`C:\Users\Максим\RiderProjects\SystemTimeMod\SystemTimeMod\SystemTimeModUI`).
  Created via `create-csii-ui-mod` template, has webpack + i18next + lucide-react
  + sass-loader already wired. Saves hours of bootstrap.
- Bridge: existing C# tool emits state events; React consumes via cohtml
  bridge; React dispatches actions back through bound C# props.

**UI components:**
- Segment hover/click selection with overlay highlights (extends
  `MarkingOverlaySystem`).
- Per-node pair list panel (collapsed by default).
- Style picker per pair / per segment.
- Segment visibility toggles.
- Localization scaffolding (EN/RU at minimum).

### Open questions (defer to Phase 6+)

- **First-edit transition UX**: currently when user creates first pair on a
  node, ALL vanilla markings disappear (suppressed) leaving just the one
  custom line. Options for v3: auto-generate vanilla replica on first edit
  vs current "wipe and replace" vs layered mode.
- **Style granularity**: per-line vs per-segment. Per-segment is more
  powerful (e.g. solid → dashed → solid), per-line is simpler UI.
- **Vanilla edge-line intersection**: extending segmentation to vanilla
  lines too (the canonical "median crosses left-turn lane" scenario).
  Requires vanilla edge-line entity discovery + insertion into our
  intersection graph. High complexity.
- **Save format versioning**: MarkingPair → MarkingLine+MarkingSegment
  schema migration. Need version tag in buffer; one-time migrator at load.
- **Performance**: dense intersections (16-way roundabouts) could produce
  hundreds of segments. Profile before optimising; baseline Phase 5b first.

---

---

## ⚠ 2026-05-17 ARCHITECTURE PIVOT — Layer 4

The original plan (preserved unchanged below for historical context) assumed
**Layer 4 would create ECS sublane entities** with the right component set,
expecting vanilla rendering to pick them up automatically. This **does not work**:
vanilla `BatchInstanceSystem` requires `PrefabSystem`-baked batch-group
registrations that hand-created entities don't inherit. Even an entity with
identical component types to a vanilla edge-line sublane (`Curve`, `Lane`,
`Owner`, `PrefabRef`, `SecondaryLane`, `MeshBatch`, `CullingInfo`,
`LaneGeometry`, `Simulate`, `Elevation`) is silently ignored by the renderer.

### New Layer 4 — custom-mesh rendering

```
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 4 — Per-node MarkingPair UI tool + custom mesh renderer       │
│   MarkingNodeToolSystem  → MarkingPair buffer on node (unchanged)   │
│   MarkingMeshRenderSystem (NEW):                                    │
│     • Dictionary<PairKey, Mesh> cache, procedurally built           │
│     • RenderPipelineManager.beginContextRendering → Graphics.DrawMesh│
│     • Material: HDRP/Unlit + HDMaterial.ValidateMaterial            │
└─────────────────────────────────────────────────────────────────────┘
```

Pattern mirrors CS1 **NodeMarkup / IMT** (vendored at `_refs/IMT/`). Vanilla CS2
itself uses the same `Graphics.DrawMesh` pattern in 6+ systems
(`BrushRenderSystem`, `AggregateRenderSystem`, `AreaRenderSystem`,
`RouteRenderSystem`, `NotificationIconRenderSystem`, `OverlayRenderSystem`).

### Critical undocumented gotcha — `HDMaterial.ValidateMaterial`

**Must call** `UnityEngine.Rendering.HighDefinition.HDMaterial.ValidateMaterial(material)`
on every freshly constructed `Material`. Without it, HDRP shader keywords
(`_SURFACE_TYPE_OPAQUE`, `_BLENDMODE_*`, etc.) and stencil state are never
derived from material properties, so every render pass is culled to a no-op.
`Graphics.DrawMesh` dispatches but produces zero pixels — **no errors, no
warnings, no shader-compile log**. Vanilla CS2 calls `ValidateMaterial` in
`OverlayRenderSystem.cs:804`, `ManagedBatchSystem.cs:1392`,
`AssetImportPipeline.cs:195` — all from infrastructure code paths invisible from
normal "rendering example" reading.

Also: HDRP Forward+ frustum culling drops meshes whose bounds collapse on any
axis. Flat quads (4 verts in one plane) need `mesh.bounds.Expand((0, 1, 0))`
after `RecalculateBounds()`, or pass a non-unit-Y scale via `Matrix4x4.TRS` to
`DrawMesh` (vanilla does the latter — `BrushRenderSystem.cs:114`).

### Files added / changed by the pivot

| File | Status | Role |
|---|---|---|
| `MarkingMeshRenderSystem.cs` | **NEW** | Custom-mesh renderer — main new file |
| `Mod.cs` | Modified | Register `MarkingMeshRenderSystem` at `SystemUpdatePhase.Rendering`; deregister `MarkingPairEmissionSystem` + `UserPairEmissionDumpSystem` |
| `EdgeLineCloneSystem.cs` | Modified | Expose managed `ClonePrefabEU/NA` (kept for future material sourcing) |
| `TownRoadLane.csproj` | Modified | Added `Unity.RenderPipelines.HighDefinition.Runtime` reference |
| `MarkingPairEmissionSystem.cs` | **Inert** (kept in tree) | Deprecated ECS-sublane emission |
| `Diagnostics/UserPairEmissionDumpSystem.cs` | **Inert** (kept in tree) | Was for ECS-sublane debugging |
| `CustomSecondaryLaneSystem.cs` | Has `TRLPairLink` guard | Suppression gate for nodes with user pairs still active; sublane GC guard kept defensively |

### Roadmap (post-pivot) — toward IMT-equivalent feature set

The killer feature is **line-line intersection segmentation** (split markings at
crossings, delete individual segments) — IMT's main differentiator. Now reachable.

1. **Bezier ribbon mesh** (cosmetic, ~1h) — replace flat quad with curve-following
   ribbon. Reuses `chord*0.4` pull from `MarkingOverlaySystem`.
2. **Realistic look** (~2h) — road-decal material, ~0.02m height, alpha.
3. **Line styles** (~1d) — solid/dashed/double/stop. One mesh-gen function per style.
4. **Default markings generator** (~1d) — auto-fill MarkingPair buffer from vanilla
   NetCompositionLane so users don't draw every line manually.
5. **Bezier-Bezier intersection math** (~1d) — LUT-sample intersections per node.
6. **Segment delete UI** (~½d) — split rendering when delete range present.
7. **Fillers, decals, symbols** — IMT-tier, on demand.

See `SESSION_2026-05-17.md` for deeper notes.

---

## Historical 4-layer plan (preserved for context)

The plan as approved 2026-05-16 follows below. Layers 1–3 are still in effect
as designed. **Layer 4 has been replaced** by the custom-mesh approach above —
the descriptions of Layer 4 below should be read as "what we tried before the
pivot."

---

## Goal

Three features on top of vanilla CS2 markings:

1. **Edge line** on city 3 m drive lanes (same visual as v1.1; curb-side line that highway roads already have).
2. **Parking line + end ticks** along parallel street-parking zones (same visual as v1.1).
3. **Per-node UI tool** for marking customisation at intersections (Traffic-style connector tool, but for marking endpoints, not vehicle lanes).

Hard constraint: **G87 custom mesh ассеты обязательны.** The mod must let the user pick a custom mesh style for both edge and parking markings.

---

## Architecture — 4 layers

```
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 1 — Marking-prefab CLONES (PrefabUpdate phase, one-shot)      │
│   EdgeLineCloneSystem    → clones EU/NA Highway Edge Line           │
│   ParkingLineCloneSystem → clones EU/NA Car Bay Line + Cross Line   │
│   Both: SwapMesh from settings (vanilla or G87), UpdatePrefab       │
└─────────────────────────────────────────────────────────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 2 — Vanilla pipeline (CustomSecondaryLaneSystem, Mod4B)       │
│   Reads SecondaryNetLane buffers (now containing our clones)        │
│   Generates markings with correct geometry/intersections/LODs       │
└─────────────────────────────────────────────────────────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 3 — MarkingOverride per-entity skip (inside Layer 2 job)      │
│   Reads MarkingOverride { hide : MarkingCategory } on edge entity   │
│   Skips emission for masked categories                              │
└─────────────────────────────────────────────────────────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 4 — Per-node MarkingPair UI tool (phase 4)                    │
│   Hotkey-activated MarkingNodeToolSystem                            │
│   MarkingPair buffer on node entity                                 │
│   Suppression hashset (LaneEndKey pattern from Traffic) in job      │
└─────────────────────────────────────────────────────────────────────┘
```

**Why these 4 layers, and not the rejected alternatives:** see decision log at the
end of this document.

---

## Working-tree audit

### Keep (current files)

| File | Action | Why |
|---|---|---|
| `CustomSecondaryLaneSystem.cs` | Strip the edge-line inject (~110 lines) | The system itself is critical (replaces vanilla); only the inject experiment goes away. |
| `MarkingOverride.cs` | Unchanged | Layer 3 component, already bit-flag-ready for phase 4. |
| `MarkingToggleSystem.cs` | Repurpose: call clone systems' `ApplyOrUpdate` before mass-Updated | Same mechanism, expanded scope. |
| `Mod.cs` | Re-wire registrations | Drop `ParkingLineLinkSystem` + diag systems, register two new clone systems. |
| `Setting.cs` | Add style dropdowns + hotkey | See Phase 2. |
| `Diagnostics/RoadPrefabDumpSystem.cs` | Unchanged | Cheap one-shot, useful across game patches. |
| `Diagnostics/ParkingPairDumpSystem.cs` | Comment out registration, keep file | Useful for Phase 4 endpoint debugging. |

### Delete

| File | Why |
|---|---|
| `ParkingLineLinkSystem.cs` | Patched vanilla `EU Car Bay Line` directly. v2 uses CLONE (v1.1 pattern), not patch. |
| `Diagnostics/CarLaneFlagsDumpSystem.cs` | Was for edge-line inject debugging — inject removed. |
| `Diagnostics/ParkingLaneFlagsDumpSystem.cs` | Same — was for parking inject debugging. |

### Create

| File | Purpose | Approx size |
|---|---|---|
| `EdgeLineCloneSystem.cs` | Clone `EU/NA Highway Edge Line` + add city lanes + mesh swap | ~250 lines |
| `ParkingLineCloneSystem.cs` | Direct port of v1.1 `ParkingMarkingPatchSystem` | ~280 lines |

Future (phase 4, do not create yet): `MarkingNodeToolSystem.cs`,
`MarkingOverlaySystem.cs`, `MarkingPair.cs` (IBufferElementData), `MarkingEndpoint.cs`.

---

## Risk register

All phases reference these risks by ID. Each risk has a concrete decomp / v1.1
source citation — no hand-waving.

### K1. `UpdatePrefab` re-creates the prefab entity; cached `Entity` handles go stale

**Source:** `decomp/Game/Game.Prefabs/PrefabSystem.cs:747-770`. The old entity gets
`Deleted`; a new entity is created with `Created` + `Updated`; `m_Entities[key]`
remaps to the new one; `ReplacePrefab(oldEntity, newEntity, ...)` rewires refs in
the world.

**Mitigation:** Never cache `Entity` for marking prefabs. Always resolve through
`m_PrefabSystem.GetEntity(prefabBase)` or ECS lookup. v1.1 already follows this.

### K2. `UpdatePrefab` is asynchronous — bake runs on the next `MainLoop` frame

**Source:** `PrefabSystem.cs:306-309` (queue only) → `PrefabSystem.cs:721-728`
(`OnUpdate` runs `UpdatePrefabs()` then `m_UpdateSystem.Update(PrefabUpdate)`).

**Mitigation:** Run all clone work in `PrefabUpdate` phase once; on the same frame
the bake runs immediately after via the explicit `m_UpdateSystem.Update` call.
Reapply path (settings button) accepts a 1-frame visual lag — invisible to users.

### K3. `UpdatePrefab` silently no-ops if the prefab isn't registered

**Source:** `PrefabSystem.cs:745` — `if (m_Entities.TryGetValue(key, out var value2))`,
no else branch.

**Mitigation:** `DuplicatePrefab` itself calls `AddPrefab` internally
(`PrefabSystem.cs:273`), so v1.1's flow is safe. For our cloned prefabs we go
through `DuplicatePrefab` → `UpdatePrefab` directly; no extra `AddPrefab` call
needed.

### K4. Marking-prefab patches do NOT persist across save / load

**Source:** `RESEARCH_decomp.md` §7 — `SecondaryNetLane` buffers live on prefab
entities, prefab entities are re-baked from managed `PrefabBase` data every load
via `NetInitializeSystem`. Only `PrefabID` and `PrefabData` GUIDs are serialised
(`PrefabSystem.cs:792`), not buffer contents.

**Mitigation:** Both clone systems live in `PrefabUpdate` phase and are one-shot
per session (`m_Done = true; Enabled = false`). On every game load `OnCreate`
runs again on a fresh World, so `m_Done` starts false and the clones are
re-created. No serialisation work needed for Layers 1-2.

### K5. Mass `AddComponent<Updated>` on road edges can crash on Road-Builder geometry

**Source:** Commit `5bb0b5e` ("MarkingReapplySystem: skip Road Builder road edges
when re-baking") — the v1.1 reapply button on a city with an exotic
highway-based RB road with angled parking crashed inside
`SecondaryLaneSystem.UpdateLanes`.

**Mitigation:**
- The mass-Updated reapply is **button-triggered only**, never automatic.
- Filter out RB edges with the proven heuristic: prefab name starts with `r`/`R`
  and contains ≥ 4 dashes (`MarkingToggleSystem.cs:124-136`).
- Open question for Phase 2 testing: does our `CustomSecondaryLaneSystem` (a
  full copy of vanilla) hit the same RB pathology? Add a test step in Phase 2.

### K6. `SwapMesh` without `UpdatePrefab` leaves stale render data

**Source:** v1.1 `ParkingMarkingPatchSystem.cs:166` — every `SwapMesh` is
followed by `m_PrefabSystem.UpdatePrefab(cloneBase)`.

**Mitigation:** Always pair `SwapMesh` with `UpdatePrefab`. Codify by exposing a
single `ApplyMeshAndHosts(clone, mesh, secondaryLane)` helper that does both.

### K7. G87 mesh may not be loaded when our system runs

**Source:** v1.1 `ParkingMarkingPatchSystem.cs:208-214` — `ResolveMeshes` queries
all entities with `PrefabData + MeshData`; if G87 hasn't loaded yet, the query
returns no match.

**Mitigation:** Fallback chain (v1.1 already implements this):
`G87 name → vanilla name → keep source mesh`. Log at WARN level when fallback
kicks in. If the user installs G87 mid-session, the reapply button triggers a
fresh `ResolveMeshes` pass that picks up the now-loaded G87 mesh.

### K8. "Turn it off in settings" must actually remove the marking

**Source:** Behaviour bug seen in v1.x — disabling a feature in settings only
skipped the patch on first run; existing patches stayed applied.

**Mitigation:** Settings change triggers a reapply that handles all three states:
- ON, mesh A → ON, mesh B: `SwapMesh(B)` + `UpdatePrefab`
- ON → OFF: `sec.m_LeftLanes = Array.Empty<>()` (and `m_RightLanes`, `m_CrossingLanes`
  if relevant) + `UpdatePrefab` — the marking still exists as a prefab but is hosted
  on no lane, so vanilla never spawns it
- OFF → ON: full apply path

v1.1 has a half-version of this for end-ticks
(`ParkingMarkingPatchSystem.cs:172-188`); we generalise it.

### K9. Our system must run on `Modification4B`, not `Modification4`

**Source:** `decomp/Game/Game.Common/SystemOrder.cs:184` —
`ModificationBarrier4B` is the `SafeCommandBufferSystem` whose
`AllowBarrier<...>` window covers `Modification4B`. Running on `Modification4`
throws `Trying to create EntityCommandBuffer when it's not allowed!`.

**Mitigation:** `Mod.cs:61` already registers correctly. Add a comment in the
class header of `CustomSecondaryLaneSystem`. Any new system that needs an ECB on
the same barrier (none planned currently) must use the same phase.

---

## Phases

Each phase ends with a working game, a focused commit, and an explicit test
checklist. **Do not start phase N+1 until phase N is verified by playtest.**

### Phase 0 — Cleanup (no behaviour change)

**Goal:** the working tree reflects the audit above. Game still runs, parking
line still draws on 2-way Small Road (current `ParkingLineLinkSystem` baseline).

**Steps:**

1. Delete the three files marked "Delete" above. Verify build.
2. Strip the edge-line inject from `CustomSecondaryLaneSystem.cs` (lines
   ~900-962 inject block, ~2023-2025 fields, ~2074-2117 OnUpdate wire-up,
   ~2170-2182 resolve loop). Keep `MarkingOverride` lookup wiring intact —
   it's Layer 3 and stays.
3. Update `Mod.cs`: drop `ParkingLineLinkSystem`,
   `CarLaneFlagsDumpSystem`, `ParkingLaneFlagsDumpSystem` registrations.
   Comment out `ParkingPairDumpSystem` registration (file stays for Phase 4).
4. Sanity-build, run game, confirm no startup errors and no city-marking
   regression vs. vanilla.

**Note:** Phase 0 temporarily removes the working parking line (current
`ParkingLineLinkSystem`). Phase 1 restores it via the clone-based system.

**Done when:** clean build, no exceptions on load, vanilla markings unchanged.

**Commit:** `phase 0: drop inject experiment + obsolete diagnostics`

---

### Phase 1 — Layer 1 clone systems (no UI, vanilla mesh only)

**Goal:** city 3 m roads get the edge line; parallel-parking roads get parking
line + end ticks. Same visual as v1.1, but the implementation is fresh.

#### Step 1.1 — `ParkingLineCloneSystem.cs`

Direct port of v1.1 `ParkingMarkingPatchSystem` (commit `342afa4`, 241 lines).
File names and class name updated to v2 conventions:
- Class: `ParkingLineCloneSystem`
- Clone names: `TownRoadLane EU Parallel Parking Line`,
  `TownRoadLane NA Parallel Parking Line`,
  `TownRoadLane EU Parallel Parking End`,
  `TownRoadLane NA Parallel Parking End`
- Host lanes: `Car Drive Lane 3` + tram/PT variants on `m_LeftLanes`,
  `Parking Lane 2` on `m_RightLanes` for longitudinal,
  `m_CrossingLanes` with `RequireContinue=false` for end ticks.
- Settings hooks: `Settings.ParkingLineMeshName()`, `Settings.ParkingEndMeshName()`,
  fallbacks `White Dashed Line Mesh - Dense` / `White Solid Line Mesh`.

**Risks covered:** K1 (no Entity caching), K2 (run in PrefabUpdate), K3
(DuplicatePrefab handles AddPrefab), K4 (one-shot per session), K6 (SwapMesh +
UpdatePrefab paired), K7 (fallback chain).

**Reference:** `RESEARCH_v1_1.md` §2 has full config tables and `git show
342afa4:src/TownRoadLane/ParkingMarkingPatchSystem.cs` is the working source.

#### Step 1.2 — `EdgeLineCloneSystem.cs`

Port of v1.1 `EdgeMarkingPatchSystem` (commit `342afa4`, 174 lines), **but with
two changes**:

- v1.1 PATCHED vanilla `EU/NA Highway Edge Line` in place. **v2 CLONES** it so
  we can swap mesh independently of vanilla (G87 support).
- Use the same two-entry pattern per city lane (`{RequireSafe}` and
  `{RequireMerge, RequireSafeMaster}`) — see `RESEARCH_v1_1.md` §1 for the
  rationale (merge-aware continuity through onramps).

Clone names: `TownRoadLane EU City Edge Line`, `TownRoadLane NA City Edge Line`.
Host lanes: same four as v1.1 (`Car Drive Lane 3` + tram/PT variants) on
`m_LeftLanes` only (`m_CanFlipSides=true` handles the mirror).

Settings hook: `Settings.EdgeLineMeshName()`, fallback
`White Solid Line Mesh - Dense` (or whichever vanilla mesh `EU Highway Edge Line`
ships with; verify at Phase 1 start).

**Risks covered:** same as 1.1.

#### Step 1.3 — `Setting.cs` style dropdowns

Add per-feature style dropdowns:
- `EdgeLineMeshName` — `enum EdgeLineStyle { None, Vanilla, G87 }`
- `ParkingLineMeshName` — `enum ParkingLineStyle { None, Vanilla, G87 }`
- `ParkingEndMeshName` — `enum ParkingEndStyle { None, Vanilla, G87 }`

Each backed by a `Settings.<Name>MeshName()` getter returning the actual mesh
name string (or `null` for `None`, which triggers the K8 "host nothing" path).

**G87 mesh names:** verbose CO strings, see v1.1
`Setting.cs:ParkingLineMeshName()` for examples. Keep them as `const string` in
`Setting.cs`.

Rename `ToggleAllMarkings` button to `ReapplyMarkings`. Its handler:
1. `EdgeLineCloneSystem.RequestReapply()`
2. `ParkingLineCloneSystem.RequestReapply()`
3. `MarkingToggleSystem.RequestReapply()` — mass-Updated on edges (RB-filtered)

#### Step 1.4 — `MarkingToggleSystem.cs` refactor

Existing system stays; rename `RequestToggle` → `RequestReapply` and add
"call clone systems first" wiring (mirrors v1.1 `MarkingReapplySystem.cs:64-66`).
RB-skip stays as is (`MarkingToggleSystem.cs:124-136`). The hide/show toggle
becomes a separate dev-only button (or is removed — `MarkingOverride` per-edge
toggling will be a Phase 4 tool).

#### Step 1.5 — `Mod.cs` registration

```
PrefabUpdate    : EdgeLineCloneSystem, ParkingLineCloneSystem,
                  RoadPrefabDumpSystem (kept), 
                  (ParkingPairDumpSystem registration stays commented out)
Modification1   : MarkingToggleSystem (idle until button)
Modification4B  : CustomSecondaryLaneSystem
                  + vanilla SecondaryLaneSystem.Enabled = false
```

**Test checklist (Phase 1):**

1. Build clean, no warnings about K1-K9 violations.
2. Fresh save, vanilla 2-way Small Road with parking → parking line present.
3. Small Road Oneway 3-lane → no parking line (uses `Boarding Lane 0`, expected,
   matches v1.1 limitation per `RESEARCH_v1_1.md` §4).
4. City Small Road, Medium Road → edge line on both sides at the curb.
5. Highway road → vanilla edge line still present (our clone is independent).
6. RB road built with vanilla `Car Drive Section 3` → edge line + parking line
   inherited automatically (per `RESEARCH_road_builder.md` §5).
7. RB road with "No Markings" option → no edge / parking line (uses
   `Alley Drive Section 3`, naturally excluded).
8. Settings → toggle `EdgeLineEnabled` OFF → click Reapply → edge line disappears
   on all roads. Toggle ON → click Reapply → edge line returns.
9. Pick G87 mesh in settings (if G87 installed) → click Reapply → mesh changes.
10. **K5 regression test:** open a save with an exotic RB road
    (highway-based with angled parking, if available) → click Reapply → no crash.
11. Save and reload → all markings still present after reload (validates K4
    mitigation).

**Commit:** `phase 1: clone-based Layer 1 (edge + parking with G87 support)`

---

### Phase 2 — Layer 3 cleanup, robust reapply

**Goal:** the existing `MarkingOverride` skip path (Layer 3) is the canonical
on/off mechanism per edge. The settings reapply button is the canonical
"apply settings change everywhere" mechanism.

#### Step 2.1 — Verify Layer 3 still works after Phase 0 inject removal

Phase 0 strips the inject block but **must keep** the `MarkingOverride` lookup
fields and the `skipMarkingGeneration` label in
`CustomSecondaryLaneSystem.cs`. Verify by:
1. Manually adding `MarkingOverride{hide=All}` to a single edge via debug code.
2. Confirming all markings disappear from that edge on next `Updated` tick.

#### Step 2.2 — Per-category gating in the job

Today `MarkingOverride` is checked once at the top of `UpdateLanes` as
`HideAll`. Add per-category guards before each `CreateSecondaryLane` site
(see `RESEARCH_decomp.md` §5 — four call sites at vanilla lines ~797, ~801,
~805, ~821).

The gating uses `MarkingOverride.Hides(MarkingCategory.X)`. Mapping which
category covers which call site is straightforward for our two clones (their
prefab entities are known, so we can compare `secondaryNetLane2.m_Lane` to
`m_OurEdgeLineEU/NA` and `m_OurParkingLineEU/NA` and gate the corresponding
category bit).

Vanilla markings (dividers, crosswalks, etc.) get a `VanillaAll` category bit
for now — fine-grained vanilla suppression is Phase 4 territory.

#### Step 2.3 — Reapply button polish

Verify the three-step reapply flow does the right thing in every K8 state
combination:
- ON, mesh A → ON, mesh B
- ON → OFF
- OFF → ON
- OFF → OFF (no-op)

Add WARN log line per skipped state for diagnostics.

**Test checklist (Phase 2):**

1. All Phase 1 tests still pass.
2. Manually inject `MarkingOverride{hide=EdgeLine}` on one edge → only edge line
   gone on that edge, parking line and vanilla markings remain.
3. `MarkingOverride{hide=ParkingLine}` → only parking line gone.
4. `MarkingOverride{hide=All}` → all markings gone on that edge.
5. RemoveComponent → markings return.
6. Repeated reapply with no settings change → no visual flicker, no log spam.

**Commit:** `phase 2: per-category MarkingOverride gating + reapply polish`

---

### Phase 3 — Stabilise, real-world testing

**Goal:** confirm Phase 1+2 hold up on diverse saves. No new features.

**Test matrix:**

| Save type | Expected |
|---|---|
| Empty fresh save | No errors on load, default markings present |
| Big city (50k+ population, mix of all road types) | Smooth performance, no crash |
| Save with G87 installed | G87 meshes used |
| Save with G87 *not* installed but settings ask for G87 | Vanilla fallback, WARN logged |
| Save with Road Builder + RB roads | RB roads inherit edge/parking on `Car Drive Section 3` (per RB research) |
| Save with Traffic mod loaded | No interaction issues (both replace different systems: LaneSystem vs SecondaryLaneSystem) |
| Save → close → reload | Markings persist (K4 mitigation works) |
| Settings change → Reapply on big save | Brief freeze, no crash (K5 mitigation) |

**Bug-fix budget:** allocate 1-2 sessions for fixes found here. Do not start
Phase 4 with known instabilities.

**Commit per fix.** No single phase commit.

---

### Phase 4 — Layer 4: Per-node UI tool (MarkingPair)

**Goal:** user activates tool via hotkey, clicks a node, sees connector dots at
marking endpoints, drags lines to define/suppress marking connections.

This is the largest phase. Sub-phases:

#### 4a. ECS infrastructure (no UI)

- `MarkingPair` (`IBufferElementData` on node entity) — fields per
  `RESEARCH_traffic.md` §1 (adapted for markings, not vehicle lanes):
  ```
  Entity sourceEdge, int sourceLaneIndex;
  Entity targetEdge, int targetLaneIndex;
  MarkingCategory category;   // which marking type this pair governs
  ConnectionMode mode;        // Force | Suppress
  ```
- `ISerializable` implementation with versioned header
  (`RESEARCH_traffic.md` §2).
- `LaneEndKey(int2)` struct copied from
  `_refs/Traffic/Code/Systems/Traffic_LaneSystem.cs:132-152`.
- Suppression hashset built per node in `CustomSecondaryLaneSystem.UpdateLanes`
  (mirrors `Traffic_LaneSystem.cs:1030-1079`). Added to the `[ReadOnly]` job
  fields.
- `continue` gates at the four `CreateSecondaryLane` call sites (per
  `RESEARCH_decomp.md` §5).
- Explicit creation pass for `Force` mode pairs after the suppression loop
  (mirrors `Traffic_LaneSystem.cs:1258-1325`).

**Test:** manually inject `MarkingPair` buffer entries on a node via debug code,
verify markings appear/disappear as configured.

**Commit:** `phase 4a: MarkingPair buffer + suppression hashset`

#### 4b. Tool scaffold + hotkey

- `MarkingNodeToolSystem : ToolBaseSystem` —
  `toolID = "MarkingNodeTool"`. Per
  `RESEARCH_ui_framework.md` §1.
- State machine: `Default → NodeSelected → EndpointHovered → DraggingLine`.
- `InitializeRaycast()` sets `TypeMask.Net`, filter on `HasComponent<Node>`
  and `ConnectedEdge.Length >= 2`.
- Settings hotkey binding (`Setting.cs`):
  ```
  [SettingsUIKeyboardAction(ToggleMarkingTool, Usages.kDefaultUsage, Usages.kToolUsage)]
  [SettingsUIKeyboardBinding(BindingKeyboard.M, ToggleMarkingTool, ctrl: true)]
  ```
- A `GameSystemBase` polls `Setting.GetAction(ToggleMarkingTool).WasPerformedThisFrame()`
  and sets `m_ToolSystem.activeTool = _markingTool` (mirrors
  `RESEARCH_ui_endpoints.md` §2.3).

**Test:** Ctrl+M activates tool, click on node logs "node N selected", click
elsewhere or Esc → Default state.

**Commit:** `phase 4b: tool scaffold + hotkey activation`

#### 4c. Endpoint extraction

- `MarkingEndpointExtractor` (managed helper, called when node selected):
  reads `ConnectedEdge`, walks `SubLane` per edge, applies the lane-corner
  formula from `RESEARCH_ui_endpoints.md` §1.3 (citing
  `SecondaryLaneSystem.cs:359-422`).
- Result: `List<MarkingEndpoint> { float3 pos, float2 tangent, Entity edge,
  int laneIndex, bool isRight, MarkingCategory available }`.
- Filter by `m_PrefabSecondaryLanes.HasBuffer` to drop utility/non-marking
  lanes (per `RESEARCH_ui_endpoints.md` §3 open question — needs test).

**Test:** activate tool, click node, log endpoint list. Verify count matches
visible lane count × 2 (left/right).

**Commit:** `phase 4c: endpoint extraction`

#### 4d. Overlay rendering

- `MarkingOverlaySystem : GameSystemBase`. Gated by
  `_toolSystem.activeTool == _markingTool`.
- Uses `OverlayRenderSystem.GetBuffer()` per `RESEARCH_ui_framework.md` §3.
- Draws:
  - All endpoint dots (`DrawCircle`, ~1 m diameter, cyan).
  - Hovered dot highlighted white.
  - Active drag-line if `DraggingLine` state (`DrawLine`).
  - Confirmed `MarkingPair` connections (`DrawCurve` between source/target).

**Test:** dots appear at expected positions, drag draws line, line follows
cursor, click-target creates pair.

**Commit:** `phase 4d: overlay rendering`

#### 4e. Click-to-connect / disconnect mechanic

- `applyAction.WasPressedThisFrame()` in `EndpointHovered` →
  transition to `DraggingLine`, record source.
- In `DraggingLine`: hover hit-test on other endpoints; on apply, write
  `MarkingPair` entry on node entity; on cancel, abort.
- Right-click (`secondaryApplyAction`) on existing pair → remove it.

**Test:** end-to-end. Create pair, save, reload, pair persists (validates the
`ISerializable` from 4a).

**Commit:** `phase 4e: click-to-connect mechanic`

#### 4f. Polish

- Tooltips ("Click to connect source endpoint").
- Colour coding by category (edge / parking / vanilla).
- Esc cancels drag, double-Esc deactivates tool.
- Settings: descriptive locale text for the hotkey binding.

**Commit:** `phase 4f: tool polish`

---

### Phase 5 (deferred, not in scope) — Per-segment style switching

Not in this plan. If needed later: N clones of each marking prefab (one per
style) + per-entity `MarkingStyle { byte styleId }` + dispatch in job. Design
TBD when the need actually arises.

---

## Hard constraints (recurring)

- **Never edit drive-lane prefabs.** Only marking prefabs. v1.x crashed when it
  cloned `Car Drive Lane 3` (`MarkingUpgradePrefabSystem`,
  `MarkingLaneSubstituteSystem`); confirmed in `RESEARCH_v1_1.md` §5. Drive
  lanes are referenced in serialised road composition data.
- **`CustomSecondaryLaneSystem` runs on `Modification4B`** (K9).
- **Mass-Updated skips RB roads** (K5). Encoded in
  `MarkingToggleSystem.LooksLikeRoadBuilderRoad`.
- **Cache `PrefabBase`, not `Entity`** (K1).
- **One-shot systems** (`m_Done = true; Enabled = false`) live in `PrefabUpdate`
  phase and re-run on every game load via fresh `OnCreate` (K4).

---

## Decision log — why these layers, not the alternatives

Five architectures were considered. Four rejected, one chosen. Reasoning logged
here for future-self reference.

| Approach | Verdict | Reason |
|---|---|---|
| **1. Clone marking prefabs + mesh swap** (Layer 1 chosen) | ✅ Chosen | v1.1 proved stable for months. Supports G87 mesh. RB inherits markings automatically because clones reference vanilla `Car Drive Lane 3` (the SecondaryNetLane buffer on the vanilla lane prefab gets our clones added by NetInitializeSystem). |
| 2. Inject into CustomSecondaryLaneSystem | ❌ Rejected | Vanilla pair-matching `.zwxy` swizzle (`SecondaryLaneSystem.cs:607-620`) works only for at-node corners, not adjacent parallel lanes on the same edge. Verified empirically over multiple sessions. |
| 3. Harmony patch vanilla SecondaryLaneSystem | ❌ Rejected | Burst-compiled job. IL transpilers don't run post-Burst. Traffic mod itself ships a full copy of `LaneSystem.cs` rather than Harmony — strong signal. |
| 4. Create a brand-new marking prefab from scratch | ❌ Rejected | Requires manually composing MeshData, MaterialData, RenderPrefab, SubMesh, LOD chain, ThemeObject. `DuplicatePrefab` (approach 1) gives all of this for free. |
| 5. Patch vanilla prefab in place (no clone) | ❌ Rejected for primary path | Can't swap mesh without affecting vanilla highways. Acceptable as fallback for "vanilla mesh" mode but adds two code paths; not worth the complexity. |

The **Layer 4 suppression** (`LaneEndKey` hashset) is borrowed verbatim from
Traffic; see `RESEARCH_traffic.md` §3 for rationale.

---

## Open items (research → defer)

- **Endpoint extraction edge cases** — `EdgeLane.m_EdgeDelta` precision on
  certain road types (`RESEARCH_ui_endpoints.md` §3). Verify at Phase 4c via
  the `ParkingPairDumpSystem` pattern.
- **NodeLane sublanes at intersections** — do they need their own endpoint
  list? (`RESEARCH_ui_endpoints.md` §3). Test at Phase 4c.
- **`MathUtils.Right` / `MathUtils.Left` accessibility** from our assembly
  (`RESEARCH_ui_endpoints.md` §3). If `internal`, implement inline.
- **CustomSecondaryLaneSystem behaviour on exotic RB geometry** (K5 open). Test
  at Phase 1 step 10.
- **React-based floating toolbar button** — Phase 4 ships hotkey-only. If
  desired later, see Traffic UI bundle as a template
  (`RESEARCH_ui_endpoints.md` §2.2).
