# Decal Breakthrough Hunt — Why MeshRenderer + DefaultDecalShader still gives 0 pixels

> Follow-up to `RESEARCH_decal_draw_recipe.md` and `RESEARCH_vanilla_decal_pipeline.md`.
> Goal: assuming the current GameObject+MeshFilter+MeshRenderer pipeline ALSO
> renders 0 pixels (very likely), enumerate the remaining unknowns, fixes, and
> realistic alternatives — so we have a ready action list when the user is back.

Source files in scope:

- Current renderer: `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/src/TownRoadLane/MarkingMeshRenderSystem.cs`
- Vanilla editor renderer (the template): `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering.Debug/RenderPrefabRenderer.cs`
- Vanilla BRG decal path: `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/ManagedBatchSystem.cs` (lines 800-1435)
- Vanilla BRG draw-command filter: `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/BatchManagerSystem.cs` (lines 595-620, 820-855)

The breakthrough insight from this round of research: **two new mods (EAI and
its DecalsImporter chain) confirm that no public CS2 mod creates a runtime
DefaultDecalShader material via Shader.Find**. Every working decal mod —
EAI, RealVision, Grunge — funnels through `Colossal.IO.AssetDatabase.SurfaceAsset`
+ `PrefabSystem.AddPrefab(NetLaneGeometryPrefab + RenderPrefab + DecalProperties)`,
i.e. the vanilla BRG path. That's a strong signal our `Shader.Find` route is
fighting the engine, not extending it.

---

## §1 Layer hypothesis

### What I found

Two related layer concepts. `MarkingMeshRenderSystem` currently leaves the
GameObject Unity layer at 0 (Default) and does not touch `MeshRenderer.renderingLayerMask`.

**Unity `gameObject.layer`** — `ManagedBatchSystem.cs:424-430` caches seven
custom Unity layers (`Tunnel, Moving, Pipeline, SubPipeline, Waterway, Outline,
Marker`). They map to `MeshLayer` via the switch at lines 1272-1306. For
plain decals (`MeshLayer.None` / unset), the layer integer `num` initialises to
`0` (line 806) and stays 0 — the Unity Default layer. So **Default (0) is
correct for our case**. Not the bug.

`RenderPrefabRenderer.cs:117` creates its preview GameObject with no explicit
`.layer` either — defaults to whatever the parent has. Editor preview renders
fine that way, so confirmed: layer 0 is not the blocker for HDRP decal
acceptance.

**HDRP `renderingLayerMask`** — this is the more interesting one, and we ARE
wrong here. Vanilla BRG draw commands set `renderingLayerMask = uint.MaxValue`
(`BatchManagerSystem.cs:603, 831`). Newly-created `MeshRenderer` defaults its
`renderingLayerMask` to 1 (just bit 0). HDRP's DBuffer decal pass and its
G-buffer write back step both filter on `renderingLayerMask` — if the renderer's
mask does not intersect the receiver's mask, decal output is dropped. The
default-1 vs vanilla-uint.MaxValue mismatch is plausibly half the reason no
pixels appear.

Note Unity HDRP docs explicitly warn (from web search):
> "Decal Meshes do not support Decal Layers. Use the Rendering Layer Mask
> drop-down to select which Decal Layers affect this Mesh Renderer or Terrain."

So **Decal *Meshes* (i.e. `MeshRenderer` with a decal material — our case)
use `renderingLayerMask` not the `colossal_DecalLayerMask` material constant**.
The two are different concepts in HDRP — material decal layer is for
`DecalProjector`, `renderingLayerMask` is for renderers and Light culling.
The 8-bit decal-layer enum (Roads=2 etc.) is HDRP's `RenderingLayerMask` —
**they share the bits**. So `renderingLayerMask = (uint)DecalLayers.Roads`
would actually be coherent with the rest of the codebase.

### Actionable

1. Set `renderer.renderingLayerMask = uint.MaxValue;` right after MeshRenderer
   creation in `PairRender.Create()` (~line 605). One-line change.
   If that opens the floodgates, narrow it to `(uint)DecalLayers.Roads` (=2) or
   `(uint)(DecalLayers.Terrain | DecalLayers.Roads)` (=3) to limit projection
   to road/terrain receivers.
2. Add a diagnostic log right after creation:
   `log.Info($"Renderer layer={go.layer} renderingLayerMask=0x{renderer.renderingLayerMask:X}")`.
