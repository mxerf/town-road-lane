# Vanilla CS2 ECS Decal Pipeline — Research

## Overview

There is **no separate "decal pipeline"** in vanilla CS2. Painted road markings (edge
lines, parking lines, crosswalks), road wear, oil stains, etc. all go through the
**same monolithic batched-instance renderer** that draws every prop, building, vehicle,
edge segment, lane, and zone block. The decal-ness is a per-mesh flag
(`MeshFlags.Decal` → `MeshData.m_State`) combined with a `DecalProperties` component
on the `RenderPrefab`; `ManagedBatchSystem` reads both at material-creation time to
flip `ShadowCastingMode.Off`, set the decal layer mask (`colossal_DecalLayerMask`
shader var), bind the `_TextureArea`/`_MeshSize`/`_LodDistanceFactor`/`SmoothingDistance`
material-property-block uniforms, and choose the surface-asset–provided template
material (a decal-shader instance for marking meshes, an opaque-shader instance for
everything else). The visual difference between a "decal" and a regular mesh draw is
**100% material + shader + render-queue**, not pipeline.

Conceptually: vanilla draws every `MeshBatch`-bearing entity as **plain geometry
instances** through Unity's `BatchRendererGroup` (BRG) low-level rendering API
(`NativeBatchInstances<…>`/`NativeBatchGroups<…>` from `Colossal.Rendering`). The
"decal look" emerges because the mesh asset is a flat strip with extruded thickness,
its bound shader (`BH/Decals/DefaultDecalShader` or `…/CurvedDecalShader`) does
projective UV math and depth-fade against the road below it, and the mesh's
`MeshFlags.Decal` flag tells `BatchInstanceSystem.UpdateLaneInstances` to suppress
outline overlay when not selected and tells `ManagedBatchSystem.CreateBatch` to skip
shadow casting and reserve render-queue for transparent decal ordering.

A mod *cannot* feed a procedurally-built `UnityEngine.Mesh` into this pipeline without
going through `PrefabSystem`. Every step of the pipeline — `RequiredBatchesSystem`,
`ManagedBatchSystem.CreateBatch`, `BatchMeshSystem.AddBatch`/`LoadMeshes`,
`BatchManagerSystem.MergeGroups` — assumes the **mesh entity is a `RenderPrefab`
registered in `PrefabSystem`** with a `GeometryAsset` and one or more `SurfaceAsset`s
that can be loaded from the asset database. Mod-created entities lack that prefab
registration, so the rendering hooks silently skip them.

## Pipeline diagram

