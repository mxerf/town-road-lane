# Custom Marking Rendering in CS2 — Approach Survey

Comprehensive scan of every reasonable path for rendering custom road markings
from CS2 mod code, with pros/cons and feasibility verdict per path. Built up
incrementally from session findings + WebSearch + Context7 docs.

**Status:** in progress. Sources cited inline.

---

## Problem statement

We need to render custom user-drawn road markings (Bezier-arc lines between
endpoint pairs on intersection nodes) with these qualities:

1. **Visually attached to road surface** — no floating gap, no z-fight, follows
   road slope and crowning.
2. **Reacts to ambient lighting** — dim at night, bright at noon, shadowed
   under bridges.
3. **Production-quality textures** — paint-like, can be stylized (solid,
   dashed, double, stop, etc.).
4. **Cheap enough** for hundreds of pairs across a large city.
5. **Survives save/reload** — pairs are in `MarkingPair` buffer (already
   `IBufferElementData` + `ISerializable v2`).

Current PoC (commit `512149c`): `MarkingMeshRenderSystem` builds Bezier ribbons,
draws via `Graphics.DrawMesh` + `HDRP/Unlit` + `HDMaterial.ValidateMaterial`.

**Known gaps with current PoC:**
- Floats 0.01m above road (visible at grazing angles)
- No lighting reaction (Unlit shader = always full bright)
- Borrowed `_BaseColorMap` texture from `BH/Decals/CurvedDecalShader` material
  was authored for projection, not direct sampling — looks weird
- Ribbon ignores road slope/crowning entirely

---

## Approach catalogue

### 1. Current — Bezier ribbon mesh + HDRP/Unlit + DrawMesh

**Status:** working, commit `512149c`.

**How:** `MarkingMeshRenderSystem.cs` builds 24-segment ribbon along Bezier,
calls `Graphics.DrawMesh` from `RenderPipelineManager.beginContextRendering`.
Material is fresh `new Material(Shader.Find("HDRP/Unlit")) + ValidateMaterial`
with borrowed `LaneLine_*` texture.

**Pros:**
- Proven working
- Full control over geometry (any curve shape)
- Cheap (1 draw call per pair, no GameObject overhead)
- Cache + invalidate-on-edit story is simple

**Cons:**
- Ribbon is a 3D mesh hovering 0.01m above the road — ignores road surface
  curvature, gets visible gap on inclines/crowning
- `HDRP/Unlit` shader → no lighting reaction → glows at night
- Texture sampling is direct (not projected) → vanilla decal textures look wrong

**Verdict:** good as fallback / baseline. Not great as final.

---

### 2. Same as #1 but swap to HDRP/Lit

**Status:** untested, but recipe known.

**How:** identical to #1, just swap `Shader.Find("HDRP/Unlit")` →
`Shader.Find("HDRP/Lit")`. Set `_SurfaceType=0`, `_BlendMode=Alpha`,
`_AlphaCutoffEnable=1` (per agent recommendation in
RESEARCH_decal_projector_spike findings). HDMaterial.ValidateMaterial still
required.

**Pros:**
- Fixes night-glow (HDRP/Lit reacts to ambient + directional + shadows)
- 5 minute change, no architectural shift
- Keeps ribbon geometry pipeline as-is

**Cons:**
- Still hovers above road (HDRP/Lit doesn't change geometry)
- Still ignores road surface curvature
- Lit shader expects normal maps, metallic, smoothness — without them looks
  matte-rubber, not painted-line

**Verdict:** cheap quick win on lighting. Doesn't fix floating or surface
adherence.

---

### 3. HDRP DecalProjector — GameObject + DecalProjector component

**Status:** spiked (`research/agent report a5c75c87`), feasibility "Yes-but".

**How:** create `GameObject` per Bezier segment with `DecalProjector` component.
HDRP projects the decal texture onto whatever geometry is below the
projector's bounding box. Material must be HDRP/Decal or
`Shader Graphs/Decal` shader.

**Pros:**
- Native HDRP decal pipeline → full visual quality (surface-adherent, lit,
  no z-fight, follows road curvature)
- Standard Unity API, plenty of community examples
- Solves all three current visual gaps simultaneously

**Cons:**
- **Zero usages in vanilla CS2 decomp** — we'd be first. Untested in this
  exact game build.
- **Performance cap**: HDRP soft-limit ~2048 active projectors. 24 segments
  per Bezier × pairs × in-view intersections = quickly over budget. Need to
  reduce subdivision (1-3 projectors per pair → straight rectangular
  approximations of curve → visible chord error on tight arcs)
- Vanilla `BH/Decals/*` materials probably won't work (authored for vanilla
  ECS decal path, not DecalProjector) — need pure `HDRP/Decal` material
  built at runtime
