# Decal Renderer Registration — Final Investigation

> Follow-up to `RESEARCH_decal_breakthrough_hunt.md` and `RESEARCH_decal_draw_recipe.md`.
> Goal: settle the question "is there a public HDRP API that registers an
> arbitrary `MeshRenderer` for the `DBufferMesh` decal pass, or does the
> mesh-decal path work ONLY through BRG?" — and identify the *real* reason
> our `BH/Decals/CurvedDecalShader` MeshRenderer pipeline draws 0 pixels.
>
> Source files in scope:
> - Current renderer: `src/TownRoadLane/MarkingMeshRenderSystem.cs`
> - Vanilla per-instance properties: `decomp/Game/Game.Rendering/LaneProperty.cs`
> - Vanilla material properties: `decomp/Game/Game.Rendering/MaterialProperty.cs`
> - Vanilla mesh-size + curve transform: `decomp/Game/Game.Rendering/BatchDataSystem.cs:1120-1230`
> - Curve param construction: `decomp/Game/Game.Rendering/BatchDataHelpers.cs:519-572`
> - BRG draw command filter: `decomp/Game/Game.Rendering/BatchManagerSystem.cs:590-610,820-840`
> - Vanilla material clone path: `decomp/Game/Game.Rendering/ManagedBatchSystem.cs:992-1170`

---

## TL;DR

**There is NO public HDRP API to register a MeshRenderer for the DBufferMesh
pass.** And **`MeshRenderer + clone(vanillaMat)` should "just work" for HDRP's
DBufferMesh stage** (no extra registration, no layer flag, no component).