```
                            ┌──────────────────────────────────────────────┐
                            │ MANAGED INITIALIZATION (one-shot, per prefab) │
                            │ PrefabSystem.AddPrefab(NetLaneGeometryPrefab) │
                            │   creates prefab entity with archetype:       │
                            │   {NetLaneData, NetLaneArchetypeData,         │
                            │    NetLaneGeometryData, SubMesh[], …}         │
                            │ Each SubMesh.m_SubMesh → separate ECS entity  │
                            │ for the mesh, with:                           │
                            │   {MeshData, SharedMeshData, BatchGroup[]}    │
                            │   (BatchGroup buffer is INITIALLY EMPTY)      │
                            │ Managed cache: PrefabSystem.m_Prefabs[i] →    │
                            │   RenderPrefab(geometryAsset, surfaceAsset[]) │
                            └──────────────────────┬───────────────────────┘
                                                   │
                                                   ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Per-frame (or per-instance-spawn) — populate batch groups                    │
│                                                                              │
│ ┌──────────────────────────────────────────────────────────────────────┐    │
│ │ RequiredBatchesSystem (Game.Rendering)                                │    │
│ │   IJobChunk over: { MeshBatch, PrefabRef, (Updated | BatchesUpdated)} │    │
│ │   For each lane/object entity, walks SubMesh[] of its prefab,         │    │
│ │   computes (layer, type, partition) tuple, calls                      │    │
│ │   m_NativeBatchGroups.CreateGroup() → groupIndex                      │    │
│ │   Appends BatchGroup{m_GroupIndex, m_Layer, m_Type, m_Partition} to   │    │
│ │   the mesh-entity's BatchGroup buffer. Calls CreateBatch() for each   │    │
│ │   sub-mesh / LOD step. STILL NO MATERIAL — pure native bookkeeping.   │    │
│ └──────────────────────────────────────────────────────────────────────┘    │
│                                  │                                           │
│                                  ▼                                           │
│ ┌──────────────────────────────────────────────────────────────────────┐    │
│ │ ManagedBatchSystem (Game.Rendering, runs after RequiredBatches)       │    │
│ │   Iterates m_NativeBatchGroups.GetUpdatedManagedBatches()             │    │
│ │   For each new batch:                                                 │    │
│ │     PrefabSystem.GetPrefab<RenderPrefab>(groupData.m_Mesh) ← REQUIRED │    │
│ │     SurfaceAsset = renderPrefab.GetSurfaceAsset(subMeshIdx)           │    │
│ │     SurfaceAsset.LoadProperties(useVT: true)  ← reads template mat,   │    │
│ │                                                  textures, keywords    │    │
│ │     DecalProperties? → set materialKey.decalLayerMask,                │    │
│ │                       _TextureArea / _LodDistanceFactor MPB,          │    │
│ │                       force ShadowCastingMode.Off                     │    │
│ │     m_Materials[materialKey] = new Material(template) + ValidateMat() │    │
│ │     CustomBatch ↑ stored in ManagedBatches<OptionalProperties>        │    │
│ │     BatchMeshSystem.AddBatch(customBatch, batchIndex)                 │    │
│ │     ManagedBatches.SetDefaults(material props → instance buffer)     │    │
│ └──────────────────────────────────────────────────────────────────────┘    │
│                                  │                                           │
│                                  ▼                                           │
│ ┌──────────────────────────────────────────────────────────────────────┐    │
│ │ BatchMeshSystem (Game.Rendering)                                      │    │
│ │   Priority-sorted GeometryAsset loader (off-CPU when possible).      │    │
│ │   Calls renderPrefab.ObtainMesh(subMeshIdx) → Unity Mesh              │    │
│ │   Uploads vertex / shape / overlay data to GraphicsBuffer.            │    │
│ │   GeometryAsset MUST exist on the RenderPrefab.                       │    │
│ └──────────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ Per-frame — register instances                                                │
│                                                                              │
│ PreCullingSystem.InitializeCullingJob — for each entity with CullingInfo,    │
│   computes m_Bounds from Curve.m_Bezier × NetLaneGeometryData.m_Size,        │
│   sets m_Mask from prefab's layers. Adds entity to NativeList<PreCullingData>│
│   and assigns m_CullingIndex (via Interlocked.Increment).                    │
│ PreCullingSystem.UpdateCullingJob — tags PreCullingData.m_Flags with the     │
│   subsystem type: Lane / Net / Object / Zone, based on which component the   │
│   entity has (m_LaneData.HasComponent(entity) → Lane).                       │
│                                                                              │
│ BatchInstanceSystem.BatchInstanceJob — IJobParallelForDefer over             │
│   PreCullingData list. Switches on flags:                                    │
│     PreCullingFlags.Lane    → UpdateLaneInstances(cullingData)               │
│     PreCullingFlags.Net     → UpdateNetInstances                             │
│     PreCullingFlags.Object  → UpdateObjectInstances                          │
│     PreCullingFlags.Zone    → UpdateZoneInstances                            │
│                                                                              │
│   In UpdateLaneInstances:                                                    │
│     m_MeshBatches.TryGetBuffer(entity) → DynamicBuffer<MeshBatch>            │
│     prefabRef = m_PrefabRefData[entity]                                      │
│     m_PrefabSubMeshes.TryGetBuffer(prefabRef.m_Prefab) → SubMesh[]           │
│     For each SubMesh:                                                        │
│       MeshData md = m_PrefabMeshData[subMesh.m_SubMesh]                      │
│       DynamicBuffer<BatchGroup> bg = m_PrefabBatchGroups[subMesh.m_SubMesh]  │
│       AddInstance(meshBatches, bg, layer, MeshType.Lane, …)                  │
│         → enqueues GroupActionData{m_AddInstanceData = InstanceData{entity,  │
│                                                                meshIdx,…}}   │
│                                                                              │
│ BatchInstanceSystem.Groups.GroupActionJob — drains the queue:                │
│   For each GroupActionData with m_AddInstanceData:                           │
│     groupInstanceUpdater.AddInstance(cullingData, instanceData, mergeIdx)   │
│     → entity becomes a draw-call participant in NativeBatchInstances<…>      │
│   For each GroupActionData with RemoveInstanceIndex:                         │
│     groupInstanceUpdater.RemoveInstance(idx)                                 │
│                                                                              │
│ BatchRendererSystem — registers Unity BRG renderers (BatchID) for each       │
│   updated group, removes obsolete renderers; passes through to               │
│   ManagedBatches.AddBatchRenderer.                                           │
│                                                                              │
│ Unity HDRP/BRG culling callback → BatchInstanceSystem.AllocateCullingJob     │
│   fills BatchCullingOutputDrawCommands from NativeBatchInstances.            │
│   ↓                                                                          │
│ GPU draws instances using upload buffers + batched material PSO.            │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Component / system inventory

### Components (ECS data)

| File | Role |
|---|---|
| `decomp/Game/Game.Rendering/MeshBatch.cs:7` | Per-entity buffer linking entity → `(groupIndex, instanceIndex, meshGroup, meshIndex, tileIndex)`. Populated by `BatchInstanceSystem.AddInstance`. **One element per (BatchGroup, tile)** the entity contributes to. |
| `decomp/Game/Game.Prefabs/BatchGroup.cs:5` | Buffer on **mesh prefab entity** (`subMesh.m_SubMesh`). Populated by `RequiredBatchesSystem.InitializeBatchGroup` per `(layer, type, partition)` it ever needs. `m_GroupIndex` is the global key into `NativeBatchGroups<…>`. |
| `decomp/Game/Game.Rendering/CullingInfo.cs:8` | Per-entity. `m_CullingIndex` (assigned by `PreCullingSystem.PassedCulling`) is the slot in `NativeList<PreCullingData>`. **`m_CullingIndex == 0` means "not registered for culling yet."** |
| `decomp/Game/Game.Rendering/PreCullingData.cs` | Live culling state in a `NativeList`, indexed by `CullingInfo.m_CullingIndex`. Flags include `Lane / Net / Object / Zone / NearCamera / Updated / BatchesUpdated`. |
| `decomp/Game/Game.Net/LaneGeometry.cs:8` | Zero-size **tag component** on a lane entity. Identifies it as a lane that has visible geometry. Required by the archetype for `MeshBatch` to be present. |
| `decomp/Game/Game.Prefabs/SubMesh.cs:8` | Buffer on a *prefab* (e.g. `NetLanePrefab`). Each element is `Entity m_SubMesh` (a separate mesh-entity in the world). The `Entity` is a fully-baked prefab in its own right — has `MeshData`, `SharedMeshData`, `BatchGroup` buffer. |
| `decomp/Game/Game.Prefabs/MeshData.cs:7` | On the mesh-entity. `m_State : MeshFlags` (incl. `Decal`, `Default`, `Tiling`), `m_DecalLayer`, `m_DefaultLayers / m_AvailableLayers`, `m_SubMeshCount`, `m_TilingCount`. |
| `decomp/Game/Game.Prefabs/SharedMeshData.cs` | On the mesh-entity. `m_Mesh` — the shared mesh entity used for batch grouping (mesh dedup). Set by `ManagedBatchSystem`. |
| `decomp/Game/Game.Prefabs/MeshMaterial.cs` | Buffer on a mesh-entity. For net composition meshes, holds `m_MaterialIndex` per submesh. |
| `decomp/Game/Game.Prefabs/DecalProperties.cs:11` | **Managed `ComponentBase`** attached to `RenderPrefab` (not an ECS component — pure managed). Fields: `m_TextureArea`, `m_RendererPriority`, `m_LayerMask : DecalLayers`, `m_EnableInfoviewColor`. Read by `ManagedBatchSystem.CreateBatch:935` via `renderPrefab.GetComponent<DecalProperties>()`. |
| `decomp/Game/Game.Prefabs/RenderPrefab.cs:14` | **Managed PrefabBase** that owns a `GeometryAsset` and `SurfaceAsset[]`. Calls to `ObtainMeshes / ObtainMaterials` go through the asset database. `GetPrefabComponents` adds `MeshData`, `SharedMeshData`, `BatchGroup` to the prefab archetype. |
| `decomp/Game/Game.Prefabs/NetLaneGeometryPrefab.cs:11` | Adds `LaneGeometry`, `CullingInfo`, `MeshBatch` to the **lane archetype** in `GetArchetypeComponents` (lines 38–40). Without this prefab base, the lane entity cannot enter the rendering pipeline. |
| `decomp/Game/Game.Rendering/InstanceData.cs:5` | Inside `NativeBatchInstances<…>` — `(entity, meshGroup, meshIndex, tileIndex)`. |
| `decomp/Game/Game.Rendering/GroupData.cs:7` | Inside `NativeBatchGroups<…>` — `(Entity mesh, Layer, MeshType, Partition, LodCount, RenderFlags, property indices)`. |
| `decomp/Game/Game.Rendering/BatchData.cs:5` | Per-batch state inside a group: `m_LodMesh`, `m_VTIndex0/1`, `m_SubMeshIndex`, `m_RenderFlags`, etc. |
| `decomp/Game/Game.Rendering/DecalLayers.cs:6` | Layer-mask enum: `Terrain | Roads | Buildings | Vehicles | Creatures | Other`. Drives `colossal_DecalLayerMask` shader uniform. |
| `decomp/Game/Game.Prefabs/MeshFlags.cs:6` | `Decal`, `Default`, `Tiling`, `Base`, `Impostor`, `Animated`, `Character`, `Prop`. `MeshFlags.Decal` is the bit that says "this mesh is a decal" — set by the asset importer based on the source mesh having a decal-shader surface. |

### Systems

| File | Role |
|---|---|
| `decomp/Game/Game.Prefabs/PrefabSystem.cs:88` | `AddPrefab(PrefabBase)` — creates the prefab ECS entity with archetype derived from `GetPrefabComponents`. Adds `Created + Updated`. Public method; mods CAN call this. |
| `decomp/Game/Game.Prefabs/PrefabSystem.cs:306` | `UpdatePrefab(PrefabBase)` — re-bakes the prefab entity (drops the old, creates new), runs `m_UpdateSystem.Update(SystemUpdatePhase.PrefabUpdate)` to refresh derived archetypes (incl. `NetLaneArchetypeData`). Async — queued for next `MainLoop` frame. |
| `decomp/Game/Game.Rendering/RequiredBatchesSystem.cs:25` | The "register prefab to have batch groups" system. Runs an `IJobChunk` over `{ MeshBatch, PrefabRef, (Updated | BatchesUpdated) }`. For each prefab encountered, walks its `SubMesh[]`, calls `m_NativeBatchGroups.CreateGroup(…)` + `CreateBatch(…)`, appends to the **prefab's** `BatchGroup` buffer (line 582). Without this step running, the `BatchGroup` buffer is empty and `BatchInstanceSystem.AddInstance` finds zero matches and skips the entity silently. |
| `decomp/Game/Game.Rendering/ManagedBatchSystem.cs:25` | Material factory. `OnUpdate` (`:588`) iterates `GetUpdatedManagedBatches()`, for each new batch calls `CreateBatch` (`:791`) which: resolves `RenderPrefab` via `m_PrefabSystem.GetPrefab<RenderPrefab>(groupData.m_Mesh)`, loads `SurfaceAsset`, reads `DecalProperties`, constructs `Material` from template + per-batch keywords/textures, calls `HDMaterial.ValidateMaterial(material)` (line 1392). **The `PrefabSystem.GetPrefab<RenderPrefab>` lookup is the choke point — if our mesh entity isn't registered as a `RenderPrefab` in `PrefabSystem`, the batch fails to initialize and never renders.** |
| `decomp/Game/Game.Rendering/ManagedBatchSystemDebugger.cs` | Editor-only telemetry; not in our path. |
| `decomp/Game/Game.Rendering/BatchManagerSystem.cs:25` | Owns the underlying `NativeBatchGroups<…>` + `NativeBatchInstances<…>` + `ManagedBatches<…>`. Schedules `MergeGroupsJob` (line 27) when two prefabs end up with equivalent material+mesh keys — merges them into one BRG draw. Public accessors: `GetNativeBatchGroups`, `GetNativeBatchInstances`. |
| `decomp/Game/Game.Rendering/BatchMeshSystem.cs:22` | Priority-loads `GeometryAsset`s via `m_GeometryLoadingSystem`. `AddBatch` (line 700) tracks the batch in priority list. `LoadMeshes` (line 805) calls `renderPrefab.ObtainMesh(subMeshIdx)`. Requires `RenderPrefab` with a valid `GeometryAsset`. |
| `decomp/Game/Game.Rendering/BatchInstanceSystem.cs:27` | The per-entity dispatcher. `BatchInstanceJob` (line 122) reads `PreCullingData`, looks up `MeshBatch` buffer, walks the **prefab's** `SubMesh[]` and `BatchGroup[]` buffers (`m_PrefabSubMeshes` + `m_PrefabBatchGroups`), calls `AddInstance` (line 894) for each tile. `AddInstance` enqueues `GroupActionData` for `Groups.GroupActionJob` (line 1133) to process — that's the system that calls `groupInstanceUpdater.AddInstance(…)` actually inserting into `NativeBatchInstances<…>`. |
| `decomp/Game/Game.Rendering/BatchRendererSystem.cs:9` | Manages the Unity BRG `BatchID` lifecycle. Calls `managedBatches.AddBatchRenderer(batchPropertyAccessor)` per group. Decoupled from instance / material logic. |
| `decomp/Game/Game.Rendering/PreCullingSystem.cs:25` | Frustum + LOD + dirty-flag tracking. Many sub-jobs. `InitializeCullingJob` (line 357) computes bounds for new entities; `PassedCulling` (line 1514) assigns `CullingInfo.m_CullingIndex` and inserts into `m_CullingData` list. `UpdateCullingJob` (around line 1816) tags `PreCullingData.m_Flags` with `Lane / Net / Object / Zone` based on which component the entity has — the dispatch key used downstream. |
| `decomp/Game/Game.Net/SecondaryLaneSystem.cs:26` | The system that **creates** sublane entities for vanilla markings. Calls `m_CommandBuffer.CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype)` (line 1384). The archetype is read from the marking prefab entity itself, so the sublane entity inherits the full set of components (`Lane, Curve, Owner, PrefabRef, LaneGeometry, CullingInfo, MeshBatch, …`) automatically. Then sets `PrefabRef → marking prefab`. **This is the only place vanilla creates lane entities, and the prefab they reference is registered in `PrefabSystem` (a `NetLaneGeometryPrefab`).** |

## How a vanilla road decal gets rendered (step-by-step)

Example: an `EU Highway Edge Line` instance on a city Small Road segment.

### Stage 0 — game start / asset load

The asset database contains the `EU Highway Edge Line` prefab + its
`Highway Edge Line Mesh` `RenderPrefab` + its `LaneLine_Solid_White` `SurfaceAsset`
+ its `LaneLineSolid.fbx` `GeometryAsset` + its `BH/Decals/CurvedDecalShader.shader`
+ textures. All loaded by Colossal asset pipeline; nothing CS2-specific happens yet.

### Stage 1 — `PrefabSystem.AddPrefab`

When the asset bundle's prefabs get registered, `PrefabSystem.AddPrefab` runs per
prefab. For our `NetLaneGeometryPrefab "EU Highway Edge Line"`:

- `GetPrefabComponents` is called via `prefab.GetComponents().GetPrefabComponents`.
  Critically `NetLaneGeometryPrefab.GetPrefabComponents` (line 24–29) adds
  `NetLaneGeometryData` and `SubMesh`. The base `NetLanePrefab.GetPrefabComponents`
  (line 23–32) adds `NetLaneData` and `NetLaneArchetypeData`.
- `NetLaneGeometryPrefab.GetArchetypeComponents` (line 31–55) — **this is the
  archetype for the LANE INSTANCE entity, not the prefab entity** — adds
  `LaneGeometry`, `CullingInfo`, `MeshBatch`.
- `LateInitialize` (`NetLanePrefab.cs:40`) calls
  `EntityManager.CreateArchetype(…)` 8 times, stores result in
  `NetLaneArchetypeData.m_LaneArchetype` etc. — these archetypes are what
  `SecondaryLaneSystem` later uses to spawn lane entities.

After this, the prefab is a normal ECS entity with `Created + Updated`, archetype
includes `NetLaneData, NetLaneArchetypeData, NetLaneGeometryData, SubMesh, Locked,
UnlockRequirement`.

Each `m_Meshes[i].m_Mesh` (a `RenderPrefab` "Highway Edge Line Mesh") is also added
by the same `AddPrefab` chain (because `NetLaneGeometryPrefab.GetDependencies` line
15–22 yields them). `RenderPrefab.GetPrefabComponents` (line 265–275) adds `MeshData`,
`SharedMeshData`, `BatchGroup` to the mesh prefab's archetype.

Importantly **the `BatchGroup` buffer is empty at this point.** It is reserved by
archetype but contains zero elements.

### Stage 2 — first lane instance gets created

Somewhere a road edge gets built. `SecondaryLaneSystem.UpdateLanes` runs, scans the
edge's `NetCompositionLane.m_LaneMaterials` and finds an entry referencing our
`EU Highway Edge Line` prefab. At `SecondaryLaneSystem.cs:1384`:

```cs
NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[component.m_Prefab];
Entity e = m_CommandBuffer.CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype);
m_CommandBuffer.SetComponent(jobIndex, e, component);   // PrefabRef
m_CommandBuffer.SetComponent(jobIndex, e, lane);        // Lane (PathNodes)
m_CommandBuffer.SetComponent(jobIndex, e, curveData);   // Curve (Bezier + length)
m_CommandBuffer.AddComponent(jobIndex, e, component2);  // Owner
m_CommandBuffer.AddComponent(jobIndex, e, component3);  // Elevation
…
```

The archetype guarantees the new entity has `Lane, Curve, PrefabRef,
NetLaneArchetypeData, NetLaneGeometryData, LaneGeometry, CullingInfo, MeshBatch,
Created, Updated, Owner, Elevation, …` (whatever the prefab's `GetArchetypeComponents`
+ user-added types accumulated).

### Stage 3 — `RequiredBatchesSystem` first-touch

`RequiredBatchesSystem.OnUpdate` (line 928) runs over entities with
`{ MeshBatch, PrefabRef, (Updated | BatchesUpdated) }`. Our new lane entity matches.

`UpdateLaneBatches` (line 313):

- Reads the prefab's `SubMesh[]` buffer (which holds **one entry per mesh**, e.g.
  the `Highway Edge Line Mesh` entity).
- For each `subMesh.m_SubMesh`, reads its `MeshData` and current
  `m_AvailableLayers / m_AvailableTypes`.
- Computes which `(layer, meshType)` tuples are **new** (not yet in the mesh
  entity's `BatchGroup` buffer).
- For each new tuple calls `InitializeBatchGroup(mesh, layer, MeshType.Lane,
  partition)` (line 482).

`InitializeBatchGroup` (line 482):

- Reads `MeshData` for bounds, sub-mesh count, LOD bias, etc.
- Calls `m_NativeBatchGroups.CreateGroup(groupData, batchCount, …)` →
  returns `groupIndex`. This is the **first time the group exists.**
- Appends `BatchGroup{ m_GroupIndex, m_MergeIndex=-1, m_Layer, m_Type, m_Partition }`
  to the **mesh entity's** `BatchGroup` buffer (line 582–589).
- For each LOD and sub-mesh, calls `m_NativeBatchGroups.CreateBatch(batchData,
  groupIndex, …)`. This allocates an empty native batch (no material yet).

After this, `NativeBatchGroups` has a fresh group whose `GroupData.m_Mesh` is our
mesh-entity, and the mesh-entity's `BatchGroup` buffer has the matching entry.

### Stage 4 — `ManagedBatchSystem` materializes the batch

`ManagedBatchSystem.OnUpdate` (line 588) drains `nativeBatchGroups.GetUpdatedManagedBatches()`.
For each updated group:

- `m_PrefabSystem.TryGetPrefab<RenderPrefab>(groupData.m_Mesh, out var prefab)`.
  If this returns `false`, the entire group is skipped (`groupKey` stays null).
- `MeshData componentData = base.EntityManager.GetComponentData<MeshData>(...)`.
- For each batch in the group, calls `CreateBatch(groupIndex, batchIdx, …)` line 791.

In `CreateBatch` for `groupData.m_MeshType == MeshType.Lane` (line 987–989):

```cs
lodFadeData = m_BatchManagerSystem.GetPropertyData(((batchData.m_LodIndex & 1) == 0)
    ? LaneProperty.LodFade0
    : LaneProperty.LodFade1);