- HDRP `decalLayerMask` system may need to match Roads=2 (vanilla mask).
  Trial-and-error to find right mask value
- GameObject lifecycle to manage (parent root, destroy on pair removal)
- Each projector does managed per-frame Update — CPU cost per visible
  projector

**Verdict:** highest visual quality if it works; significant integration risk
+ performance management work. ~2-3h spike.

---

### 4. Reverse-engineer vanilla ECS decal pipeline & inject from mod

**Status:** research in flight (agent `aa2c1a33f2e1cc602`, writing to
`research/RESEARCH_vanilla_decal_pipeline.md`).

**How:** vanilla CS2 renders thousands of decals (road wear, edge lines, oil
stains) through `ManagedBatchSystem` + `BatchInstanceSystem` + `DecalProperties`
+ `MeshBatch` + `BatchGroup`. If we can register our Bezier-ribbon mesh into
the same ECS pipeline, we get vanilla-grade rendering "for free" — correct
projection, lighting, performance.

The hard part: our Phase 4 step 2 attempt to create ECS sublane entities
*looked* identical to vanilla edge-line sublanes (same component set:
`Curve, Lane, PrefabRef, Owner, MeshBatch, CullingInfo, SecondaryLane,
LaneGeometry, Simulate, Elevation`) but rendered nothing. The research agent
is investigating exactly what we missed (likely a `BatchGroup` reference, or
a `MeshBatch.m_Group` value that only `ManagedBatchSystem` populates).

**Pros (if feasible):**
- Native pipeline — every quality concern (lighting, surface, performance)
  handled automatically
- Reuses vanilla decal shader, vanilla texture pipeline, vanilla LOD/culling
- Performance: scales to vanilla decal counts (thousands)

**Cons (likely):**
- Vanilla pipeline may genuinely require `PrefabSystem` baking for batch
  registration — that's exactly why our Phase 4 step 2 failed
- Even if feasible, may need Reflection or internal APIs that change between
  patches

**Verdict:** TBD pending research agent report. If yes, this is the best path;
if no, it explains forever why Phase 4 step 2 didn't work.

---

### 5. Mesh sampling against terrain/road height (hybrid)

**Status:** conceptual.

**How:** in `BuildRibbonMesh`, instead of fixed y-coordinate per vertex,
**sample the terrain height** (or, better, the road mesh height) at each
vertex's XZ. The ribbon physically follows the surface contour.

**Pros:**
- Solves floating/incline mismatch with current approach
- No new shader/material work — keeps #1 or #2 code path
- Each vertex is independently grounded

**Cons:**
- Terrain height ≠ road height (road sits on top of terrain via segment
  elevation). Need access to road mesh sampling, not raw terrain.
  `TerrainSystem.heightScaleOffset` gives global, not local-road, height
- Road crowning (~2-5cm bulge to centerline) won't be captured by terrain
  height — need actual road mesh queries
- For long arcs, may need denser tessellation to follow surface accurately
- Doesn't fix lighting / texture issues — those are shader concerns