3. Leave `gameObject.layer = 0` — vanilla doesn't change it for decals either.

---

## §2 Material properties checklist — what vanilla actually sets

### What vanilla sets on a decal material — exhaustive list

Cross-referenced `ManagedBatchSystem.cs:CreateMaterial` (1351-1436) +
`CreateBatch` (791-1327) + `RenderPrefabRenderer.SetDecalProperties` (401-414).
Order = order-of-execution in vanilla. Whether it goes on the **MAT**erial or
the **MPB** is per the column.

| Step | Property / Keyword | Where | Source value | Notes |
|---|---|---|---|---|
| 1 | `template = surfaceAsset.GetTemplateMaterial()` | MAT clone source | template material from surface asset | **Critical**. The template carries the correct `_Affect*` toggles, `_StencilRef`, `_StencilWriteMask`, `_DecalStencilWriteMask`, surface type, blend states, all stencil refs. `new Material(Shader.Find(...))` skips all of these. |
| 2 | textures copied from `materialKey.textures` | MAT | `_BaseColorMap`, `_NormalMap`, `_MaskMap` typically | vanilla loops over `m_CachedTextures` — for VT-disabled path it uses `material.SetTexture(item.nameID, item.texture)` (line 1402-1403). |
| 3 | `keywords` from surface asset | MAT | shader-asset-defined | vanilla calls `material.EnableKeyword(keyword)` per source surface keyword (lines 1388-1391) |
| 4 | `HDMaterial.ValidateMaterial(material)` | call | — | Line 1392. Translates the `_Affect*` toggles + `_StencilRef` into shader keywords + pass-enable state. |
| 5 | `material.SetFloat(m_DecalLayerMask, math.asfloat(materialKey.decalLayerMask))` | MAT | int-as-float bit cast of `(int)DecalLayers.Roads` | Line 1408. ALSO done — `RenderPrefabRenderer.cs:409` puts it on MPB. Both paths work; vanilla BRG prefers material. |
| 6 | `material.renderQueue = materialKey.renderQueue` | MAT | `template.shader.renderQueue + decalProperties.m_RendererPriority` | Line 1410-1413. For `BH/Decals/DefaultDecalShader` template renderQueue is 2000; priority typically 0 → queue 2000. |
| 7 | extra material-key keywords | MAT | accumulated during CreateBatch | Lines 1414-1424. Add/remove based on `m_CachedKeywords`. |
| 8 | VT atlas binding | MAT | from `surfaceAsset.VTAtlassingInfos` | Lines 1425-1434. We disable VT so skip this. |
| 9 | `materialPropertyBlock.SetVector(_TextureArea, float4(min, max))` | MPB | `decalProperties.m_TextureArea` | Line 1022 |
| 10 | `materialPropertyBlock.SetVector(_MeshSize, float7)` | MPB | bounds | Line 1023. `float7` here is `(size.x, size.y, size.z, ?)` — exact layout matches `RenderPrefabRenderer.cs:408` = `(MathUtils.Size(m_Bounds), 0f)`. So `w=0`, NOT `bounds.center.y` as our v1 research speculated. Our code sets w to `bounds.center.y` (line 182) — **probably wrong, should be 0f**. |
| 11 | `materialPropertyBlock.SetFloat(_LodDistanceFactor, RenderingUtils.CalculateDistanceFactor(lod))` | MPB | lod-dependent | Line 1024. For LOD 0 = 1f. |
| 12 | `materialPropertyBlock.SetFloat(_SmoothingDistance, meshData.m_SmoothingDistance)` | MPB | mesh prefab field | Line 1029, ONLY when `MeshType.Lane`. Missing on our MPB. |
| 13 | `materialKey.renderQueue` is recomputed (#6 above) | — | — | — |

### What's NOT on vanilla decal materials

For confirmation:

- No `_AffectAlbedo`, `_AffectNormal`, `_AffectMetal`, `_AffectAO`,
  `_AffectSmoothness`, `_AffectEmission` SetFloat calls. These properties
  exist on HDRP's `HDRP/Decal` shader (a Unity template) but on
  `BH/Decals/DefaultDecalShader` they are baked-in via the template material
  and not toggled at C# level.
- No `_StencilRef`/`_StencilRefDepth`/`_StencilWriteMask`/`_DecalStencilRef`
  SetInt calls. Same reason — baked into template.
- No `SetShaderPassEnabled("DBufferProjector_AO", ...)` calls. Same.
- No `mat.color = Color.white` setter — `_BaseColor` is left at template default.

### Why our `BuildDecalMaterial()` is suspect

Our `MarkingMeshRenderSystem.cs:232-298` does
`var mat = new Material(Shader.Find("BH/Decals/DefaultDecalShader"))`. **This
is NOT equivalent to vanilla's `new Material(template)`**. The template material
ships pre-configured with stencil refs, `_Affect*` flags, surface type 1
(decal), receive-decals state, all baked. Bare-shader-instance has none of
that. ValidateMaterial fills in some but not all — its job is to translate
property values into keywords, not to invent default values.

### Actionable

1. **Stop creating a fresh material from the shader.** Instead clone an
   existing vanilla decal material — borrow it the same way we borrow the
   texture today. Either:
   - `_edgeLineSys.ClonePrefabEU.m_Meshes[0].m_Mesh.ObtainMaterial(0, useVT:false)`
     then `new Material(vanillaMat) { hideFlags = HDDontSave }` — this carries
     stencil refs, surface type, all flags. Then call our existing
     ValidateMaterial. Set texture-area on MPB. Done.
   - Alternative: `m_BatchManagerSystem.GetUpdatedManagedBatches()` and pick
     any decal batch's material — but the cloned-prefab path is simpler.
2. **Fix `colossal_MeshSize.w`**: currently `bounds.center.y` (line 182),
   should be `0f` to match `RenderPrefabRenderer.cs:408`.
3. **Add `colossal_SmoothingDistance = 0f` to MPB** — vanilla sets it for
   `MeshType.Lane`; without it the shader may treat the value as garbage.
   `MaterialProperty.SmoothingDistance` → `colossal_SmoothingDistance`
   (verify the exact shader uniform name via
   `decomp/Game/Game.Rendering/MaterialProperty.cs`).
4. Keep `colossal_DecalLayerMask` on **material**, and ALSO add it to MPB
   (belt and braces — `RenderPrefabRenderer` puts it on MPB; cost of
   redundancy is zero).

---

## §3 ValidateMaterial alternatives

### What's in HDMaterial

`UnityEngine.Rendering.HighDefinition.HDMaterial` (HDRP package, closed in
the runtime DLL but documented). Per the Unity 14.0 docs the public surface
includes:

- `ValidateMaterial(Material)` — runs all keyword/pass derivation. Generic.
- `SetAlphaClipping`, `SetAlphaCutoff`, `SetSurfaceType`, `SetBlendMode`,
  `SetDoubleSidedNormalMode`, `SetTransparentSortPriority` and other
  per-property setters that AUTOMATICALLY call internal ValidateMaterial.
- **NO** `ValidateDecalMaterial`, `SetDecalLayerMask`, `SetAffectAlbedo`,
  `SetReceiveDecals` — these don't exist as a public API. The decal toggles
  live as plain shader properties (`_AffectAlbedo` etc.) that the user is
  expected to `SetFloat` then call `ValidateMaterial`.

### Vanilla calls it ONCE per material

`ManagedBatchSystem.cs:1392` is the only `HDMaterial.ValidateMaterial` call in
the entire decompiled codebase. So vanilla relies on ValidateMaterial doing the
right thing — given a CORRECT TEMPLATE.

### Critical Unity bug pattern (from web search)

Multiple Unity Discussions threads (`HDMaterial.ValidateMaterial still broken
for Shader Graphs [HDRP 14.0.6]`, `Decal problem in HDRP`, `HDRP decal not
showing in standalone build`) document a known issue: when a decal material
is created at runtime via `Shader.Find()`, "the decal/material won't show up
in-game until the material gets opened in the inspector". The inspector path
runs a richer setup than `ValidateMaterial` does at runtime. The workaround
people use: load the material from an `AssetBundle` (which preserves the
inspector-baked state), not from a runtime `Shader.Find` + new Material.

### Actionable

1. **Skip the question** — by switching to "clone existing vanilla material"
   (§2 actionable 1), ValidateMaterial works correctly because the clone
   source IS a properly inspector-baked material. The whole bug class becomes
   irrelevant.
2. Belt-and-braces — after our existing `HDMaterial.ValidateMaterial(mat)` call,
   also call `mat.SetShaderPassEnabled("DBufferProjector", true)` and
   `mat.SetShaderPassEnabled("DecalProjectorForwardEmissive", true)`. Per
   HDRP source these are real pass names on the decal shader; in case
   ValidateMaterial disabled them, this re-enables.
3. Log `mat.GetShaderPassEnabled("DBufferProjector_AO")` etc. for every
   `DBufferProjector_*` permutation after ValidateMaterial. If any are FALSE,
   that proves ValidateMaterial silently disabled passes we need.

---

## §4 Working mod examples — what production CS2 mods actually do

### Examined

All decal mods that ship custom decals (EAI, RealVision-Decals, GrungeDecals,
the StammenKai McDonalds pack, MiguelRita's pack) follow the SAME pattern:

1. Build a `NetLaneGeometryPrefab` (or `StaticObjectPrefab` for static decals).
2. Inside it put a `RenderPrefab` whose `GeometryAsset` is a procedural box
   mesh sized to the decal volume.
3. Add a `DecalProperties` managed component to that `RenderPrefab` with
   `m_TextureArea`, `m_LayerMask = DecalLayers.Roads`, `m_RendererPriority = 0`.
4. Build a `SurfaceAsset` referencing the **vanilla template material**
   "CurvedDecal" or "DefaultDecal" by NAME (Colossal asset DB looks it up) —
   not by `Shader.Find()`.
5. Register everything via `PrefabSystem.AddPrefab(netLaneGeometryPrefab)`.
6. The vanilla BRG pipeline (`RequiredBatchesSystem` →
   `ManagedBatchSystem.CreateBatch` → `BatchInstanceSystem`) picks it up
   automatically and renders it.

The critical concrete file:
`github.com/AlphaGaming7780/ExtraAssetsImporter/MOD/AssetImporter/Importers/DecalsImporter.cs`

```csharp
public static void SetupDecalRenderPrefab(PrefabImportData data, RenderPrefab renderPrefab, SurfaceAsset surface)
{
    Vector4 TextureArea = surface.vectors.ContainsKey("colossal_TextureArea")
        ? surface.vectors["colossal_TextureArea"]
        : new Vector4(0, 0, 1, 1);
    DecalProperties decalProperties = renderPrefab.AddOrGetComponent<DecalProperties>();
    decalProperties.m_TextureArea = new(new(TextureArea.x, TextureArea.y), new(TextureArea.z, TextureArea.w));
    decalProperties.m_LayerMask = (DecalLayers)surface.floats["colossal_DecalLayerMask"];
    decalProperties.m_RendererPriority = (int)(surface.HasProperty("_DrawOrder") ? surface.floats["_DrawOrder"] : 0);
    decalProperties.m_EnableInfoviewColor = false;
}
```

And `NetLanesDecalImporter.cs:24` — `k_DefaultMaterialName = "CurvedDecal"`.
The string `"CurvedDecal"` is the NAME of an in-game material loaded by
`SurfaceAsset` via the asset DB, NOT a shader name. So even EAI does not call
`Shader.Find` at any point.

### Implication for us

We already do step 1-2-3-5 in `EdgeLineCloneSystem` / `ParkingLineCloneSystem`
— and those WORK (they paint the vanilla atlas onto road surfaces fine). The
only place we deviate is **Phase 4**: instead of registering a third prefab for
our pair markings, we tried to bypass PrefabSystem and render via
`Graphics.DrawMesh` / `MeshRenderer`. That bypass is exactly what no working
mod does.

### Where EAI's mesh comes from

`DecalsImporter.CreateMeshes(surface)` calls
`ImportersUtils.CreateBoxMeshAsyncOnMainThread(MeshSize)`. The mesh is a
static axis-aligned box, ALWAYS the same shape, just resized. The actual
projection-onto-curved-road work happens in the shader (`BH/Decals/CurvedDecalShader`)
which reads `colossal_TextureArea` + `colossal_MeshSize` to figure out how to
deform the projection along the Bezier curve. There is NO per-pair mesh.
The curve adaptation is shader-side.

**Big implication for our Bezier ribbon**: if we go the PrefabSystem route,
we don't actually need per-pair geometry. ONE static box prefab per
direction-type plus per-instance `Curve` data (Bezier endpoints) on the lane
entity is enough — the CurvedDecal shader will bend the projection. This is
how vanilla edge lines work too. Our `BuildRibbonMesh` is overengineering
for the DrawMesh path; for the PrefabSystem path it's unnecessary.

### Actionable

1. **Highest-priority alternative**: build a third prefab clone (in addition
   to EdgeLine + ParkingLine) for "user-pair markings", spawn lane entities
   with `Curve` set from our pair Bezier, let vanilla render. This is the
   same path Phase 4 step 2 (`MarkingPairEmissionSystem`) tried but failed —
   but failure root cause was **never properly investigated**. The
   "PrefabSystem-baked batch registration that hand-created entities don't
   inherit" hypothesis in the file comment may be wrong; if we set the
   correct archetype + PrefabRef + LaneGeometry components, the lane entity
   should pass `RequiredBatchesSystem`'s filter. Re-attempt with eyes-open.
2. **If MeshRenderer path is to be kept**: clone an existing already-validated
   vanilla decal material (per §2) rather than building from scratch.

---

## §5 Pass selection — how HDRP decides which renderer enters the decal pass

### How HDRP picks shaders into passes

Unity HDRP rendering is **pass-tag driven**. A shader pass with
`Tags { "LightMode" = "DBufferProjector" }` is invoked during HDRP's DBuffer
phase (decal G-buffer write); a pass with `Tags { "LightMode" = "Forward" }`
is invoked during forward shading. The SRP iterates over visible renderers,
for each renderer's material's shader looks at the pass tags, and includes
the renderer only in passes whose `LightMode` matches the current SRP phase.

For `BH/Decals/DefaultDecalShader`:

- We KNOW the shader has multiple DBuffer variants — Unity HDRP standard decal
  shader has `DBufferProjector`, `DBufferProjector_M` (metal), `DBufferProjector_S`
  (smoothness), `DBufferProjector_MAO` etc., and `DecalProjectorForwardEmissive`
  for the emissive component. `BH/Decals/DefaultDecalShader` is derived from
  this template, so likely has the same set.

- A pass is **enabled by default** when the shader is loaded. `ValidateMaterial`
  + the keyword `_MATERIAL_AFFECTS_ALBEDO` etc. determine which DBuffer
  variants the runtime selects. If all `_MATERIAL_AFFECTS_*` keywords are OFF,
  HDRP may skip ALL DBuffer passes for that material → 0 pixels.

- `MeshRenderer.SetPropertyBlock` does NOT influence pass selection. Pass
  selection happens at material binding, before MPBs are merged.

### What we KNOW vanilla does that we do too

Our material logs show `_MATERIAL_AFFECTS_ALBEDO`, `_MATERIAL_AFFECTS_NORMAL`,
`_MATERIAL_AFFECTS_MASKMAP` are all enabled after our manual `EnableKeyword`
+ `ValidateMaterial`. So the keywords are right.

### What we DON'T know

- Does `BH/Decals/DefaultDecalShader` use a `[HideInInspector] _DecalStencilWriteMask`
  property defaulting to 0 (i.e. write nothing) unless the inspector / vanilla
  surface asset sets it to a meaningful bit pattern? If yes, our material has
  zero stencil mask → DBuffer pass writes garbage → backwards G-buffer read
  reads garbage → 0 visible pixels.
- What is the `_SurfaceType` value baked on the vanilla CurvedDecal material?
  Possibly 1 (transparent) or 0 (opaque). For decals it should be 0 (opaque,
  written to DBuffer); transparent decals follow a different path.

### How to find out

After material build, log every property in detail:

```csharp
var s = mat.shader;
for (int i = 0; i < s.GetPropertyCount(); i++)
{
    var name = s.GetPropertyName(i);
    var type = s.GetPropertyType(i);
    string val = type switch {
        ShaderPropertyType.Float or ShaderPropertyType.Range => mat.GetFloat(name).ToString(),
        ShaderPropertyType.Int => mat.GetInt(name).ToString(),
        ShaderPropertyType.Color => mat.GetColor(name).ToString(),
        ShaderPropertyType.Vector => mat.GetVector(name).ToString(),
        ShaderPropertyType.Texture => (mat.GetTexture(name)?.name ?? "<null>"),
        _ => "?"
    };
    log.Info($"  prop {name} ({type}) = {val}");
}
for (int i = 0; i < s.passCount; i++)
{
    log.Info($"  pass[{i}] name='{s.GetPassName(i)}' enabled={mat.GetShaderPassEnabled(s.GetPassName(i))}");
}
```

Compare against the same dump for the BORROWED vanilla edge-line material
(`_edgeLineSys.ClonePrefabEU.m_Meshes[0].m_Mesh.ObtainMaterial(0, useVT:false)`).
Any property difference is a smoking gun.

### Actionable

1. Add the property+pass dump to `BuildDecalMaterial()` so next test run gives
   us a definitive diff.
2. Dump the vanilla material side-by-side in `TryBorrowLaneLineTexture` so
   we can compare in the same log file.
3. If the diff shows non-trivial property mismatch (likely), switch material
   build to clone-vanilla approach (per §2 actionable 1).

---

## §6 Plan B — alternatives if the MeshRenderer route is dead

Estimated time and visual cost for each.

### B.1 Replay Phase 4 step 2 — spawn vanilla NetLane sublane entities (~6h)

Re-attempt `MarkingPairEmissionSystem` with the knowledge we now have:

- Use `EdgeLineCloneSystem.ClonePrefabEU` as the prefab, not a new prefab.
- Set Curve, PrefabRef, Owner, Elevation, SecondaryLane components correctly.
- Crucially: ensure the entity goes through the proper `BatchesUpdated`
  / `Updated` flag dance so `RequiredBatchesSystem` picks it up. Previous
  failure may have been because the entity was created without
  `BatchesUpdated` or without surviving the next sync barrier.

**Why this should work**: vanilla `SecondaryLaneSystem` does exactly the same
thing — `m_CommandBuffer.CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype)`
(see `decomp/Game/Game.Net/SecondaryLaneSystem.cs:1384`). If we replicate
its archetype + component set, the lane should render. Previous "fails silently"
suggests we missed one component or one update flag.

**Pros**: zero rendering work — vanilla does everything. Native render perf.
Anti-aliased, depth-faded, properly stencil-clipped at intersections.

**Cons**: re-do failed work. May rediscover the original blocker. Lane geometry
limited to whatever the borrowed prefab can do — edge-line is straight + thin.

**Visual fidelity**: 100% — indistinguishable from vanilla edge lines.

### B.2 Build a third PrefabSystem-baked clone for "user pair lane" (~10h)

The EAI pattern: clone an existing vanilla lane-line marking prefab, give it
a different name + a different Bezier-curve owner relationship, spawn one
ephemeral lane entity per pair. Builds on B.1 but takes the additional step
of registering an ENTIRELY separate prefab (so we can give it a different
texture / different decal layer / different debug name) instead of recycling
the edge-line clone.

**Pros**: cleanest architecture, isolates our markings from edge lines for
future style customization. Per-instance variant via swapping in different
prefabs.

**Cons**: more code, hits `PrefabSystem.UpdatePrefab` async-bake risks (K2)
that EdgeLineCloneSystem already battles.

**Visual fidelity**: 100%.

### B.3 Clone-existing-vanilla-material + MeshRenderer fix (~3h)

Stay on current architecture, fix the material-build to clone a vanilla
material rather than `Shader.Find + new Material`. Combined with §1 renderingLayerMask
fix, §2 `colossal_MeshSize.w = 0`, §5 dump-and-diff, and §3 belt-and-braces
ValidateMaterial extras.

**Pros**: smallest delta from current code. Keeps the per-pair custom Bezier
ribbon (which is nice for sharp turns where a straight box decal would clip).

**Cons**: still fights HDRP at the level no other mod does. May still
silently miss some property. Even if it renders, perf may be worse than BRG
path (uncached draw calls, no merging).

**Visual fidelity**: 100% if it works. Currently 0%.

### B.4 HDRP DecalProjector MonoBehaviour per pair (~4h)

One `UnityEngine.Rendering.HighDefinition.DecalProjector` per pair, dimensioned
to the Bezier ribbon AABB. Material can still be `BH/Decals/DefaultDecalShader`.

**Pros**: guaranteed HDRP-correct path. Decal Projector IS the supported
runtime decal entry point.

**Cons**: rectangular projection only — S-curved Bezier projects a straight
rectangle through the curve, which clips ugly at sharp angles. Acceptable
for short straight pair markings, ugly for tight turns. Multiple projectors
per long curve (split the Bezier into N short rectangles each with its own
projector) — costlier but visually correct.

**Visual fidelity**: 60% (straight segments fine; curves look segmented).

### B.5 Custom shader via AssetBundle (~8h, requires Unity Editor)

Build a Unity HDRP project, create a Decal-Master-Stack shader graph variant
that handles Bezier curves properly, build to AssetBundle, ship it with the
mod, load at runtime.

**Pros**: full control. Industry-standard pattern for runtime materials.
Bypasses the entire "ValidateMaterial inspector bug" class.

**Cons**: setup overhead — Unity Editor with HDRP 14.x matching the game's
HDRP version, build script, asset bundle versioning. Tight coupling to game's
HDRP version (breaks if game upgrades HDRP).

**Visual fidelity**: 100% (custom-tuned to our exact use case).

### B.6 Hard fallback — accept current HDRP/Unlit + cosmetic compromise (~0h)

Mark Path B as v3 work and ship v2 with the HDRP/Unlit white-bar render. The
unlit fallback is currently the only path that visibly draws.

**What we lose vs proper decal**:

- No projection onto road surface — appears as a floating thin 3D bar at
  fixed offset, glitches into terrain on slopes / crowned roads.
- No depth-fade — straight cuts into nearby vehicles / props.
- No emissive paint look — solid color, no per-pixel anti-aliasing along
  edges from decal supersampling.
- No interaction with snow/wet/wear infoview overlays.

**What we keep**: still tracks the Bezier, still themable color, still draws.

**Visual fidelity**: 40%.

### Priority order

1. **B.3** first (3h, no architecture change) — if dump-and-diff reveals a
   simple property mismatch, fixes everything. Cheap to attempt.
2. **B.1** next (6h) — if B.3 doesn't unblock, the BRG path is the proven
   route every other mod uses. Re-attempt with the renewed pipeline understanding.
3. **B.4** if B.1 also fails (4h) — DecalProjector is the safe "this WILL
   work" fallback even though it sacrifices curve quality.
4. **B.2** only if we ever want per-style markings — not blocker work.
5. **B.5** as a v3 polish milestone.
6. **B.6** as a fallback for shipping v2 if everything fails.

---

## Appendix: file:line index — net-new findings since previous research

| Subject | File | Line |
|---|---|---|
| BRG draw command `renderingLayerMask = uint.MaxValue` | `decomp/Game/Game.Rendering/BatchManagerSystem.cs` | 603, 831 |
| Decal layer mask on MAT (BRG path) | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 1408 |
| Decal layer mask on MPB (Editor path) | `decomp/Game/Game.Rendering.Debug/RenderPrefabRenderer.cs` | 409 |
| Vanilla never sets `_AffectAlbedo` etc. | (negative finding) | grep returns nothing |
| Vanilla `colossal_MeshSize.w = 0f` | `decomp/Game/Game.Rendering.Debug/RenderPrefabRenderer.cs` | 408 |
| ManagedBatchSystem `HDMaterial.ValidateMaterial` call | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 1392 |
| Vanilla material is `new Material(template)`, not `new Material(shader)` | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 1356 |
| Vanilla layer 0 default for plain decals (line 806 sets `num=0`, no MeshLayer case) | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 806, 1272-1306 |
| EAI DecalsImporter recipe | `github.com/AlphaGaming7780/ExtraAssetsImporter/MOD/AssetImporter/Importers/DecalsImporter.cs` | SetupDecalRenderPrefab |
| EAI NetLane decal — uses CurvedDecal template by name | `github.com/AlphaGaming7780/ExtraAssetsImporter/MOD/AssetImporter/Importers/NetLanesDecalImporter.cs` | 17 |
| MeshType.Lane gets `colossal_SmoothingDistance` MPB | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 1027-1030 |

## TL;DR — what to do when the user is back, in order

1. Run the **current** code, capture logs. Confirm 0 pixels (or note any
   change).
2. Apply two trivial fixes — §1 actionable 1 (`renderingLayerMask = uint.MaxValue`)
   and §2 actionable 2 (`colossal_MeshSize.w = 0f`). Test.
3. If still 0 pixels: apply §5 actionable 1+2 (dump material props + passes,
   both ours and vanilla's). Compare in log.
4. If diff is non-trivial: switch material build to **clone vanilla material**
   (§2 actionable 1) — replaces ~50 lines in `BuildDecalMaterial()` with
   ~5 lines. Test.
5. If still 0 pixels — abandon MeshRenderer path. Pivot to **B.1** (revive
   `MarkingPairEmissionSystem` with renewed pipeline understanding).
6. If B.1 hits a wall — pivot to **B.4** (DecalProjector — known good).
7. Ship v2 with whatever works; mark perfect-Bezier-decal as v3 polish.