```

Then:

- `surfaceAsset = renderPrefab.GetSurfaceAsset(num2)` (line 981).
- `surfaceAsset.LoadProperties(useVT: true)` (line 991) — pulls template material,
  floats, ints, vectors, colors, textures, keywords from the surface asset.
- `materialKey.Initialize(surfaceAsset)` (line 992) — `template = surface.GetTemplateMaterial()`.
- `DecalProperties decalProperties = renderPrefab.GetComponent<DecalProperties>()` (line 935).
- If non-null (it IS non-null for edge lines):
  - `materialKey.renderQueue = template.shader.renderQueue + decalProperties.m_RendererPriority`.
  - `materialPropertyBlock.SetVector(MaterialProperty.TextureArea, decalProperties.m_TextureArea)`.
  - `materialKey.decalLayerMask = (int)decalProperties.m_LayerMask`.
  - `materialPropertyBlock.SetFloat(MaterialProperty.SmoothingDistance, meshData.m_SmoothingDistance)`.
  - `shadowCastingMode = ShadowCastingMode.Off` (line 1176).
- Mesh = `m_BatchMeshSystem.GetDefaultMesh(groupData.m_MeshType, batchFlags, generatedType)`
  (line 1231) — initially a placeholder.
- `value = CreateMaterial(surfaceAsset, material, materialKey)` (line 1239) →
  `new Material(template) + property/keyword copy + HDMaterial.ValidateMaterial(material)`
  (line 1392).
- `customBatch = new CustomBatch(groupIndex, batchIdx, surfaceAsset, …, value, …, batchFlags, …)`
  returned to caller.

Back in `OnUpdate`:

- `managedBatches.AddBatch(customBatch, …)` registers it.
- `m_BatchMeshSystem.AddBatch(customBatch, num2)` queues geometry-asset loading.
- `managedBatches.SetDefaults(template, surfaceAsset.floats / ints / vectors / colors,
  customBatch.customProps, batchProperties, batchDefaultsAccessor)` — uploads
  per-instance defaults to the GPU instance buffer.

### Stage 5 — `BatchMeshSystem` actually uploads the mesh

`BatchMeshSystem.OnUpdate` periodically processes `m_LoadingData`:

- Picks high-priority batches (those with currently-visible instances).
- Calls `renderPrefab.ObtainMesh(customBatch.sourceSubMeshIndex, out subMeshIndex)`
  (line 1158, 1237, 1282) — this loads the `GeometryAsset` from disk if needed.
- Generates `Mesh.MeshData` (vertices, normals, tangents, UV, indices) into the
  shared `MeshDataArray`.
- Allocates a slot in `m_ShapeAllocator` / `m_CullAllocator` graphics buffers, copies
  the vertex / cull data over.

### Stage 6 — `PreCullingSystem` registers the instance

Separately, when the lane entity is created with `CullingInfo` (uninitialized,
`m_CullingIndex == 0`), `PreCullingSystem.UpdateCullingDataJob` runs an
`InitializeCullingJob` that for lane entities computes:

```cs
NetLaneGeometryData netLaneGeometryData = m_PrefabLaneGeometryData[prefabRef3.m_Prefab];
reference5.m_Bounds = MathUtils.Expand(MathUtils.Bounds(curve.m_Bezier),
                                       netLaneGeometryData.m_Size.xyx * 0.5f);