**Verdict:** partial fix. Useful if combined with HDRP/Lit (#2). Doesn't help
with #1 + Unlit's flat lighting.

---

### 6. Custom shader from runtime AssetBundle

**Status:** conceptual, ~days of work.

**How:** author an HDRP shader in a separate Unity project (matching CS2's
HDRP version), bake into AssetBundle, ship with mod, load at runtime, use
that shader for custom marking material.

**Pros:**
- Full control over visual style — true paint look, custom alpha/blending,
  weathering, etc.
- Bypasses every "vanilla shader requires unknown uniform" issue
- Path IMT used in CS1 and known to scale

**Cons:**
- Requires Unity Editor with matching HDRP version installed (CS2 is HDRP
  14.x on Unity 2022.3.x — needs exact match)
- AssetBundle deployment + shader-graph creation = real engineering effort
- Doesn't automatically solve road-surface-adherence (still need DecalProjector
  or geometry sampling)
- One more piece of mod infrastructure to maintain across CS2 patches

**Verdict:** future tier-2 polish. Not for this sprint.

---

### 7. HDRP DecalSystem (static API alternative to DecalProjector)

**Status:** to investigate via WebSearch / Context7.

**How:** Unity HDRP exposes `DecalSystem.instance` for low-level decal
submissions without MonoBehaviour overhead. If accessible, this skips the
GameObject-per-decal cost while keeping decal quality.

**Pros:**
- Same quality as DecalProjector, fewer managed allocations
- Direct API — no GameObject lifecycle management

**Cons:**
- Internal API in some HDRP versions — may require Reflection to reach from
  mod code
- Less documented than DecalProjector
- Unknown if accessible in CS2's HDRP version

**Verdict:** worth a 30-min investigation. If accessible, beats #3 on
performance.

---

### 8. Defer entire problem to vanilla via prefab cloning + dynamic SecondaryNetLane hosting

**Status:** conceptual, related to Phase 1/2.

**How:** every "user pair" becomes an extension to the existing edge-line
`SecondaryNetLane` buffer on host city lanes. Instead of rendering ourselves,
we extend vanilla's `m_LeftLanes` / `m_RightLanes` at runtime to include
"virtual hosts" representing our pair endpoints, then nudge vanilla through
`UpdatePrefab` + `Updated` cascade.

**Pros:**
- Pure vanilla rendering path
- Zero shader / material work

**Cons:**
- Doesn't fit semantically — `SecondaryNetLane` hosts are per-physical-lane
  on edge geometry, not per-intersection point-pair
- Vanilla doesn't have a "draw line between two arbitrary points on a node"
  concept; this is what we'd have to invent
- Likely requires global modifications to PrefabSystem each time user
  toggles a pair — performance disaster
- Lose all customization power (only what vanilla supports)

**Verdict:** dead-end conceptually. Won't fit the goal.

---

## Comparison matrix

| Approach | Floats | Lit | Texture | Perf | Risk | Effort |
|---|---|---|---|---|---|---|
| 1 Ribbon + Unlit (current) | ❌ | ❌ | ⚠ | ✅ | low | done |
| 2 Ribbon + Lit | ❌ | ✅ | ⚠ | ✅ | low | 30min |
| 3 DecalProjector | ✅ | ✅ | ✅ | ⚠ | medium | 2-3h |
| 4 Vanilla ECS pipeline | ✅ | ✅ | ✅ | ✅ | high | unknown |
| 5 Mesh terrain sampling | ⚠ | — | — | ✅ | low | 1h |
| 6 Custom AssetBundle shader | ⚠ | ✅ | ✅ | ✅ | high | days |
| 7 HDRP DecalSystem | ✅ | ✅ | ✅ | ✅ | medium | 30min spike |
| 8 Vanilla SecondaryNetLane extension | ⚠ | ✅ | ✅ | ✅ | dead-end | — |

Legend: ✅ good, ⚠ partial, ❌ bad / not addressed

---

## Open WebSearch / Context7 queries

To be filled in as research progresses:

1. HDRP DecalProjector — confirmed working in HDRP 14.x with custom material
   from mod code (not asset)? See section: WEB findings below.
2. HDRP DecalSystem static API — accessible in 14.x? Reflection-only?
3. Other CS2 mods that ship custom geometry — anyone done DecalProjector?
4. HDRP Polygon Offset (`_OffsetFactor` / `_OffsetUnits`) — does it work on
   `HDRP/Lit` materials to avoid z-fight without lifting geometry?