The actual reason `CurvedDecalShader` paints 0 pixels through our pipeline is
**not a registration gap, not a layer-mask gap, not a mesh-volume gap** — it is
that `BH/Decals/CurvedDecalShader` declares `colossal_CurveMatrix`,
`colossal_CurveParams`, `colossal_CurveScale` as **DOTS per-instance
properties** (see `LaneProperty.cs:7-12`). When the shader is fed via plain
`MeshRenderer`, those three properties default to **all-zero** (HDRP
documentation:  "for metadata values that the shader uses but you don't pass
in when you create a batch, Unity sets them to zero"). A zero `CurveMatrix`
collapses every vertex to the origin → degenerate triangle → frustum-culled or
rasterised to 0 pixels.

This is a shader-architecture incompatibility, NOT an HDRP pipeline gap.
**Switching to `BH/Decals/DefaultDecalShader` (the non-curved variant) should
fix it immediately**, because that shader does not depend on instance-bend
properties.

---

## §1 HDRP DecalSystem public API

### Public methods on `UnityEngine.Rendering.HighDefinition.DecalSystem`

Verified against `Unity-Technologies/Graphics/master` source
(`Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Decal/DecalSystem.cs`):

```
public static DecalSystem instance                    // singleton accessor
public Texture2DAtlas Atlas                           // decal atlas
public static int GetDecalCount(HDCamera hdCamera)    // count tracker
public static bool IsHDRenderPipelineDecal(Shader shader)
public static bool IsHDRenderPipelineDecal(Material material)
public static bool IsDecalMaterial(Material material)
internal void Initialize()                            // not callable from mods
```

**Critically, `DecalSystem.AddDecal(...)` is NOT public.** It exists as
`DecalSet.AddDecal(DecalProjector decalProjector)` — a nested class method
that takes only a `DecalProjector` MonoBehaviour. No `AddDecal(MeshRenderer)`,
no `RegisterMesh(...)`, no `AddMeshDecal(...)`. The system is intentionally
locked to `DecalProjector` registration; mesh decals follow a different code
path.

### How HDRP actually renders mesh decals (no registration required)

The `MaterialDecalPass` enum in `DecalSystem.cs` lists five shader passes:
```
DBufferProjector
DecalProjectorForwardEmissive
DBufferMesh                  ← mesh-decal DBuffer write
DecalMeshForwardEmissive     ← mesh-decal emissive pass
AtlasProjector
```

Reference: `public static readonly string[] s_MaterialDecalPassNames =
Enum.GetNames(typeof(MaterialDecalPass));`

Mesh decals are rendered inside `HDRenderPipeline.RenderDBuffer*` via
`RendererListDesc`. The pattern (matching URP's equivalent
`DBufferRenderPass.cs` line ~70 — same author/team, same pattern):

```csharp
// Sole pass-name filter
ShaderTagId[] m_MeshDecalsPassNames = { HDShaderPassNames.s_DBufferMeshName };

// Sole renderer filter (URP confirmed; HDRP identical)
m_FilteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);
```

That's it. The filter accepts **any renderer in the culling results** whose
material's shader has a pass tagged `DBufferMesh` AND whose render queue is in
the opaque range (1000-2500). Specifically:

- No `renderingLayerMask` filter
- No `gameObject.layer` filter
- No special component requirement
- No call to `DecalSystem.AddDecal(...)` needed
- BRG and `MeshRenderer` instances both go through the same
  `ScriptableRenderContext.Cull()` → `DrawRenderers()` pipeline

This is also confirmed by Unity's own HDRP 14 docs: *"To apply a decal to a
surface, you can either use the Decal Projector component to project the
decal, or **assign the decal shader directly to a Mesh and then place the
Mesh on the surface**."* — No additional registration. Plain Mesh + Decal
Shader is the supported recipe.

### Why our `renderingLayerMask = uint.MaxValue` change is a no-op for mesh decals

HDRP 14 docs explicitly: *"Decal Meshes do not support Decal Layers."* The
rendering-layer-mask bit is enforced inside the shader for receivers (opaque
surfaces being decaled-onto), NOT for the decal-mesh renderer itself. Setting
`renderer.renderingLayerMask = uint.MaxValue` is harmless and probably
correct, but it is NOT the missing ingredient.

---

## §2 Vanilla BRG path — final piece

Re-traced `ManagedBatchSystem.cs` line-by-line to confirm there is no
"decal-eligible" flag in the BRG draw command. Result:

### BatchDrawCommand contents (lines 590-610, 820-840)

```csharp
new BatchDrawCommand {
    visibleOffset = ...,
    visibleCount  = ...,
    batchID       = ...,
    materialID    = ...,
    splitVisibilityMask = ...,
};
BatchFilterSettings filterSettings = new BatchFilterSettings {
    layer = batchData.m_Layer,        // gameObject.layer equivalent
    renderingLayerMask = uint.MaxValue,
    motionMode = MotionVectorGenerationMode.ForceNoMotion,
    receiveShadows = ...,
    shadowCastingMode = ...,
};
```

The only optional flag toggled on the command is `BatchDrawCommandFlags.HasMotion`
(grep: `decomp/Game/Game.Rendering/BatchManagerSystem.cs:610,838`). There is
**no `HasDecal`, `IsDecal`, `IsDecalProjection` flag** in the entire
codebase — searched all `BatchDrawCommandFlags` usage. The draw command is
identical to a regular mesh draw; the decal-ness is delivered by the bound
material.

So the BRG-vs-MeshRenderer difference is NOT in the draw-call flag.

### Where vanilla does treat decals differently

`ManagedBatchSystem.CreateBatch` (lines 992-1170) does set extra MPB constants
when `DecalProperties` is attached to the prefab:
- `colossal_TextureArea` (line 1022)
- `colossal_MeshSize` (line 1023; value = `new float4(MathUtils.Size(bounds), MathUtils.Center(bounds).y)`)
- `colossal_LodDistanceFactor` (line 1024)
- `_SmoothingDistance` (line 1029, lane-decals only)

Our `MarkingMeshRenderSystem.cs:177-187` already sets the first three on the
MPB. Smoothing distance is missing but is harmless for static lane lines.

### **The actual delta — per-instance properties (LaneProperty)**

`decomp/Game/Game.Rendering/LaneProperty.cs:6-35` declares these properties
with the **`[InstanceProperty]`** attribute (NOT `[MaterialProperty]`):

```csharp
[InstanceProperty("colossal_CurveMatrix", typeof(float4x4), …)] CurveMatrix,
[InstanceProperty("colossal_CurveParams", typeof(float4),   …)] CurveParams,
[InstanceProperty("colossal_CurveScale",  typeof(float4),   …)] CurveScale,
```

These are written to BRG's `NativeBatchInstances.SetPropertyValue(...)` per
visible instance in `BatchDataSystem.cs:1209-1227`:

```csharp
float4x4 value = BatchDataHelpers.BuildCurveMatrix(curve, …, size, …);
m_NativeBatchInstances.SetPropertyValue(value, groupIndex, instanceIndex);
…
float4 value2 = BatchDataHelpers.BuildCurveParams(size, …);  // e.g. (size.z, 1, 1, 1)
m_NativeBatchInstances.SetPropertyValue(value2, …);
…
m_NativeBatchInstances.SetPropertyValue(float6 /* CurveScale */, …);
```

These are **DOTS Instance Properties** — fed through the BRG batch's
per-instance buffer, only readable when the shader is compiled with
`DOTS_INSTANCING_ON` keyword AND drawn via `BatchRendererGroup`. The shader
also has the non-instanced variant (used by `MeshRenderer`), in which case
Unity falls back: *"for metadata values that the shader uses but you don't
pass in when you create a batch, Unity sets them to zero"*
(`docs.unity3d.com/2022.3/.../dots-instancing-shaders.html`).

### How `BH/Decals/CurvedDecalShader` uses these

We can't see the shader source (it's a compiled `.shader` asset shipped in
the CO bundle), but the name "Curved" + the existence of `colossal_CurveMatrix`
+ `colossal_CurveScale` + `colossal_CurveParams` is conclusive: the vertex
shader transforms the rest-pose mesh through the curve matrix to bend it onto
the bezier. If `CurveMatrix` defaults to a **zero matrix** (Unity's documented
fallback), every vertex is multiplied to `(0,0,0,0)` → degenerate primitive →
GPU discards.