reference5.m_Radius = 0f;
reference5.m_Mask = BoundsMask.Debug | <layer-derived mask>;
reference5.m_MinLod = (byte)netLaneGeometryData.m_MinLod;
```

(PreCullingSystem.cs:705–724.) This requires `m_PrefabLaneGeometryData.HasComponent(prefab)`,
i.e. **the lane prefab must be a `NetLaneGeometryPrefab`** (which has the
`NetLaneGeometryData` component).

`PreCullingSystem.PassedCulling` (line 1514) is called from a parallel-for over
visibility-test results. When the instance first passes culling, it assigns
`m_CullingIndex = Interlocked.Increment(...) - 1` and writes a fresh
`PreCullingData` into the list, with `m_Flags = PassedCulling | NearCamera |
NearCameraUpdated`. `UpdateCullingJob` further tags it with `PreCullingFlags.Lane`
because `m_LaneData.HasComponent(reference.m_Entity)` is true (PreCullingSystem.cs:1816).

### Stage 7 — `BatchInstanceSystem` instantiates into the batch

`BatchInstanceSystem.OnUpdate` (line 1466) schedules `BatchInstanceJob` over
`m_PreCullingSystem.GetUpdatedData()`. For each `PreCullingData` with
`PreCullingFlags.Lane`:

`UpdateLaneInstances` (line 677):

- `m_MeshBatches.TryGetBuffer(cullingData.m_Entity, out var bufferData)`. If our
  entity has no `MeshBatch` buffer → return (skipped silently).
- `curve = m_CurveData[entity]`; `prefabRef = m_PrefabRefData[entity]`.
- `m_PrefabSubMeshes.TryGetBuffer(prefabRef.m_Prefab, out var subMeshes)` — reads
  the **prefab's** `SubMesh[]`.
- For each `subMesh` not excluded by `subMeshFlags`:
  - `meshData = m_PrefabMeshData[subMesh.m_SubMesh]`.
  - `batchGroups = m_PrefabBatchGroups[subMesh.m_SubMesh]` — reads the
    **mesh-entity's** `BatchGroup` buffer (populated by `RequiredBatchesSystem` in
    stage 3).
  - `tileCount = BatchDataHelpers.GetTileCount(curve, …)`.
  - `AddInstance(ptr, oldBatchCount, bufferData, batchGroups, meshLayer, MeshType.Lane,
    minLod, fadeIn, entity, 0, subMeshIdx, tileCount, hasMeshMatches)` (line 821).

`AddInstance` (line 894):

```cs
for (int i = 0; i < batchGroups.Length; i++)
{
    BatchGroup bg = batchGroups[i];
    if ((bg.m_Layer & layers) == 0 || bg.m_Type != type || bg.m_Partition != partition)
        continue;
    for (int j = 0; j < tileCount; j++)
    {
        …
        m_GroupActionQueue.Enqueue(new GroupActionData
        {
            m_GroupIndex = bg.m_GroupIndex,
            m_AddInstanceData = new InstanceData { m_Entity = entity, … },
            m_FadeIn = fadeIn,
        });
        meshBatches.Add(new MeshBatch { m_GroupIndex = bg.m_GroupIndex,
                                        m_InstanceIndex = -1, … });
    }
}
```

→ enqueues GroupActionData for the post-job `GroupActionJob` to actually call
`groupInstanceUpdater.AddInstance(…)` (line 1193). Result: the entity is now in
`NativeBatchInstances`, ready to be culled and drawn.

### Stage 8 — frame draw

Unity's BRG culling callback fires per frame and per camera. `BatchInstanceSystem`
(via `AllocateCullingJob` in `BatchManagerSystem.cs:262`) builds
`BatchCullingOutputDrawCommands` from `NativeBatchInstances`. The GPU executes the
draws, with instances batched per material+mesh.

## Required components for an entity to enter the decal batch

For a lane-style entity (which is the closest match to what we want for ribbon
markings):

### On the instance entity (lane sublane)

| Component | Required by | Source in vanilla |
|---|---|---|
| `Lane` | `PreCullingSystem.UpdateCullingJob:1816` for dispatch flag | Archetype from `NetLanePrefab.LateInitialize` |
| `Curve` | `BatchInstanceSystem.UpdateLaneInstances:696`; `PreCullingSystem:703` | Set by `SecondaryLaneSystem:1391` |
| `PrefabRef` | Everywhere: `m_PrefabRefData` lookups | Set by `SecondaryLaneSystem:1389` |
| `LaneGeometry` (zero-size tag) | `SecondaryLaneSystem:390` (flag3 check); archetype trigger | From `NetLaneGeometryPrefab.GetArchetypeComponents:38` |
| `CullingInfo` | `PreCullingSystem` to assign `m_CullingIndex`; `BatchInstanceSystem:439` for mask check | From `NetLaneGeometryPrefab.GetArchetypeComponents:39` |
| `MeshBatch` (buffer) | `BatchInstanceSystem:564,677` (the `if (!m_MeshBatches.TryGetBuffer(...)) return;` gate) | From `NetLaneGeometryPrefab.GetArchetypeComponents:40` |
| `Owner` | Optional. Used by `SearchSystem.GetLayers` for tunnel detection; required if you want shadow-casting / Tunnel layer | Set by `SecondaryLaneSystem:1392` (AddComponent) |
| `Elevation` (`Game.Net`) | Optional, for elevation-aware rendering | Set by `SecondaryLaneSystem:1393` |
| `Updated` (tag) | `RequiredBatchesSystem.m_UpdatedQuery:903`; `PreCullingSystem.m_InitializeQuery` | Created tag (vanilla edges add this) |
| `Created` (tag) | Same | Same |
| `EdgeLane / NodeLane / SlaveLane / MasterLane / AreaLane` | One must be present per `NetLaneArchetypeData` archetype (which one depends on parent topology) | Selected in `SecondaryLaneSystem` based on whether the sublane lives on edge / node |

### On the prefab entity (the marking definition)

| Component / property | Required by |
|---|---|
| `NetLaneData` | Prefab base components for any `NetLanePrefab` |
| `NetLaneArchetypeData` | Holds the 8 baked `EntityArchetype`s; `SecondaryLaneSystem:1383` reads it |
| `NetLaneGeometryData` | `PreCullingSystem:707`, `BatchInstanceSystem` indirectly. Provides `m_Size`, `m_MinLod`, `m_GameLayers`, `m_EditorLayers` |
| `SubMesh` buffer | `BatchInstanceSystem:441,704`; `RequiredBatchesSystem:172,331`. Each element points to a separate mesh-entity prefab |

### On each mesh-entity (the `subMesh.m_SubMesh`)

| Component / property | Required by |
|---|---|
| `MeshData` | Read in `BatchInstanceSystem:526,761`, `RequiredBatchesSystem:225,362`, `ManagedBatchSystem:606,917` |
| `SharedMeshData` | Read+written in `ManagedBatchSystem:918,933,934` (mesh deduplication) |
| `BatchGroup` buffer | Read in `BatchInstanceSystem:527,665,762,845`; written by `RequiredBatchesSystem:549,582`. **Initially empty; populated by `RequiredBatchesSystem` on first use.** |
| `RenderPrefab` (managed PrefabBase in `PrefabSystem`) | `ManagedBatchSystem.CreateBatch:916,931,939,958` calls `m_PrefabSystem.GetPrefab<RenderPrefab>(entity)`. **THE choke point** — if `TryGetPrefab` fails, the batch silently fails to initialize. |
| `DecalProperties` (managed `ComponentBase` on `RenderPrefab`) | `ManagedBatchSystem:935`. Controls decal-shader behaviour. Optional. |
| `GeometryAsset` (`renderPrefab.geometryAsset`) | `BatchMeshSystem.LoadMeshes:805,936`. Required to upload the mesh. |
| `SurfaceAsset[]` (`renderPrefab.GetSurfaceAsset(i)`) | `ManagedBatchSystem.CreateBatch:981,991`. Required for material+shader+template. |

### Cross-cutting

- `MeshBatch` buffer on instance entity is **the gate** for
  `BatchInstanceSystem.UpdateLaneInstances` (`if (!m_MeshBatches.TryGetBuffer(...))
  return;`).
- `BatchGroup` buffer on mesh prefab entity is **the gate** for
  `BatchInstanceSystem.AddInstance` finding a matching group — it iterates this
  buffer and only inserts if `(bg.m_Layer & layers) != 0 && bg.m_Type == type &&
  bg.m_Partition == partition`. If the buffer is empty, no insert happens (silent).
- `PrefabSystem.GetPrefab<RenderPrefab>(meshEntity)` is **the gate** for material
  initialization in `ManagedBatchSystem`. If it returns null, batch creation aborts
  silently.

## How material/shader is resolved

`ManagedBatchSystem.CreateBatch` (line 791) does the resolution. The full chain for
a lane-decal batch:

1. `entity = batchData.m_LodMesh != Entity.Null ? batchData.m_LodMesh : groupData.m_Mesh`
   (line 825). The mesh-entity ECS reference.
2. `renderPrefab = m_PrefabSystem.GetPrefab<RenderPrefab>(entity)` (line 916). **If
   the entity is not a registered `RenderPrefab`, throws or returns null; control
   does NOT fall through to a default — the whole batch silently fails to create.**
3. `surfaceAsset = renderPrefab.GetSurfaceAsset(subMeshIndex)` (line 981).
4. `surfaceAsset.LoadProperties(useVT: true)` (line 991) — pulls these from the
   asset database:
   - `floats / ints / vectors / colors` dicts
   - `keywords` HashSet
   - `textures` dict (key = property name, value = `TextureAsset`)
   - VT atlassing info
5. `materialKey.Initialize(surfaceAsset)` (line 992) — calls
   `template = surface.GetTemplateMaterial()` which is provided by the `SurfaceAsset`
   from the asset bundle. **This is where the `BH/Decals/DefaultDecalShader` shader
   reference comes from** — it was set in Unity by whoever authored the
   `LaneLine_Solid_White` surface asset and serialized into the asset's metadata.
   The shader itself is compiled into the game's HDRP shader bundle at game build
   time; we cannot register a new shader at runtime.
6. `if (decalProperties != null) { /* set decal-specific MPB props */ }` (line 1015).
7. `value = CreateMaterial(surfaceAsset, sourceMaterial, materialKey)` (line 1239).
8. In `CreateMaterial` (line 1351):
   - `material = new Material(materialKey.template)` (clones the surface's template
     material — so we get the right shader for free).
   - Sets all `floats / ints / vectors / colors / textures / keywords` from surface.
   - `material.SetFloat(m_DecalLayerMask, math.asfloat(materialKey.decalLayerMask))`
     (line 1408).
   - `HDMaterial.ValidateMaterial(material)` (line 1392) — **mandatory HDRP step,
     derives shader-feature keywords from material properties; without it every
     pass draws zero pixels (no errors).**

So the shader is **already compiled and bundled** with the game. We do not call
`Shader.Find("BH/Decals/CurvedDecalShader")` at runtime; the surface asset's
template material holds a serialized reference to the shader object that lives in
the game's main shader bundle.

A mod can in principle do `Shader.Find("BH/Decals/CurvedDecalShader")` — Unity will
return it if loaded — but that shader expects per-batch shader uniforms that
`ManagedBatchSystem` sets up through the **GPU instance buffer** (not via
MaterialPropertyBlock). Driving it manually from `Graphics.DrawMesh` would require
reproducing the `BatchManagerSystem.SetDefaults` upload logic, plus per-instance
property uploads through `NativeBatchInstances` → not feasible.

## Could a mod inject custom mesh into this pipeline? Feasibility verdict

**No, not for runtime-generated per-instance meshes (our Bezier ribbons).**

**Yes, but indirectly — only for STATIC mesh assets registered as a `RenderPrefab`.**

### Why "no" for our actual use case

Our markings are **runtime-generated geometry that changes shape per node-pair**
(start position, end position, two tangents → cubic Bezier ribbon mesh). The
vanilla pipeline is built on `RenderPrefab`s that are:

- **Authored as asset files** at build time. `RenderPrefab.geometryAsset` is an
  `AssetReference<GeometryAsset>` — an `Hash128` GUID lookup into the asset
  database, populated at import. No public API to construct a `GeometryAsset` from
  a runtime `UnityEngine.Mesh`.
- **One shared mesh per prefab.** `BatchMeshSystem` uses `MeshKey` (asset GUID +
  flags) to dedup; `m_MeshInfos` is keyed by the mesh ENTITY. There is no concept
  of "many instances with their own per-instance mesh."
- **Loaded once via `renderPrefab.ObtainMesh()`** which goes through
  `GeometryAsset.ObtainMeshes()` — that's a managed `Mesh[]` cached in the asset.
  Even if we monkey-patched it to return our procedural mesh, all instances of
  that prefab would share that one mesh — defeating per-pair geometry.

### What would have to be true for it to work

To inject a custom mesh per pair, we would need:

1. A new `RenderPrefab` per pair (or one with a runtime-overridable mesh).
2. A `GeometryAsset`-implementing object that wraps a runtime `Mesh`. This is
   internal: `GeometryAsset` is `sealed class` in `Colossal.IO.AssetDatabase` with
   no public constructor accepting a `Mesh`.
3. A `SurfaceAsset` for the material; surface assets are similarly file-backed.
4. Registration via `PrefabSystem.AddPrefab(myRenderPrefab)` → forces a re-bake of
   `RequiredBatchesSystem` over the new entity to populate `BatchGroup`.
5. A custom lane (or object) prefab whose `SubMesh[]` points to our mesh-entity.
6. Sublane creation via `SecondaryLaneSystem`-style ECB writes pointing at the
   custom prefab.

Step 2 is the blocker. Without ability to construct a `GeometryAsset` from a
runtime `Mesh`, `BatchMeshSystem.LoadMeshes` cannot upload our procedural mesh.

### What CAN be done with the pipeline

For a **fixed-style marking** (e.g. a single repeating "white solid" decal tiled
along a curve), the vanilla pipeline works because the mesh itself is a 1-meter
straight strip and `BatchDataHelpers.GetTileCount` repeats it along the curve via
GPU instancing with per-instance Curve uniform (the `colossal_GeometryTiling`
keyword + `Curve` shader inputs). That's how `EU Highway Edge Line` works on a
curved road — one straight-strip mesh, tiled by the curved-decal shader.

So an alternative approach is: **don't render the pair as a unique mesh — register
a vanilla-style straight-strip mesh + curved-decal shader, then create a lane
sublane entity per pair with a `Curve` component holding the pair's Bezier.** The
vanilla pipeline then tiles + bends the mesh along the Bezier exactly like it does
for `EU Highway Edge Line` on a city road. That is in fact exactly what Layer 1
(`EdgeLineCloneSystem` / `ParkingLineCloneSystem`) already does for vanilla
SecondaryLanes — and what the abandoned Phase 4 Step 2 (`MarkingPairEmissionSystem`)
attempted for per-node user pairs.

The previous attempt failed because the sublane entity was created from the lane
prefab's archetype but **`SecondaryLaneSystem.UpdateNodes` was never told it
exists**, so the mesh-prefab entity's `BatchGroup` buffer never had a Lane-typed
group initialized for it (vanilla's first call into `RequiredBatchesSystem` for a
given mesh+layer+type only happens when a lane entity with that mesh first arrives
through a vanilla code path — see analysis in next section).

## If yes — minimum code skeleton

For the "register-a-RenderPrefab-and-spawn-sublanes" path (the only viable one if
we accept fixed style + curved-decal tiling, not per-pair custom mesh shape):

```cs
// Done once at mod init (in PrefabUpdate phase)
public void RegisterMarkingPrefab()
{
    // Approach A — clone an existing prefab (proven, this is Layer 1):
    var src = m_PrefabSystem.GetPrefab<NetLanePrefab>(srcEntity);          // EU Highway Edge Line
    var clone = m_PrefabSystem.DuplicatePrefab(src, "TownRoadLane …");
    // Optional: m_PrefabSystem.UpdatePrefab(clone) to apply mesh swap, etc.
    // The clone inherits all SubMesh + RenderPrefab assets from src — fully wired.

    // Approach B — bake from scratch:
    //   NOT VIABLE — see "If no" section. No public API to construct GeometryAsset
    //   from a runtime Mesh, no public API to construct SurfaceAsset from a
    //   runtime Material.
}