5. HDRP `_DecalLayerMask` value to match CS2 road's "Roads = 2" receive bit.

---

## WEB findings

### Q1: DecalProjector with runtime-built material — ⚠ KNOWN BROKEN

**Smoking-gun bug**: Unity Issue Tracker confirms that DecalProjector with a
runtime-built `HDRP/Decal` material **does not render in shipped (non-editor)
builds** until the material has been "touched" in the inspector. Source:
[Unity Issue Tracker — Decal Projector does not work with HDRP/Decal Materials
that were not edited previously](https://issuetracker.unity3d.com/issues/hdrp-decal-projector-does-not-work-with-hdrp-slash-decal-materials-that-were-not-edited-previously).
Marked "Fixed in 7.2.0" but commenter in Sep 2023 reported still broken in
12.1.7. CS2 ships HDRP 14.x so we're past the official fix but **regression
exists**.

Related: [Unity Issue Tracker — Decal Projector is not instantiating its own
material during runtime](https://issuetracker.unity3d.com/issues/hdrp-decal-projector-is-not-instantiating-its-own-material-during-runtime)
— `DecalProjector.material` setter doesn't auto-instance unlike Renderer API.

Also: [HDMaterial.ValidateMaterial still broken for Shader Graphs
[HDRP 14.0.6]](https://discussions.unity.com/t/hdmaterial-validatematerial-still-broken-for-shader-graphs-hdrp-14-0-6/908101)
— even with ValidateMaterial, local Shader Graph keywords stayed broken in
2022.2.5f1.

**Verdict**: DecalProjector path has fundamental Unity-side bugs we likely
can't work around from mod code. **High risk of silent zero pixels** with no
fix mechanism. Path 3 is significantly more dangerous than agent spike
indicated.

### Q2: HDRP DecalSystem (static API) — public singleton exists, low docs

`DecalSystem.instance` singleton is public per
[Unity Discussions — How to access HDRP Decal Projector with script](https://discussions.unity.com/t/how-to-access-hdrp-decal-projector-with-script-and-more/865735)
and visible in [FPSSample HDRP DecalSystem.cs source](https://github.com/Unity-Technologies/FPSSample/blob/master/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Decal/DecalSystem.cs).
Has properties `DrawDistance`, `perChannelMask` but documented API surface
for direct mesh submission is thin. **Likely same underlying bug as Q1** since
it's the same pipeline — just a lower-level entry point.

### Q3: CS2 mod precedents — 🎯 BREAKTHROUGH

**[ExtraAssetImporter mod by AlphaGaming7780](https://github.com/AlphaGaming7780/ExtraAssetsImporter)**
exists and DOES register custom decals into CS2's vanilla rendering pipeline.
Public API:

```csharp
ExtraAssetsImporter.EAI.LoadCustomAssets(pathToModFolder);
// — and on shutdown —
ExtraAssetsImporter.EAI.UnLoadCustomAssets(pathToModFolder);
```

Assets organized in `CustomAssets/CustomDecals/<CategoryName>/` with PNG +
`decal.json` metadata. Built by companion tool
[CS2DecalBuilder](https://github.com/whitevamp/CS2DecalBuilder).

[Wiki](https://github.com/AlphaGaming7780/ExtraAssetsImporter/wiki/Asset-Mod)
confirms: this is the **community-blessed extension point** for adding decals
that render through vanilla CS2 systems. Several mods use it
([RealVision Decals](https://github.com/MiguelRita/RealVision-Decals),
[CS2-AssetPacksManager](https://github.com/CitiesSkylinesModding/CS2-AssetPacksManager)).

**Importance for us**: confirms that **vanilla CS2 decal pipeline IS reachable
from mod code** through a community-built bridge. Our Phase 4 step 2 ECS
sublane attempt was missing whatever ExtraAssetImporter does internally —
likely PrefabSystem registration with proper `DecalProperties` + texture
loading + atlas baking.

We can either:
(a) **depend on** ExtraAssetImporter as a soft prerequisite — easy, but adds
    a user-install dependency and limits us to their data format (static
    PNG decals, not procedural geometry)
(b) **reverse-engineer** what ExtraAssetImporter does (its source is on
    GitHub) and inline that logic — keeps us standalone, harder to do but
    cleanest result

### Q4: Polygon offset on HDRP/Lit

WebSearch returned no results. `_OffsetFactor`/`_OffsetUnits` are legacy
fixed-function depth bias parameters; HDRP shaders typically don't expose
them. Not a viable workaround for floating-line problem.

### Q5: Roads decal-layer mask

[Decal Layers in HDRP 14](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Decal.html)
confirms decal-layer mask is **bitmask** controlling which receivers a decal
affects. Receiver also needs `Receive Decals = true` on its material.

CS2 vanilla uses `DecalLayerEnum` value Roads=2 (per agent finding in
`Game.Rendering/DecalLayers.cs:6`). For our decal to project on road, we'd
need `decalLayerMask` to include bit 2, AND vanilla road material must have
`Receive Decals` enabled (likely yes since vanilla road wear decals work).

---

## Updated comparison matrix

| Approach | Floats | Lit | Texture | Perf | Risk | Effort |
|---|---|---|---|---|---|---|
| 1 Ribbon + Unlit (current) | ❌ | ❌ | ⚠ | ✅ | low | done |
| 2 Ribbon + Lit | ❌ | ✅ | ⚠ | ✅ | low | 30min |
| 3 DecalProjector | ✅* | ✅ | ✅ | ⚠ | **high — known broken** | 2-3h |
| 4 Vanilla ECS pipeline (raw) | ✅ | ✅ | ✅ | ✅ | high | unknown |
| 5 Mesh terrain sampling | ⚠ | — | — | ✅ | low | 1h |
| 6 Custom AssetBundle shader | ⚠ | ✅ | ✅ | ✅ | high | days |
| 7 HDRP DecalSystem | ✅* | ✅ | ✅ | ✅ | **high — same bug** | 30min |
| 8 SecondaryNetLane extension | — | — | — | — | dead-end | — |
| **9 ExtraAssetImporter dependency** | ✅ | ✅ | ✅ | ✅ | low | 2h |
| **10 Reverse-engineer EAI** | ✅ | ✅ | ✅ | ✅ | medium | 1-2d |

*marked broken by Unity's own issue tracker — Q1 finding

---

## ExtraAssetImporter — internal recipe (decoded)

Source files in [github.com/AlphaGaming7780/ExtraAssetsImporter/tree/main/MOD/AssetImporter/Importers](https://github.com/AlphaGaming7780/ExtraAssetsImporter/tree/main/MOD/AssetImporter/Importers):

### NetLanesDecalImporter pattern (most directly relevant to us)

`NetLanesDecalImporter.cs` is **exactly** the asset category we'd target —
decals along net lanes. Its `Import` recipe:

```csharp
// 1. Create a NetLaneGeometryPrefab (same prefab type as vanilla edge-line!)
NetLaneGeometryPrefab netLanesPrefab = ScriptableObject.CreateInstance<NetLaneGeometryPrefab>();

// 2. Apply per-asset metadata from JSON (optional)
if (data.PrefabJson != null)
    data.PrefabJson.Make<NetLanePrefabJson>().Process(netLanesPrefab);

// 3. Build a RenderPrefab if not externally supplied:
//    a. Create a SurfaceAsset using DefaultMaterialName = "CurvedDecal"
SurfaceAsset surfaceAsset = DecalsImporterNew.CreateSurface(data, "CurvedDecal");
//    b. Generate a box mesh sized by JSON's colossal_MeshSize vector
Mesh[] meshes = DecalsImporterNew.CreateMeshes(surfaceAsset);
//    c. Wrap into a RenderPrefab via ImportersUtils.CreateRenderPrefab,
//       running SetupDecalRenderPrefab as a configuration callback
renderPrefab = ImportersUtils.CreateRenderPrefab(data, surfaceAsset, meshes,
    DecalsImporterNew.SetupDecalRenderPrefab);

// 4. Attach to net-lane prefab via standard NetLaneMeshInfo
netLanesPrefab.AddNetLaneMeshInfo(renderPrefab);

// (PrefabBase return — PrefabImporterBase parent then registers via PrefabSystem.AddPrefab)
```

### DecalsImporterNew.SetupDecalRenderPrefab — the magic configuration

- Reads `colossal_TextureArea` vector from surface
- **Adds `DecalProperties` component** with the texture area + a decal layer mask
- Sets render priority from `_DrawOrder` shader property

### DecalsImporterNew.CreateSurface — material setup

- Default material name `"DefaultDecal"` for static-object decals,
  `"CurvedDecal"` for net-lane decals (that's the key insight — these are
  the actual material types vanilla CS2 uses for road markings!)
- Sets `colossal_DecalLayerMask` (this controls which receivers paint)
- Sets `colossal_TextureArea` (this is the UV rectangle in the decal atlas)

### DecalsImporterNew.CreateMeshes — geometry

- Async box-mesh creation on main thread via
  `CreateBoxMeshAsyncOnMainThread()`
- Size driven by `colossal_MeshSize` vector property in JSON
- **Returns box mesh** — the decal pipeline projects the texture into this
  3D box volume, which is what we'd want for our Bezier-ribbon segments

---

## What this means for us

**The vanilla CS2 decal pipeline IS reachable from mod code**, via a path
that's exercised by a community-maintained mod (EAI) and several derived
mods. The pattern is:

1. `ScriptableObject.CreateInstance<NetLaneGeometryPrefab>()`
2. Build `SurfaceAsset` with the right Colossal shader properties
   (`colossal_DecalLayerMask`, `colossal_TextureArea`, `colossal_MeshSize`)
3. Build `RenderPrefab` with our texture + box mesh
4. Add `DecalProperties` component on the RenderPrefab
5. Wrap as `NetLaneMeshInfo` on the `NetLaneGeometryPrefab`
6. Call `PrefabSystem.AddPrefab(netLanesPrefab)`

**This explains why our Phase 4 step 2 sublane ECS attempt failed.** We tried
to instantiate sublane ENTITIES directly with `CreateEntity(archetype)`.
What we actually needed was to instantiate **PrefabBase descendants** via
`ScriptableObject.CreateInstance` + `PrefabSystem.AddPrefab` so the bake
pipeline could install all the SurfaceAsset / DecalProperties / batch-group
metadata we couldn't replicate by hand.

The good news: we already do something very similar in Phase 1!
`EdgeLineCloneSystem.cs` uses `PrefabSystem.DuplicatePrefab` to clone the
vanilla EU/NA Highway Edge Line into our own NetLanePrefab. That clone
**renders correctly** on city roads via the vanilla pipeline. We just
haven't applied the same trick for our pair-marking case because we were
trying to render between two arbitrary endpoints on a node instead of
along an edge.

**The gap is:** vanilla NetLane decals are anchored to edges, not to
intersection-node arc segments. We'd need to either:
- (A) trick vanilla into treating a node-arc as if it were an edge segment
  (probably impossible — Edge ECS components are core),
- (B) build a chain of small "fake net lane" segments along the Bezier,
  each with its own Edge-like ancestor — complex, would create a lot of
  pseudo-edges in the world,
- (C) use the same `RenderPrefab` + `DecalProperties` material/mesh recipe
  but feed it to a custom render system instead of vanilla — i.e. take
  the **material setup** from EAI but the **draw call** from our existing
  `MarkingMeshRenderSystem`.

Option (C) is interesting because it might be **the missing piece for our
own DrawMesh path** — we'd build the proper SurfaceAsset + DecalProperties
material setup that vanilla understands, then call DrawMesh on our Bezier
ribbon with that material. The material being correctly constructed by
the same path EAI uses might solve the "borrowed material renders nothing"
problem we hit earlier.

---

## Approach 11 — EAI material recipe + our DrawMesh (NEW, most promising)

**Status:** newly identified.

**How:** Combine our working Bezier-ribbon mesh + render-callback DrawMesh
infrastructure with EAI's material-construction logic. Specifically:
- Build a `SurfaceAsset` with `colossal_DecalLayerMask`, `colossal_TextureArea`,
  `colossal_MeshSize` properties
- Call `surfaceAsset.Load(useVT: false)` to get a proper material
- Use this material for `Graphics.DrawMesh` instead of our half-baked
  HDRP/Unlit one
- Keep ribbon geometry, render-callback, and lifecycle exactly as today

**Pros:**
- Reuses everything that already works (ribbon, render system, lifecycle)
- Material is constructed by the same logic vanilla uses for decals →
  shader keywords/passes/queue properly populated for vanilla pipeline
- No GameObject overhead, no DecalProjector bugs

**Cons:**
- Requires reading EAI's `ImportersUtils.cs`,
  `DecalsImporterNew.CreateSurface` source to extract the exact API calls
- SurfaceAsset construction might require PrefabSystem registration that
  fires asset-bake which fires NetInitializeSystem — i.e. heavyweight one
  time at startup, not per draw
- May still need to project onto road via shader, not just sample as
  texture — vanilla decal materials probably read uniforms (`colossal_MeshSize`)
  that aren't set unless we set them per draw via MaterialPropertyBlock

**Verdict:** **highest-promise new path.** Combines lessons from all prior
work. Needs ~1-2h reading EAI source + 1h implementing.

---

## Approach 9 — ExtraAssetImporter dependency (NEW)

**Status:** discovered via WebSearch. Documented API exists.

**How:** Add EAI as soft dependency. Generate decal assets at build time (or
ship pre-generated). Tell EAI to load our `CustomAssets/CustomDecals/`
folder on mod startup. Vanilla CS2 then renders these decals natively.

**Pros:**
- Native vanilla rendering — no shader/material/layer work
- Decals render correctly on roads (surface-adherent, lit, no z-fight)
- Proven by other mods (RealVision Decals, McDonalds Decals)
- No DecalProjector bugs because we don't touch DecalProjector

**Cons:**
- User must install ExtraAssetImporter mod separately (soft dep)
- Decal assets are **static** PNGs with fixed mesh dimensions — we'd need
  pre-generated tiles per style (solid 1m, solid 2m, dashed, etc.) and
  place multiple decals along Bezier curves
- Geometry is fixed rectangles like DecalProjector — Bezier curves
  approximated by N rectangular decals
- Tight integration: our mod becomes a customer of EAI not a peer

**Verdict:** **Most promising new path.** Should be investigated **before**
committing to DecalProjector or custom shaders. The community-blessed channel
exists; we'd be fools not to look closely.

---

## Approach 10 — Reverse-engineer EAI and inline the logic (NEW)

**Status:** depends on Approach 9 investigation.

**How:** Read EAI source on GitHub, extract the decal-registration logic
(probably ~500 lines of PrefabSystem + DecalProperties construction + asset
loading), inline into our mod. Skip the user-install dependency.

**Pros:**
- All Approach 9 benefits without user-install dep
- We learn the exact "magic" that registers things into vanilla decal
  pipeline — useful knowledge regardless of where it ends up

**Cons:**
- ~1-2 days of careful work understanding+adapting EAI internals
- Must track EAI's compatibility with CS2 patches ourselves
- Code-duplication ethics (if EAI is MIT/Apache — fine; if other — ask)

**Verdict:** secondary option after Approach 9 spike.

---

## Cross-references

- `research/RESEARCH_vanilla_decal_pipeline.md` — agent report on ECS pipeline
  (in progress, agentId aa2c1a33f2e1cc602)
- `SESSION_2026-05-17.md` — current session snapshot, includes
  ValidateMaterial discovery
- `IMPLEMENTATION_PLAN.md` — top-level pivot section explains why ECS sublane
  approach was abandoned
- `_refs/IMT/` — CS1 NodeMarkup vendored for reference (98 files)

---

## Final recommendation

Two paths are now identified as worth real investment, ranked:

### 🥇 Primary: Approach 11 — EAI material recipe + our DrawMesh

Use EAI's source as documentation for constructing a vanilla-compatible
decal material (`SurfaceAsset` with `colossal_DecalLayerMask`,
`colossal_TextureArea`, `colossal_MeshSize` properties; `surfaceAsset.Load`).
Plug that material into our existing `MarkingMeshRenderSystem.Render`.
Geometry stays as our Bezier ribbon.

Why this is best:
- Reuses everything that works (ribbon, render path, lifecycle, persistence)
- Solves all three current cosmetic gaps (lighting, surface adhesion via
  proper decal shader, correct texture)
- No GameObject overhead, no DecalProjector bugs
- ~2-3h work (reading EAI source + adapting ~30 lines of material setup)
- Even if it doesn't solve surface-adherence (because our mesh stays a flat
  ribbon, not a real volumetric decal), it should at least solve lighting
  and texture issues — and floating gap can be addressed via per-vertex
  height sampling (Approach 5)

### 🥈 Secondary: Approach 4 — full vanilla ECS pipeline (pending agent report)

Background agent is currently reading the full ECS decal pipeline. If they
find that we can register our mesh via PrefabSystem like EAI does but
keeping our Bezier shape, that's even better than 11.

### Explicit DON'T paths

- **DecalProjector** — proven broken in Unity issue tracker; runtime-built
  materials don't render in shipped builds without inspector touch
- **DecalSystem static API** — same underlying pipeline as DecalProjector,
  same bugs likely
- **Custom shader AssetBundle (Approach 6)** — too heavy for current sprint,
  requires Unity Editor + HDRP project matching CS2
- **Vanilla SecondaryNetLane extension (Approach 8)** — semantically wrong
  fit, dead-end

### Cheap quick-win available now

If we want a 5-minute polish before the real fix:
- **Approach 2** (HDRP/Lit swap) — fixes night-glow, low risk

This is a worthwhile interim ship if user wants "good enough" today and
defer full polish to next session.

---

## Sources

- [Unity Issue Tracker: HDRP Decal Projector not instantiating runtime material](https://issuetracker.unity3d.com/issues/hdrp-decal-projector-is-not-instantiating-its-own-material-during-runtime)
- [Unity Issue Tracker: HDRP Decal Projector does not work with materials not previously edited](https://issuetracker.unity3d.com/issues/hdrp-decal-projector-does-not-work-with-hdrp-slash-decal-materials-that-were-not-edited-previously)
- [HDMaterial.ValidateMaterial still broken for Shader Graphs (HDRP 14.0.6)](https://discussions.unity.com/t/hdmaterial-validatematerial-still-broken-for-shader-graphs-hdrp-14-0-6/908101)
- [Modify materials at runtime — HDRP 14.0.12 docs](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Material-API.html)
- [Decal — HDRP 14.0.12 docs](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Decal.html)
- [HDRP DecalSystem source (FPSSample)](https://github.com/Unity-Technologies/FPSSample/blob/master/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Decal/DecalSystem.cs)
- [ExtraAssetImporter mod source](https://github.com/AlphaGaming7780/ExtraAssetsImporter)
- [ExtraAssetImporter wiki — Asset Mod](https://github.com/AlphaGaming7780/ExtraAssetsImporter/wiki/Asset-Mod)
- [CS2DecalBuilder tool](https://github.com/whitevamp/CS2DecalBuilder)
- [Customizing HDRP materials with Shader Graph — HDRP 14.0](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Customizing-HDRP-materials-with-Shader-Graph.html)

---

## Cross-references

- `research/RESEARCH_vanilla_decal_pipeline.md` — agent report on ECS pipeline
  (in progress)
- `SESSION_2026-05-17.md` — current session snapshot, includes
  ValidateMaterial discovery
- `IMPLEMENTATION_PLAN.md` — top-level pivot section explains why ECS sublane
  approach was abandoned

---

## Final recommendation

_To be written after all sections populated._