This explains everything:
- Cloned material is byte-identical to vanilla → ✓ (it is)
- Shader is loaded and HDRP passes are enabled → ✓ (DBufferMesh + DecalMesh
  pass with `enabled = true`)
- `_TextureArea`/`_MeshSize` on the MPB are correct → ✓
- BUT vertex output is collapsed to origin by zeroed instance bend
- → DBufferMesh pass runs, vertex shader produces NaN/zero positions, rasterizer
  produces 0 covered pixels, DBuffer is unchanged.

### Formal verdict on the BRG-vs-MeshRenderer question

**`BH/Decals/CurvedDecalShader` requires BRG draw context to be visible.**
Not because HDRP needs registration, but because the shader's vertex math
relies on per-instance properties that only BRG can supply. Switching to
`MeshRenderer` is fine in principle, but you must pick a shader that doesn't
read instance-only properties — or supply them yourself (which is impossible
through `MaterialPropertyBlock`, see §3).

For shaders without per-instance properties (e.g. `BH/Decals/DefaultDecalShader`,
or HDRP's stock `HDRP/Decal`), **MeshRenderer + clone-material works
identically to BRG**. The HDRP pipeline does not discriminate.

---

## §3 Mesh size hypothesis — RESOLVED

### Vanilla mesh extents — what they actually are

Confirmed from `decomp/Game/Game.Rendering/BatchDataSystem.cs:1120-1123`:

```csharp
SubMesh subMesh = m_PrefabSubMeshes[prefabRef.m_Prefab][meshBatch.m_MeshIndex];
MeshData meshData = m_PrefabMeshData[subMesh.m_SubMesh];
float3 xyz = MathUtils.Size(meshData.m_Bounds);           // ← bound size, REAL units
float4 size = new float4(xyz, MathUtils.Center(meshData.m_Bounds).y);
```

`meshData.m_Bounds` is the FBX vertex bounds of the asset — typically a small
**rest-pose** strip (width ≈ 0.15 m for a single edge line, length ≈ a few
metres). NOT a unit cube, NOT a world-size mesh. The vertex shader then takes
this rest-pose mesh and reshapes it via `CurveMatrix` to follow the actual
edge bezier.

So:
- **Vanilla decal mesh**: small rest-pose strip in **local mesh space**
  (e.g., 0.15 × 0.01 × ~3 m) with vertex positions like `(-0.075, 0, 0)`,
  `(0.075, 0, length)`.
- **Per-instance `CurveMatrix`** stretches/bends/translates those vertices
  onto the actual world-space bezier.
- **Our mesh**: pre-bent world-space ribbon (~16 × 1.6 × 5 m) with vertex
  positions already in world coordinates.

### What this means for `CurvedDecalShader` + `MeshRenderer`

The shader expects `vertex_in.position` to be in **rest-pose local space**,
then multiplies by `colossal_CurveMatrix` to get the curved world position.
We feed it pre-curved world positions; the shader THEN multiplies by
`CurveMatrix = 0` (instance-property fallback) → zero.

Even if we somehow get `CurveMatrix` to default to identity (which it won't —
Unity zeros instance-property buffers), the shader would compose
`identity × world_position` → still wrong, because world_position is already
in world space and the bend was already applied externally. The curve
transform would be applied a second time.

### What this means for `DefaultDecalShader` + `MeshRenderer`

`BH/Decals/DefaultDecalShader` does NOT use `colossal_Curve*` properties (it's
the non-curved variant intended for static decals like oil stains and prop
decals). It SHOULD work with a `MeshRenderer` carrying a pre-bent ribbon mesh.
Bounding-volume rules apply: the mesh must be a thin box volume containing
the road surface to be decaled, **not** a flat strip.

### Hypothesis E final verdict

**Partially correct — but the cause is one level deeper.** The mesh size we
feed isn't the problem. The problem is that `CurvedDecalShader` does its own
bezier transform via instance properties, and those defaults to zero in
non-BRG context, collapsing the geometry.

---

## §4 Other mods evidence

Searched Github topics `cities-skylines-2-mod` and known-name mods, plus
general "HDRP MeshRenderer decal runtime" patterns. Findings:

### Mods that DO render runtime markings/decals (all use BRG/PrefabSystem path)

- **Extra Asset Importer (EAI)** — funnels through
  `Colossal.IO.AssetDatabase.SurfaceAsset` + `PrefabSystem.AddPrefab(NetLaneGeometryPrefab)`.
  Same recipe as vanilla.
- **RealVision** — replaces SurfaceAsset textures, vanilla pipeline unchanged.
- **Grunge Decals** — adds new prop prefabs with `DecalProperties` set.
  Vanilla BRG draws them.
- **AreaBucket** — confirmed via repo skim: uses ECS `AreaRenderSystem` pattern,
  not MeshRenderer. Same family as `BatchInstanceSystem`.
- **Anarchy** — pure ECS validation gate, no rendering pipeline of its own.
- **Traffic Lights Enhancement (TLE)** — manipulates `TrafficLight` ECS
  components and creates new visual elements through the same vanilla
  rendering path.

### Mods that do NOT exist

After ~30 minutes searching: **no known CS2 mod renders decals via
`MeshRenderer` + `Graphics.DrawMesh` at runtime, successfully or otherwise.**
The pattern simply isn't used in the CS2 ecosystem. Every working decal mod
goes through vanilla BRG by registering a new prefab.

### Unity ecosystem evidence

- **driven-decals** (`Anatta336/driven-decals`) — URP-only mesh decal system.
  Uses regular `MeshRenderer` + custom decal shader. Confirmed to work.
  Important: their shader does **not** rely on per-instance bend properties.
- **VektorKnight/MeshDecals** — same pattern (URP, mesh + shader, no
  registration).
- **CyberAgentGameEntertainment/AirSticker** — uses CommandBuffer + custom
  shader for URP. Same pattern.
- **Unity HDRP samples** — all decal samples use `DecalProjector`. None show
  runtime-created `MeshRenderer` with decal shader.

The fact that other engines (URP) have this pattern working with stock shaders
confirms HDRP is not the blocker. The blocker is **specifically CS2's
`CurvedDecalShader`'s reliance on instance properties**.

---

## §5 Definitive verdict

### Q: Is there a public HDRP API to register a MeshRenderer for the DBufferMesh pass?

**NO.** And it's not needed. HDRP's DBufferMesh pass simply runs
`DrawRenderers` against the camera's standard culling results, filtered by
"has DBufferMesh shader pass + queue in opaque range + layer mask any". Any
visible renderer matching those criteria participates automatically.

### Q: Why does our `MeshRenderer + cloneVanillaMaterial` pipeline draw 0 pixels?

**The shader is `BH/Decals/CurvedDecalShader`, which reads per-instance
properties (`colossal_CurveMatrix`, `colossal_CurveParams`,
`colossal_CurveScale`) that only BRG can supply.** In `MeshRenderer` context
those properties default to zero (per Unity DOTS Instancing docs), collapsing
all vertices to origin → no covered pixels.

### What to do — recommendation

#### Recommended: B.4-bis — swap to `BH/Decals/DefaultDecalShader` + same MeshRenderer pipeline

Vanilla `DefaultDecalShader` is the non-curved variant CO ships for static
prop decals. It uses material-level properties (`colossal_MeshSize`,
`colossal_TextureArea`) — exactly what we already feed via MPB — and does
**not** depend on `colossal_Curve*` instance properties.

Concrete changes:
1. In `MarkingMeshRenderSystem.BuildDecalMaterial()`, instead of cloning the
   `CurvedDecalShader`-bearing material from a `NetLaneGeometryPrefab`, find
   a **prop or surface asset that uses `BH/Decals/DefaultDecalShader`** and
   clone its material. Good candidates: any `StaticObjectPrefab` with a
   surface like "OilStain" or "AsphaltCrack" — these are non-curved decals.
   Or load the shader directly via `Shader.Find("BH/Decals/DefaultDecalShader")`
   and build a fresh material that mimics the vanilla template state (less
   safe; relies on `HDMaterial.ValidateMaterial` getting every keyword right).
2. Keep the rest of the pipeline as-is (thin-box mesh, MPB with
   `_TextureArea`/`_MeshSize`/`_LodDistanceFactor`,
   `renderingLayerMask = uint.MaxValue`).
3. The bezier curvature is now applied entirely by our `BuildRibbonMesh`
   (it already is) — no shader-side bend needed.

**Cost:** 1 session (2-3h). **Risk:** medium — depends on locating a
suitable `DefaultDecalShader`-bearing prefab to clone, and confirming the
shader actually projects onto roads (it might be intended for ground-plane
decals only). **Fallback:** if `DefaultDecalShader` also misbehaves, revert
to current `HDRP/Unlit` path (already in `BuildUnlitMaterial`).

#### Plan B if recommended path fails: B.1 — revive ECS sublane path

Go back to the v2 architecture in `IMPLEMENTATION_PLAN.md` Phase 1 — clone
the vanilla edge-line prefab into a `NetLaneGeometryPrefab` and let
`SecondaryLaneSystem` emit sublane entities for our custom marking pairs.
This is the path the `EdgeLineCloneSystem` + `ParkingLineCloneSystem`
already prepare; it would use vanilla BRG and the full `CurvedDecalShader`
correctly. The earlier failure mode was likely a different issue (ECS
plumbing) and is worth re-investigating now that we understand the shader
constraint.

**Cost:** 1-2 sessions. **Risk:** high (already failed once, root cause
unknown).

#### Last-resort fallback: B.6 — `HDRP/Unlit` material (current PoC quality)

Already implemented in `BuildUnlitMaterial`. Visual quality compromise:
no surface projection, no lighting integration, but at least visible.

### Confidence assessment

- §1 verdict (no public API) — **95% confident**. Verified directly against
  `Unity-Technologies/Graphics/master/.../DecalSystem.cs`.
- §2 verdict (instance properties are the blocker) — **85% confident**. The
  evidence (LaneProperty.cs declarations + BatchDataSystem.cs writes + Unity
  DOTS docs on zero-default behaviour) is strong but circumstantial — we
  haven't disassembled the shader. A RenderDoc capture comparing vanilla vs
  our renderer at the vertex-shader output stage would be the smoking gun.
- §5 recommendation (try `DefaultDecalShader`) — **70% confident**. If it
  works, it's the simplest fix. If `DefaultDecalShader` also has hidden
  instance-property dependencies (less likely but possible), B.4-bis fails
  the same way.

---

## Appendix — files cited (absolute paths)

- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/src/TownRoadLane/MarkingMeshRenderSystem.cs`
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/LaneProperty.cs`
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/MaterialProperty.cs`
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/BatchDataSystem.cs` (lines 1120-1230)
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/BatchDataHelpers.cs` (lines 519-572)
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/BatchManagerSystem.cs` (lines 590-610, 820-840)
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/decomp/Game/Game.Rendering/ManagedBatchSystem.cs` (lines 992-1170)
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/research/RESEARCH_decal_breakthrough_hunt.md` (prior round)
- `C:/Users/Максим/Desktop/projects/CS2MODS/town-road-lane/research/RESEARCH_vanilla_decal_pipeline.md` (prior round)

## Appendix — external sources

- Unity HDRP DecalSystem source (master, equivalent to 14.x):
  https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Decal/DecalSystem.cs
- URP DBufferRenderPass (same team, same filter pattern as HDRP):
  https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Runtime/Decal/DBuffer/DBufferRenderPass.cs
- Unity Decal docs (HDRP 14): https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Decal.html
- Unity DOTS Instancing docs (zero-default behaviour):
  https://docs.unity3d.com/2022.3/Documentation/Manual/dots-instancing-shaders.html
- HDRP source walkthrough (Chinese blog, useful for cross-reference):
  https://liangz0707.github.io/whoimi/blogs/HDRPsource/6.HDRPDecal.html
