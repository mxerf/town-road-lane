# Decal Draw-Call Recipe — Where vanilla CS2 actually issues decal pixels

> Goal of this report: find the exact draw-call recipe used by vanilla CS2 to
> render `BH/Decals/DefaultDecalShader` decals, decide whether our current
> `Graphics.DrawMesh` path can ever work with that shader, and if yes give a
> complete recipe (matrix, MPB, layers, mesh orientation). If no, propose
> alternatives.

Status: **DrawMesh path CAN work for HDRP decals** but the current implementation
in `src/TownRoadLane/MarkingMeshRenderSystem.cs` is missing several material/MPB
setups that vanilla does outside the per-instance MPB. The most important miss
is on the **material**, not the MPB: `colossal_DecalLayerMask` is set on the
**material** by vanilla (via `math.asfloat(int)`), not on the MPB; on top of that
several decal-template properties (`_DrawOrder`, `_DecalMeshDepthBias`, the
HDRP material keywords that `HDMaterial.ValidateMaterial` derives from the
shader stencil/decal-affects-flags) must be present on the cloned material.

---

## A) Where vanilla actually issues the decal draw call

### A.1 Vanilla decals do NOT use `Graphics.DrawMesh*`

Vanilla CS2 routes every decal that comes from a `RenderPrefab` with a
`DecalProperties` component through the **`BatchRendererGroup` (BRG)** path —
not the immediate-mode `Graphics.DrawMesh*` family.

The chain (file:line references, all absolute paths under
`C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane`):

1. `decomp/Game/Game.Rendering/BatchManagerSystem.cs:984` —
   `m_ManagedBatches = ManagedBatches<OptionalProperties>.Create(m_NativeBatchInstances, OnPerformCulling, …)`
   creates the BRG and registers the cull callback.
2. `decomp/Game/Game.Rendering/BatchManagerSystem.cs:1455` —
   `private JobHandle OnPerformCulling(BatchRendererGroup rendererGroup, BatchCullingContext cullingContext, BatchCullingOutput cullingOutput, IntPtr userContext)`
   is the actual BRG culling callback. It schedules `BatchCullingJob`
   (lines 1497–1510) which writes draw commands into `cullingOutput`.
3. `decomp/Game/Game.Rendering/BatchRendererSystem.cs:77` —
   `BatchID batchID = managedBatches.AddBatchRenderer(batchPropertyAccessor);`
   the **only AddBatch in the codebase**. Lives in the `Colossal.Rendering`
   native DLL (`ManagedBatches<T>.AddBatchRenderer`); source is not in our
   decomp.
4. `decomp/Game/Game.Rendering/ManagedBatchSystem.cs:650-652` registers a
   custom batch + mesh for each prefab's surface material with shader
   `BH/Decals/DefaultDecalShader`.

So the production pipeline never grep-shows `Graphics.DrawMesh` *for decals*.
The only sites that *do* call `Graphics.DrawMesh*` are:

- `decomp/Game/Game.Rendering/RouteRenderSystem.cs:262` — `DrawMeshInstancedIndirect` for route arrows.
- `decomp/Game/Game.Rendering/OverlayRenderSystem.cs:903,907,913,946` — overlays (tool feedback).
- `decomp/Game/Game.Rendering/AreaRenderSystem.cs:99,110` — area/zone surfaces (vertex-colored quads).
- `decomp/Game/Game.Rendering/AggregateRenderSystem.cs:66,90` — aggregate (sub-batches).
- `decomp/Game/Game.Rendering/NotificationIconRenderSystem.cs:159` — icons.
- `decomp/Game/Game.Rendering/BrushRenderSystem.cs:134` — brush UI.
- `decomp/Game/Game.Simulation/TerrainSystem.cs` — terrain (not a decal).

None of these touch `BH/Decals/DefaultDecalShader`. They use their own
purpose-built shaders.

### A.2 BUT: vanilla also has a *non-BRG* path for decals — the editor renderer