// Per pair, spawn a sublane entity (the part Phase 4 Step 2 attempted)
public void SpawnPairSublane(EntityCommandBuffer ecb, Entity ownerNode,
                              Entity markingPrefab, Bezier4x3 bez)
{
    var arch = EntityManager.GetComponentData<NetLaneArchetypeData>(markingPrefab)
                            .m_NodeLaneArchetype;   // or EdgeLane / NodeSlave / etc.
    var e = ecb.CreateEntity(arch);
    ecb.SetComponent(e, new PrefabRef(markingPrefab));
    ecb.SetComponent(e, new Curve { m_Bezier = bez, m_Length = MathUtils.Length(bez) });
    ecb.SetComponent(e, new Lane { /* PathNode start/middle/end */ });
    ecb.AddComponent(e, new Owner { m_Owner = ownerNode });
    ecb.AddComponent(e, default(Elevation));
    ecb.AddComponent(e, default(Updated));     // ← triggers RequiredBatchesSystem
    ecb.AddComponent(e, default(Created));     // ← triggers PreCullingSystem init
    // The archetype guarantees Lane, Curve, PrefabRef, LaneGeometry,
    // CullingInfo, MeshBatch are present.
}
```

That much we already tried. **It still doesn't render**, which leads us to the
unresolved blocker:

## If no — why exactly

There are two blockers:

### Blocker 1 — `RenderPrefab` cannot be constructed from a runtime mesh

`RenderPrefab` is a managed `PrefabBase` whose payload is `AssetReference<GeometryAsset>`
+ `AssetReference<SurfaceAsset>[]`. Both `GeometryAsset` and `SurfaceAsset` are
`sealed` classes in `Colossal.IO.AssetDatabase` whose public surface only allows
loading from disk (`AssetDatabase.AddAsset(path, …)`) or referencing an existing
asset by GUID. No public API to wrap a `UnityEngine.Mesh` / `UnityEngine.Material`.

Workarounds (all hacky):

- **Reflection** — `GeometryAsset` likely has private fields holding the actual
  `Mesh[]`; we could override them. Brittle across game patches.
- **Inject into the asset database at runtime** — write a fake on-disk asset and
  trigger a database reload. Not viable at gameplay time (asset DB rebuild blocks
  the world).
- **Patch `RenderPrefab.ObtainMesh` via Harmony** — intercept the call and return
  our procedural mesh. Works for the `BatchMeshSystem` upload path but doesn't fix
  `MeshData.m_Bounds` and other metadata read elsewhere. And in any case, all
  pair instances would share the same prefab → same mesh → can't be per-pair.

### Blocker 2 — Phase 4 Step 2 ECS-sublane approach: where it actually fails

We have a working `EdgeLineCloneSystem` (Layer 1) — it clones a `NetLaneGeometryPrefab`,
registers it via `PrefabSystem`, and vanilla `SecondaryLaneSystem` spawns instances
for us. These render correctly.

We tried `MarkingPairEmissionSystem` (Phase 4 Step 2) — same clone prefab, but
sublanes spawned by us with `ecb.CreateEntity(arch)` from `NetLaneArchetypeData.m_LaneArchetype`.
Components set: `PrefabRef, Lane, Curve, Owner, Elevation, Created, Updated`.
**Result: no visual.**

Looking at the pipeline carefully (now that we have it documented), the failure
mode is **not** what `IMPLEMENTATION_PLAN.md` claims ("vanilla `BatchInstanceSystem`
requires `PrefabSystem`-baked batch-group registrations that hand-created entities
don't inherit"). The plan misdiagnosed.

The actual failure mode is more subtle. Reading `RequiredBatchesSystem.m_UpdatedQuery`
(line 896–908):

```cs
m_UpdatedQuery = GetEntityQuery(new EntityQueryDesc
{
    All = new ComponentType[2] {
        ComponentType.ReadOnly<MeshBatch>(),
        ComponentType.ReadOnly<PrefabRef>()
    },
    Any = new ComponentType[2] {
        ComponentType.ReadOnly<Updated>(),
        ComponentType.ReadOnly<BatchesUpdated>()
    }
});
```

Our entity has `MeshBatch` (from archetype), `PrefabRef`, `Updated` → it matches.
So `RequiredBatchesSystem` SHOULD process our entity, walk
`m_PrefabSubMeshes[markingPrefab]`, and call `InitializeBatchGroup` for each
sub-mesh+layer combo.

Then `BatchInstanceSystem.UpdateLaneInstances` (which is what runs for
`PreCullingFlags.Lane`) reads `prefabRef = m_PrefabRefData[entity]`, then
`m_PrefabSubMeshes.TryGetBuffer(prefabRef.m_Prefab, …)`. Our prefab has SubMesh.
Then for each subMesh it reads `m_PrefabBatchGroups[subMesh.m_SubMesh]` — which
should be populated by `RequiredBatchesSystem` from above.

So in theory it should work. But there are several **hidden requirements** the
plan missed:

1. **`PreCullingSystem.InitializeCullingJob` needs `m_LaneType` to dispatch**
   correctly. The job uses a `ComponentTypeHandle<Lane>` (or `m_LaneData.HasComponent`
   in UpdateCullingJob:1816). Our entity has `Lane` from the archetype. ✓
2. **`PreCullingSystem` needs to compute valid bounds for the entity.** At line 705
   it requires `m_PrefabLaneGeometryData.HasComponent(prefabRef3.m_Prefab)` — our
   prefab is `NetLaneGeometryPrefab` so this should be true. ✓
3. **`Curve.m_Length > 0.1f` gate** in `BatchInstanceSystem.UpdateLaneInstances`
   (line 704). Our `Curve.m_Length` is computed from the Bezier. ✓
4. **`subMeshFlags` filter** at line 757 — if the prefab's `SubMesh[0].m_Flags`
   includes `SubMeshFlags.RequireSafe / RequireLevelCrossing / RequireEditor` etc.
   that aren't satisfied, the submesh is skipped. **The vanilla `EU Highway Edge
   Line` has `RequireSafe`** (because the marking only appears on safe sections).
   For our sublane entity (which we don't tag with the corresponding flag-clearing
   data), this might fail.
5. **`PreCullingFlags.NearCamera` must be set** for `BatchInstanceSystem` to
   dispatch into UpdateLaneInstances. `PreCullingSystem.PassedCulling` only sets
   this after a successful frustum + bounds-mask check. If our bounds are
   inappropriate (e.g. zero-radius and outside `BoundsMask` for the current
   visibility settings), the entity never passes culling.
6. **`Owner.m_Owner` matters.** `BatchInstanceSystem.UpdateLaneInstances:721` calls
   `IsNetOwnerTunnel(componentData)`. We set `Owner.m_Owner = node`, but vanilla
   sublanes usually have `Owner.m_Owner = edge`. This affects layer selection but
   shouldn't kill rendering.
7. **`PathNode slots in Lane`.** Our `idxBase = 32768 + pairIndex * 4` puts us in
   secondary-node range. `SecondaryLaneSystem`'s `m_OldLanes` reconciliation uses
   `Lane` as part of the dedup key — if our node also gets touched by vanilla's
   sublane GC pass, it might decide our entity is a stale sublane and delete it.

Of these, **(4) `SubMeshFlags.RequireSafe`** is the most likely culprit. The flag
gate at line 757 is `if ((subMesh.m_Flags & subMeshFlags2) != 0) continue;`. The
`subMeshFlags2` for a lane is set to `SubMeshFlags.RequireEditor` (if not in
editor) plus left/right-hand-traffic flags. If the prefab's submesh has
`RequireSafe`, our default `subMeshFlags2 = 0` means the gate becomes
`subMesh.m_Flags & 0 != 0` → false → submesh kept. So that's not it.

The actually-suspicious line is `BatchInstanceSystem:704`:

```cs
if (curve.m_Length > 0.1f && m_PrefabSubMeshes.TryGetBuffer(prefabRef.m_Prefab, …))
```

Our prefab IS a `NetLaneGeometryPrefab` → has `SubMesh`. ✓

So the real reason MIGHT be: **`PreCullingSystem.PassedCulling` simply never fires
for our entity** because the culling frustum / bounds check fails. The
`InitializeCullingJob` at PreCullingSystem.cs:680 computes bounds from
`MathUtils.Bounds(curve.m_Bezier)`. If our Bezier is degenerate, that's empty,
and our entity is invisible.

OR — and this is the most likely explanation — `RequiredBatchesSystem` doesn't
actually populate `BatchGroup` for our mesh because vanilla never spawns this
prefab's submesh on a Lane entity-type before our sublane comes along. Read
`RequiredBatchesSystem.UpdateLaneBatches:383`:

```cs
MeshLayer meshLayer4 = (MeshLayer)((uint)meshLayer3 & (uint)(ushort)(~(int)value.m_AvailableLayers));
MeshType meshType = (MeshType)(4 & (ushort)(~(int)value.m_AvailableTypes));
if (meshLayer4 != 0 || meshType != 0)
{
    value.m_AvailableLayers |= meshLayer4;
    value.m_AvailableTypes |= meshType;
    m_PrefabMeshData[subMesh.m_SubMesh] = value;
    InitializeBatchGroups(subMesh.m_SubMesh, meshLayer4, meshType, …);
    …
}
```

`meshType = 4 & (~m_AvailableTypes)` — `4` is `MeshType.Lane`. If
`m_AvailableTypes` already has `Lane`, this is 0 and `InitializeBatchGroups` is
skipped. The check `if (meshLayer4 != 0 || meshType != 0)` only triggers when this
is the FIRST time we see a particular (mesh, type) combo.

So in theory, on the very first frame after our sublane is created, this should
trigger — assuming our entity passed the `m_UpdatedQuery`. It DOES pass the query
because we add `Updated` to it.

**Hypothesis (most likely actual cause):** the entity's `MeshBatch` buffer never
sees `BatchGroup` entries because the `RequiredBatchesSystem` job runs **before**
our sublane creation in the same frame, due to system ordering. The vanilla phase
is `Modification1` → `Modification4B` → `Rendering`. `RequiredBatchesSystem` lives
in the Rendering phase but reads through chunks via an EntityQuery — if our
sublane is created in `Modification1`, by the time Rendering runs it should be
visible. But the entity has `Created` tag (which adds it to special creation
queues) AND we add `Updated` — the interplay can leave the entity in a state
where `RequiredBatchesSystem` sees it in the same frame as it's created, before
`PreCullingSystem.InitializeCullingJob` has computed bounds. Then `BatchInstanceSystem`
runs against `PreCullingData` that has the entity's culling-index still at 0,
which would skip it.

**Without a build-time experiment we can't pin the exact cause.** What is certain:
the path is **NOT** "PrefabSystem doesn't allow this." The path is "subtle race
conditions and missing flag bits in the sublane archetype that we'd need a debug
build of `BatchInstanceSystem` to diagnose."

## Comparison with our previous attempts

| Attempt | Approach | Outcome | Likely root cause |
|---|---|---|---|
| Layer 1 (`EdgeLineCloneSystem`) | Clone vanilla prefab, let `SecondaryLaneSystem` spawn instances | **Works** | Vanilla's own code spawns the entities — all flags / Owner / PathNode slots / Lane archetype combinations are exactly right. |
| Phase 4 Step 2 (`MarkingPairEmissionSystem`) | Same clone prefab, mod spawns sublane entities via `ecb.CreateEntity(arch)` | **Silent no-op** | Most likely a combination of: (a) wrong `EdgeLane`/`NodeLane` tag (we use `m_LaneArchetype` not `m_NodeLaneArchetype`), (b) `Owner.m_Owner = node` while vanilla uses `Owner.m_Owner = edge`, (c) culling bounds computed before our entity has `Curve` set in its first frame because `Created` + ECB defers, (d) the prefab's mesh-entity `BatchGroup` buffer may have been initialized for `MeshType.Lane` by vanilla's Layer 1 instances but with `m_Partition` value (e.g. partition derived from `meshData.m_MinLod`) that doesn't match what our entity's `partition` resolves to in `UpdateLaneInstances:820` — causing the loop in `AddInstance:899` to find no matching `BatchGroup`. **None of these are "PrefabSystem fundamentally blocks us"**; all are fixable with patient diagnostic logging. |
| Current Phase 4 Step 3 (`MarkingMeshRenderSystem`) | Custom mesh + `Graphics.DrawMesh` + `HDRP/Unlit` material | Renders but low quality (no proper decal projection, no shadow interaction, texture sampling crude) | Outside vanilla pipeline entirely. Loses decal-shader benefits. |

The key insight: **Layer 1 proves that prefabs registered through `DuplicatePrefab`
do enter the vanilla pipeline correctly.** Our ECS-sublane attempt failed not
because of a fundamental architectural barrier but because of subtle component /
tag / flag / ordering mismatches we never fully isolated.

## Recommendation

**Three viable paths, in order of recommended preference:**

### Path A (best for quality, hardest) — fix Phase 4 Step 2 (ECS-sublane spawn) with patient diagnostics

The vanilla pipeline IS reachable; we abandoned the path on a misdiagnosis. To
revisit, add a one-shot diagnostic system that:

1. Watches our spawned sublane through 60 frames.
2. Each frame, logs: `MeshBatch.Length`, `CullingInfo.m_CullingIndex`,
   `prefab.BatchGroup.Length`, `m_AvailableLayers`/`m_AvailableTypes` on the mesh
   prefab entity, `PreCullingData.m_Flags` (if reachable via reflection on
   `PreCullingSystem`).
3. Compare frame-by-frame with the same data for a vanilla edge-line sublane
   spawned by `SecondaryLaneSystem`.

The diff will surface the exact missing piece (most likely an `EdgeLane` /
`NodeLane` tag, an `Owner` that should be an edge not a node, or a `Lane.m_StartNode`
PathNode-flag bit). Once isolated, the fix is 1–2 lines.

If this works, we get **vanilla-quality decal rendering for free** — correct
projection, depth fade, decal layers, virtual texturing, LODs, the lot. Plus
G87 mesh support works the same as Layer 1.

**Cost:** estimated 1–2 sessions of debugging. High payoff.

**Limitation:** the rendered marking will follow the **prefab's mesh shape**,
tiled along our Bezier curve. Style is fixed per-prefab (one mesh = one style).
Per-pair custom shapes (e.g. variable width, arrows, custom symbols) are NOT
achievable this way — only tiled curves of pre-baked mesh assets.

### Path B (best ergonomics for "per-pair custom shape", current direction) — stay with `MarkingMeshRenderSystem` and improve material

Current PoC works. To improve visual quality without abandoning custom-mesh:

1. **Use vanilla decal shader on a flat ribbon.** `Shader.Find("BH/Decals/DefaultDecalShader")`
   — this shader works on flat geometry (it's the non-curved variant). Sample the
   decal projection in screen-space depth. Vanilla material from `EdgeLineCloneSystem`
   already loads this shader; copy the material instead of building a fresh
   `HDRP/Unlit` one. The current MarkingMeshRenderSystem code already explored this
   and found "renderQueue=2000, 2 passes, keywords incl. ENABLE_VT" — the missing
   piece is the **mesh has to be a small cube volume**, not a flat strip. Try
   extruding the ribbon 0.5m vertically into a thin box → `DefaultDecalShader` will
   project the marking texture onto whatever surface is *inside* the box.

2. **Alternative:** use Unity's HDRP `DecalProjector` MonoBehaviour. This is the
   conventional HDRP decal API. Vanilla doesn't use it but nothing prevents a mod
   from using it (an HDRP project's `DecalProjector` adds itself to HDRP's culling
   list directly). Per-pair geometry → per-pair `GameObject + DecalProjector`. Cost
   per pair: 1 GameObject + 1 Mesh + projector setup. Concerns: GameObject churn
   per node change, but pair counts are tiny.

3. **Set `_DecalLayerMask` correctly** on the material so it projects only onto
   Roads (`DecalLayers.Roads = 2`).

**Cost:** ~1 session experimentation. Lower payoff than Path A but unblocks
per-pair styling.

### Path C (give up on vanilla quality) — keep current `HDRP/Unlit` approach, polish edges

Pros: known-working. Predictable. Renders anything we feed it.
Cons: line floats slightly above road, no depth fade, no proper road-color blending,
no virtual texturing.

**Recommended only if** Paths A and B both prove unworkable in their first session.

### Bottom line

**Try Path A first.** The diagnostic effort is bounded (1–2 sessions, with clear
diff comparison between our entity and a working vanilla entity), and the payoff
is full vanilla quality with zero shader work. The reason Phase 4 Step 2 was
abandoned was an unverified hypothesis ("PrefabSystem requires baked-in
registration") that the decomp does NOT support — `RequiredBatchesSystem` is
prepared to initialize batch groups on-demand for any entity that matches the
right query. We just need to figure out which exact component / tag / flag is
missing on our sublane to make the existing machinery accept it.

If Path A fails after a deliberate diagnostic effort, Path B (stay with custom
mesh but switch to `DefaultDecalShader` on cube volumes, or HDRP `DecalProjector`)
is the next-best improvement to the current PoC's visual quality.

Path C is the "freeze and ship" option.