`decomp/Game/Game.Rendering.Debug/RenderPrefabRenderer.cs` is a regular
`MonoBehaviour` used by the editor/preview UI. It creates **plain GameObjects
with `MeshFilter` + `MeshRenderer`** (`RenderPrefabRenderer.cs:202-219`) and
sets the decal properties through an MPB
(`RenderPrefabRenderer.cs:401-414`, `SetDecalProperties`). It uses the same
`BH/Decals/DefaultDecalShader` and **HDRP renders it**. This confirms: HDRP
*will* draw `DefaultDecalShader` through the standard `MeshRenderer` path,
which is exactly what `Graphics.DrawMesh` becomes inside the SRP. So the
DrawMesh path is not "principially broken" — it just needs the right
combination of material setup + mesh orientation + MPB.

### A.3 Key implication

The BRG path optimises throughput for thousands of repeating decal instances.
For a mod that draws **a few hundred custom procedural decal meshes**, the
DrawMesh / RenderMesh / RenderMeshIndirect path is appropriate and
demonstrably works in HDRP (the editor uses it every time you preview an
asset). The thing we have to do right is the **material setup**, plus mesh
shape.

---

## B) Can our DrawMesh path work with `DefaultDecalShader`? — Yes

Confirmed by `RenderPrefabRenderer` using `MeshRenderer` + that exact shader.
HDRP's `DecalSystem` does not require `DecalProjector` MonoBehaviours — it
accepts any `MeshRenderer` whose material's `Surface Type` field maps to a
decal stencil pass (which `DefaultDecalShader` already does, otherwise it
wouldn't render in BRG either).

Three caveats that explain "draws=N, zero pixels":

1. **`colossal_DecalLayerMask` is a *material* property in vanilla, not an
   MPB property**. `ManagedBatchSystem.cs:1408` does
   `material.SetFloat(m_DecalLayerMask, math.asfloat(materialKey.decalLayerMask))`.
   Setting it on MPB still works for *non*-decal-layer-aware shaders, but for
   the HDRP decal pass it is read at material level by the decal projector
   logic — if not set, the decal layer mask defaults to 0 and HDRP culls the
   decal from every receiver. **Move this from MPB to material.**

2. **`HDMaterial.ValidateMaterial` is not enough on its own**. Vanilla also
   manually toggles three keywords that ValidateMaterial may not re-derive
   when the material was freshly constructed from a stock shader (it derives
   them when properties like `_AffectAlbedo` change):
   `_MATERIAL_AFFECTS_ALBEDO`, `_MATERIAL_AFFECTS_NORMAL`,
   `_MATERIAL_AFFECTS_MASKMAP`. Logs in `MarkingMeshRenderSystem.cs:300-303`
   show our material already has all three, so this is not the problem in
   our specific case — but if you ever Find a fresh DecalShader from disk you
   must also call `HDMaterial.ValidateDecalMaterial` (HDRP-specific) or set
   `mat.SetFloat("_AffectAlbedo", 1f)` etc. and *then* ValidateMaterial.

3. **Mesh shape**. See §D below.

---

## C) Complete DrawMesh recipe for HDRP decals

### Matrix

`Matrix4x4.identity` is correct when the mesh is built in **world-space** (our
current case: `BuildRibbonMesh` outputs absolute coordinates). If you instead
build the mesh in local space and pass a TRS for the world transform, both
modes are equivalent for HDRP decal projection — but the
**`colossal_MeshSize` property must always be the AABB size of the mesh
*after* the matrix is applied** (i.e. world-space extents). Our code already
does `mesh.bounds.size` of a world-space mesh, so identity matrix is right.

### Per-call parameters

```csharp
Graphics.DrawMesh(
    mesh,
    Matrix4x4.identity,
    material,           // see "Material setup" below
    layer: 0,           // HDRP doesn't filter decals by layer; 0 is fine
    camera,             // pass the specific camera; one DrawMesh per camera
    submeshIndex: 0,
    properties: mpb,    // see "MPB setup"
    castShadows: ShadowCastingMode.Off,
    receiveShadows: false,
    probeAnchor: null,
    useLightProbes: false   // optional overload; LightProbeUsage.Off
);
```

Vanilla parallels (file:line):

- `AreaRenderSystem.cs:99,110` — same exact signature for area surfaces.
- `RenderPrefabRenderer.cs:213-214` — the editor uses `MeshRenderer` instead
  but configures identical castShadows=Off / receiveShadows=false.

### Material setup (must happen ONCE at material construction)

```csharp
var shader = Shader.Find("BH/Decals/DefaultDecalShader");
var mat = new Material(shader) { name = "TRL_Decal", hideFlags = HideFlags.HideAndDontSave };

// Source bitmap. _BaseColorMap is the canonical slot; some templates also
// expect _MainTex as a legacy alias.
mat.SetTexture("_BaseColorMap", tex);
if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);

// Decal-layer-mask — CRITICAL, sets on MATERIAL via int-as-float bit cast.
// 2 = DecalLayers.Roads (from decomp/Game/Game.Rendering/DecalLayers.cs).
// See ManagedBatchSystem.cs:1408.
mat.SetFloat(Shader.PropertyToID("colossal_DecalLayerMask"),
             math.asfloat((int)Game.Rendering.DecalLayers.Roads));

// Render queue. Vanilla decals live in 2000-2500 range. Without a manual
// override the shader's renderQueue is correct; only set this if you want
// to layer multiple custom decals.
mat.renderQueue = mat.shader.renderQueue;     // = 2000 from logs

// HDRP decal "affects" keywords — usually already on the shader, but force
// in case ValidateMaterial doesn't infer them.
mat.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO");
mat.EnableKeyword("_MATERIAL_AFFECTS_NORMAL");
mat.EnableKeyword("_MATERIAL_AFFECTS_MASKMAP");

// Stable draw order (optional, but vanilla materials usually set it).
if (mat.HasProperty("_DrawOrder")) mat.SetFloat("_DrawOrder", 0f);
// Optional but vanilla touches: depth bias to push decal slightly above the
// surface — pre-empts z-fight on overlapping decals.
if (mat.HasProperty("_DecalMeshDepthBias")) mat.SetFloat("_DecalMeshDepthBias", 0f);

// FINAL step — translates the HDRP "affects" toggles into runtime keywords.
HDMaterial.ValidateMaterial(mat);
```

### MPB setup (per draw)

For **decals** vanilla's per-instance MPB carries exactly these three
properties (`ManagedBatchSystem.cs:1010-1024`):

| Property | Type | Value | Notes |
|---|---|---|---|
| `colossal_TextureArea` | `float4` | `(uMin, vMin, uMax, vMax)` | Sub-rect of the source texture to sample. `(0,0,1,1)` = full texture. From `DecalProperties.m_TextureArea`. |
| `colossal_MeshSize` | `float4` | `(sizeX, sizeY, sizeZ, centerY)` | **World-space AABB**, NOT local. `xyz` = bounds.size, `w` = bounds.center.y. The shader uses `w` for per-pixel projection cutoff. |
| `colossal_LodDistanceFactor` | `float` | `RenderingUtils.CalculateDistanceFactor(lod)` | For LOD 0 use `1f`. Without this set, HDRP may fade the decal out at any distance. |

For **lane-type decals** (which is our exact use case — lane line markings
sit on `MeshType.Lane`) there is one more:

| `colossal_SmoothingDistance` | `float` | from `MeshData.m_SmoothingDistance` | controls fade between LOD samples; pass `0f` if unsure. |

Other `MaterialProperty` enum entries (SingleLightsOffset, DilationParams,
ImpostorFrames, ImpostorSize, ImpostorOffset, ShapeParameters{1,2},
CullParameters, OverlayParameters, AlphaMaskIndexRange, BaseColor) are
**not** needed for plain decals — they belong to impostors, characters,
overlay (UI projector), color-variant assets, and skinned meshes. The
clearest evidence is `ManagedBatchSystem.cs:1015-1035` — the `if
(decalProperties != null)` branch sets ONLY the 4 properties above
(TextureArea, MeshSize, LodDistanceFactor, optional SmoothingDistance) and
then falls through to the non-decal block (skipped via the `else`).

Important: **DO NOT** put `colossal_DecalLayerMask` on the MPB. Even though
`RenderPrefabRenderer.SetDecalProperties` puts it on the MPB
(`RenderPrefabRenderer.cs:409`), that codepath is a debug-renderer hack — the
production BRG codepath sets it on the **material** (`ManagedBatchSystem.cs:
1406-1408`). The HDRP decal projection code reads it from material constants,
not from instance constants. Our v1 PoC currently sets it on MPB only —
**that is one of the bugs causing zero pixels.**

### DecalLayers enum cheat-sheet (from `decomp/Game/Game.Rendering/DecalLayers.cs`)

```
Terrain   = 1
Roads     = 2
Buildings = 4
Vehicles  = 8
Creatures = 0x10
Other     = 0x20
```

For road markings: use `2` (Roads) or `3` (Roads | Terrain) if you also
want bitmap to appear on raw terrain where a road dips below ground.
Pass as `math.asfloat(2)` not `2f`.

---

## D) Mesh orientation — vanilla decals are vertical extruded volumes

This is the second source of "zero pixels". HDRP decals work like
`DecalProjector`: a volume is rendered, and every pixel inside the volume's
projection sees the decal painted onto the deepest opaque surface along the
projector's local **−Y** axis.

`decomp/Game/Game.AssetPipeline/AssetImportPipeline.cs:2509-2557`
(`SetupDecalComponent`) confirms vanilla decal meshes have **vertices
whose normals point +Y** — those are the visible "top" face that defines
the texture area. Specifically line 2536:

```
if (Vector3.SqrMagnitude(normals[j]) > 0.01f && Vector3.Angle(Vector3.up, normals[j]) < 0.01f)
```

The vertices used to compute the texture-area bounds are explicitly filtered
to those facing straight up. So vanilla decal meshes are 3D volumes whose
top face is horizontal (+Y normal), sides are skirts going down, bottom is
an inverted face. Same shape as `AreaRenderSystem.CreateMesh()` at
`AreaRenderSystem.cs:225-267` — a 6-vertex prism with base at y=0 and top
at y=1.

**Our current `MarkingMeshRenderSystem.BuildRibbonMesh` already does this
correctly** — it produces a thin extruded box with top face normal +Y,
bottom −Y, sides ±X. Top face vertices at `liftTop` (y = `kHeightOffset +
kBoxHeight`) and bottom at `liftBot` (y = `kHeightOffset`). With the side
faces wrapping the volume, HDRP should accept it as a decal projection
volume. So **mesh shape is NOT the current blocker** (it was in v1 era when
mesh was a flat strip).

One nuance: HDRP's decal projector default projects DOWN (−Y), and the
shader looks at the world-space AABB of the rendered geometry to set up the
projection volume. Our mesh has `kBoxHeight = 1.5m` tall and `bounds.Expand(0.1)`,
which gives plenty of vertical extent. The road surface must lie *inside*
that vertical extent at any sample, which is why `kHeightOffset = -0.5f` is
correct.

---

## E) Other BH/Decals/* shaders to try

`Grep "BH/Decals/" decomp` returns ZERO matches — meaning no other decal
shader name is hard-referenced anywhere in the C# decomp. `CurvedDecalShader`
is mentioned only in our own research notes / source. The pool of decal
shaders in the game (visible via Unity's Shader.Find at runtime) is:

- `BH/Decals/DefaultDecalShader` — confirmed, what we're using.
- Possibly `BH/Decals/CurvedDecal` — only existence rumour; runtime
  `Shader.Find` test will confirm. Behaviour likely identical except for
  per-vertex curve baking.

Best falsify-or-confirm cheaply: at mod startup do
`Shader.Find("BH/Decals/CurvedDecal")` and log non-null. If found, swap and
re-test.

---

## F) Decision summary

**The DrawMesh path is viable**, contradicting the title hypothesis. The
zero-pixel bug in `MarkingMeshRenderSystem` is the combination of:

1. `colossal_DecalLayerMask` set on MPB instead of material (Bug #1).
2. Possibly missing keywords/material props that ValidateMaterial does not
   auto-derive from a stock shader instance (Bug #2 — speculative;
   keywords look right in logs so probably fine).
3. Texture-area defaults possibly wrong — if our borrowed texture is an
   atlas, `(0,0,1,1)` samples the entire atlas, not just the lane-line
   sprite. Need to derive from the source prefab's `DecalProperties.m_TextureArea`
   (Bug #3).
4. Texture used is the surface's `_BaseColorMap` but the borrowed material
   was the **net-lane mesh's** material, not a decal material; that texture
   may not be a paint bitmap at all. Check by logging the texture name in
   `TryBorrowLaneLineTexture` (Bug #4).

### Fix priority

1. **Move `colossal_DecalLayerMask` from MPB to material** — one-line fix,
   biggest impact.
2. **Verify the borrowed texture is a paint bitmap** — log
   `tex.name + tex.width + tex.height` and inspect; if it's something
   weird like "PavementAlbedo" or "RoadDiffuse" the entire approach is
   sampling the wrong texture.
3. **Compute `colossal_TextureArea` from the source `DecalProperties`** —
   look up the lane-line decal prefab via `_edgeLineSys.ClonePrefabEU`,
   `prefab.GetComponent<DecalProperties>().m_TextureArea` → set the float4
   on MPB.
4. **Add `colossal_LodDistanceFactor = 1f` to MPB** — without this, HDRP
   may fade decal at any non-trivial distance.

After all four fixes, re-test. Expected behaviour: a textured ribbon
painted onto the road surface, following the Bezier arc.

### Backup plan (≈2h if Fix #1 alone doesn't unblock)

Switch to **`Graphics.RenderMesh`** (the newer API, RenderParams-based) and
optionally try **`Graphics.RenderMeshIndirect`** like
`AreaRenderSystem.cs:179` does. RenderMesh exposes
`RenderParams.lightProbeUsage = LightProbeUsage.Off`,
`renderingLayerMask`, `motionVectorMode`, etc. — more knobs in case HDRP
is filtering us out by one of those.

### Fallback if RenderMesh also fails (≈4h)

Use **`UnityEngine.Rendering.HighDefinition.DecalProjector`** components on
runtime GameObjects, one per pair, dimensioned to the AABB of the Bezier
ribbon. Pros: guaranteed-correct HDRP integration. Cons: rectangular
projection only — your S-curved Bezier will project a straight rectangle
through the curve, which clips at sharp angles. Acceptable for short
straight lane segments, ugly for tight turns. Used by some mods (notably
the older AnarchyMod fork).

### Hard fallback (≈1 session)

Stay on `HDRP/Unlit` (current PoC works), accept the cosmetic gaps, and
focus on the killer feature (per-segment delete + line styles). Mark
visual polish as "v3 cosmetic milestone".

---

## Appendix: file:line index

| Subject | File | Line |
|---|---|---|
| BRG setup | `decomp/Game/Game.Rendering/BatchManagerSystem.cs` | 984 |
| OnPerformCulling | `decomp/Game/Game.Rendering/BatchManagerSystem.cs` | 1455 |
| AddBatchRenderer (native) | `decomp/Game/Game.Rendering/BatchRendererSystem.cs` | 77 |
| Decal material build (BRG) | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 1015–1035 |
| Decal layer mask on material | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 1408 |
| `m_DecalLayerMask` ID init | `decomp/Game/Game.Rendering/ManagedBatchSystem.cs` | 431 |
| Editor MeshRenderer decals | `decomp/Game/Game.Rendering.Debug/RenderPrefabRenderer.cs` | 213, 401–414 |
| Decal MPB props (editor) | `decomp/Game/Game.Rendering.Debug/RenderPrefabRenderer.cs` | 580–584 |
| MaterialProperty enum | `decomp/Game/Game.Rendering/MaterialProperty.cs` | 5–56 |
| DecalLayers enum | `decomp/Game/Game.Rendering/DecalLayers.cs` | 5–14 |
| DecalProperties prefab comp | `decomp/Game/Game.Prefabs/DecalProperties.cs` | 11–25 |
| Vanilla decal volume mesh | `decomp/Game/Game.Rendering/AreaRenderSystem.cs` | 225–267 |
| Decal asset import (normals +Y) | `decomp/Game/Game.AssetPipeline/AssetImportPipeline.cs` | 2509–2557 |
| Graphics.DrawMesh in vanilla (areas) | `decomp/Game/Game.Rendering/AreaRenderSystem.cs` | 99, 110 |
| Graphics.RenderMeshIndirect (areas) | `decomp/Game/Game.Rendering/AreaRenderSystem.cs` | 179, 214 |
| Current mod render code | `src/TownRoadLane/MarkingMeshRenderSystem.cs` | 162–216 |
| Current mod material build | `src/TownRoadLane/MarkingMeshRenderSystem.cs` | 263–305 |
| Current mod DecalLayerMask on MPB (BUG) | `src/TownRoadLane/MarkingMeshRenderSystem.cs` | 205 |
