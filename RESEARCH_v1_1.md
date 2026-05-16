# RESEARCH: v1.1 Implementation Recipes (commit 342afa4)

Source commit: `342afa4` — "Roll back per-segment toolbar upgrade — keep stable v1.1"

---

## 1. Edge-line patch recipe

**File:** `src/TownRoadLane/EdgeMarkingPatchSystem.cs`

### Which marking prefabs are patched

Two vanilla NetLanePrefabs that hold the curb-side edge line, one per region theme (CS2 picks via `ThemeObject` component automatically at runtime):

```csharp
// Line 33–34
private static readonly string[] kEdgeLinePrefabNames = { "EU Highway Edge Line", "NA Highway Edge Line" };
```

These prefabs already existed in vanilla and already had a `SecondaryLane` component with `m_LeftLanes` pointing at highway drive-lane prefabs. The system appends city-road entries to that same array without touching the vanilla entries.

### Which drive-lane prefabs get the edge line appended

```csharp
// Lines 36–43
private static readonly string[] kCityLaneNames =
{
    "Car Drive Lane 3",
    "Car Drive Lane 3 - Tram",
    "Public Transport Lane 3",
    "Public Transport Lane 3 - Tram",
};
```

Rationale: `Car Drive Lane 3` is used by Small Road, Medium Road, one-way variants, and Road Builder 3 m lanes. The tram and PT variants cover the same lane width on roads that carry trams or buses. All four use the same 3 m geometry, matching `Highway Drive Lane 3` which also uses `Car Lane 3 Mesh` — so the edge-line geometry fits without any positional adjustment.

### Exact SecondaryLaneInfo configuration per city-lane entry

Two entries are added per lane, mirroring what the vanilla edge-line already has for `Highway Drive Lane 3`:

```csharp
// Lines 135–139 (AppendCityLanes method)
if (!HasEntry(existing, lane, safe: true, merge: false, safeMaster: false))
    toAdd.Add(new SecondaryLaneInfo { m_Lane = lane, m_RequireSafe = true });
if (!HasEntry(existing, lane, safe: false, merge: true, safeMaster: true))
    toAdd.Add(new SecondaryLaneInfo { m_Lane = lane, m_RequireMerge = true, m_RequireSafeMaster = true });
```

**Entry 1 — `{ m_RequireSafe = true }`**
Draws the edge line on ordinary straight segments. `RequireSafe` means the lane has safe (non-merging) space next to it — the line appears at the outer edge of the carriageway where there is no merge happening.

**Entry 2 — `{ m_RequireMerge = true, m_RequireSafeMaster = true }`**
Draws the edge line through merge geometry. Without this second entry the line would stop at merge points (onramps, road-width transitions). `RequireMerge` means the lane IS a merge; `RequireSafeMaster` means this instance is the "master" side of the merge that continues the safe edge. This pair exactly matches what vanilla uses for `Highway Drive Lane 3` to continue the highway edge line through entry/exit ramps.

### Which list it is added to

`m_LeftLanes` only:

```csharp
// Lines 144–146
sec.m_LeftLanes = merged;
```

NOTES.md confirms (line 181–184) that vanilla `EU Highway Edge Line` lists all its host lanes in `m_LeftLanes` with `canFlipSides = true`, so the engine mirrors the line to the right side automatically. The system does not touch `m_RightLanes` or `m_CrossingLanes`.

### How UpdatePrefab is called

```csharp
// Lines 111–115
m_PrefabSystem.UpdatePrefab(edgePrefab);
patched++;
log.Info($"patched '{edgeName}': added {added} m_LeftLanes entries, queued UpdatePrefab");
```

`UpdatePrefab` triggers `EntityArchetypeCollectionSystem` to re-bake the marking prefab's `SecondaryNetLane` buffer, so roads already in the scene pick up the new entries on their next geometry rebuild.

### Execution model

The system runs once during `PrefabUpdate` phase, sets `m_Done = true`, then disables itself. Re-running is not needed because marking-prefab changes propagate via the normal net-pipeline without re-triggering this system.

---

## 2. Parking line + end-tick patch recipe

**File:** `src/TownRoadLane/ParkingMarkingPatchSystem.cs`

### Clone vs edit-in-place — why clone for parking but edit for edge

The edge-line system **edits** vanilla prefabs in place because the target prefabs (`EU/NA Highway Edge Line`) already exist and already have the right mesh, material, LODs, and rendering archetype — only their lane-host lists need expanding.

The parking system **clones** because there is no vanilla marking that draws along parallel parking zones. The closest vanilla prefabs (`EU/NA Car Bay Line` for longitudinal, `EU/NA Parking Cross Line` for end ticks) have the right rendering setup (material, LODs, SubMesh, entity archetype), but the wrong lane-host configuration and wrong mesh. Cloning brings all rendering data for free; then the clone's `SecondaryLane` component and mesh slot are overwritten.

`PrefabSystem.DuplicatePrefab` is used (not `AddPrefab` with a raw `PrefabBase.Create`) because `DuplicatePrefab` copies the full entity archetype including all rendering components, which is required for the net pipeline to treat the prefab as a valid marking lane.

### Source prefabs and clone names

```csharp
// Lines 56–62
private static readonly (string src, string clone, Role role)[] kRecipes =
{
    ("EU Car Bay Line",       "TownRoadLane EU Parallel Parking Line", Role.Longitudinal),
    ("NA Car Bay Line",       "TownRoadLane NA Parallel Parking Line", Role.Longitudinal),
    ("EU Parking Cross Line", "TownRoadLane EU Parallel Parking End",  Role.End),
    ("NA Parking Cross Line", "TownRoadLane NA Parallel Parking End",  Role.End),
};
```

The `EU`/`NA` split is needed because the clone inherits the vanilla prefab's `ThemeObject` component, so the engine picks the right regional variant automatically.

### Host lanes

Carriageway side (left lanes of longitudinal line):

```csharp
// Lines 44–49
private static readonly string[] kCarriagewayLaneNames =
{
    "Car Drive Lane 3", "Car Drive Lane 3 - Tram", "Public Transport Lane 3", "Public Transport Lane 3 - Tram",
};
```

Parking side:

```csharp
// Line 53
private const string kParkingLaneName = "Parking Lane 2";
```

**Why `Parking Lane 2` and not `Boarding Lane 0`:**
`Boarding Lane 0` is present on ~50 roads including all oneway and asymmetric variants even when they have no actual parking space — hosting on it would draw the line everywhere and would overlap the curb edge line (NOTES.md line 253). `Parking Lane 2` is present on ~26 roads that actually have a 2 m parallel parking zone, which is the correct condition. Its 2 m width also gives a clean geometry position at the parking edge.

### Full SecondaryLane configuration — longitudinal line

```csharp
// Lines 161–167
sec.m_LeftLanes = MakeInfos(carriageway);       // Car Drive Lane 3 + variants, m_RequireSafe=true
sec.m_RightLanes = MakeInfos(new[] { parkingLane }); // Parking Lane 2, m_RequireSafe=true
sec.m_CrossingLanes = Array.Empty<SecondaryLaneInfo2>();
sec.m_FitToParkingSpaces = false;
sec.m_CanFlipSides = true;
```

`MakeInfos` always sets only `m_RequireSafe = true` (no merge flags):

```csharp
// Lines 243–248
private static SecondaryLaneInfo[] MakeInfos(IReadOnlyList<NetLanePrefab> lanes)
{
    var arr = new SecondaryLaneInfo[lanes.Count];
    for (int i = 0; i < lanes.Count; i++) arr[i] = new SecondaryLaneInfo { m_Lane = lanes[i], m_RequireSafe = true };
    return arr;
}
```

`m_FitToParkingSpaces = false` — because `Parking Lane 2` has `SlotInterval == 0` (slot count = 1), `FitToParkingSpaces` would not subdivide it, but leaving it false avoids any unintended cut-range logic. `m_CanFlipSides = true` so the engine mirrors the line to the correct side regardless of road direction.

### Full SecondaryLane configuration — end tick

```csharp
// Lines 171–179
sec.m_LeftLanes = Array.Empty<SecondaryLaneInfo>();
sec.m_RightLanes = Array.Empty<SecondaryLaneInfo>();
sec.m_CrossingLanes = MakeCrossInfos(new[] { parkingLane });  // Parking Lane 2, m_RequireContinue=false
sec.m_FitToParkingSpaces = true;
sec.m_CanFlipSides = true;
sec.m_LengthOffset = new Unity.Mathematics.float2(-0.1f, 0f);
sec.m_PositionOffset = new Unity.Mathematics.float3(0.1f, 0f, 0f);
```

`MakeCrossInfos`:

```csharp
// Lines 250–255
private static SecondaryLaneInfo2[] MakeCrossInfos(IReadOnlyList<NetLanePrefab> lanes)
{
    var arr = new SecondaryLaneInfo2[lanes.Count];
    for (int i = 0; i < lanes.Count; i++) arr[i] = new SecondaryLaneInfo2 { m_Lane = lanes[i], m_RequireContinue = false };
    return arr;
}
```

Key points:
- `m_CrossingLanes` with `m_RequireContinue = false` draws at zone boundaries (start and end of the parking block), not at internal connections. `RequireContinue = true` would draw at internal lane-continuation points (middle of the block) — the inverse of what we want.
- `m_FitToParkingSpaces = true` is required for `m_CrossingLanes` to activate the crossing-path code path in `SecondaryLaneSystem`. Since `Parking Lane 2` has `SlotInterval == 0`, the crossing path draws exactly two ticks: one at `m=0` (block start) and one at `m=slotCount=1` (block end) — NOTES.md line 259–261.
- `m_LengthOffset = (-0.1f, 0f)` trims both ends of each tick symmetrically to avoid overrun.
- `m_PositionOffset = (0.1f, 0f, 0f)` shifts the tick 0.1 m laterally for visual alignment.

### Mesh swap details

**Longitudinal line default mesh:** `"White Dashed Line Mesh - Dense"` (fallback `"White Dashed Line Mesh - Dense"`)
**End-tick default mesh:** `"White Solid Line Mesh"` (fallback `"White Solid Line Mesh"`)

Both are resolved by querying entities with `PrefabData + MeshData` components and looking up `RenderPrefab` by name:

```csharp
// Lines 208–214 (ResolveMeshes)
var meshQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<MeshData>());
var ents = meshQuery.ToEntityArray(Allocator.Temp);
for (int i = 0; i < ents.Length; i++)
    if (m_PrefabSystem.TryGetPrefab<RenderPrefab>(ents[i], out var rp) && rp != null && wanted.Contains(rp.name) ...)
        result[rp.name] = rp;
```

Mesh is placed into the cloned prefab by replacing `m_Meshes[].m_Mesh` slots:

```csharp
// Lines 199–205 (SwapMesh)
private static int SwapMesh(NetLanePrefab prefab, RenderPrefab mesh)
{
    if (mesh == null || !(prefab is NetLaneGeometryPrefab g) || g.m_Meshes == null) return 0;
    int n = 0;
    for (int m = 0; m < g.m_Meshes.Length; m++)
        if (g.m_Meshes[m].m_Mesh != null) { g.m_Meshes[m].m_Mesh = mesh; n++; }
    return n;
}
```

The source clone already has the correct `SubMesh` slots and LOD count from `DuplicatePrefab`; `SwapMesh` replaces only the `RenderPrefab` reference in each occupied slot without touching count or LOD structure.

### Settings-driven mesh selection

`Setting.ParkingLineMeshName()` and `Setting.ParkingEndMeshName()` return vanilla or G87 mesh names based on the user's dropdown selection. G87 names are long constant strings; the system falls back gracefully if the mesh is not found (G87 not installed).

### Idempotency / re-apply

`ApplyOrUpdate()` is designed to be called more than once. On first call it clones the prefabs (they do not exist yet, so `TryGetValue` on `laneByName` returns false). On subsequent calls the clones are found by name in the same query, so they are updated in place rather than cloned again. This is what makes the "Reapply markings" button work without creating duplicate prefabs.

---

## 3. MarkingReapplySystem

**File:** `src/TownRoadLane/MarkingReapplySystem.cs`

### What it does

It serves the "Reapply markings now" button in the mod settings. Its sole job is:
1. Call `ParkingMarkingPatchSystem.ApplyOrUpdate()` so the cloned marking prefabs pick up the newly-selected mesh style.
2. Add the `Updated` component to every existing road edge, forcing the net pipeline to re-bake them.

### When it runs

Registered at `SystemUpdatePhase.Modification1` (Mod.cs line ~31). It is idle (`Enabled = false`) by default and only activates when `RequestReapply()` is called from the settings button:

```csharp
// Lines 28–33
public static void RequestReapply()
{
    var sys = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MarkingReapplySystem>();
    if (sys == null) { log.Warn(...); return; }
    sys.m_Requested = true;
    sys.Enabled = true;
}
```

### Query used

```csharp
// Lines 43–48
m_RoadEdgeQuery = GetEntityQuery(new EntityQueryDesc
{
    All = new[] { ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Edge>() },
    None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
});
```

Matches all non-deleted, non-temp road edges (includes bridges and quays — considered harmless). `EdgeGeometry` ensures GeometrySystem will reprocess them.

### The trigger mechanism

```csharp
// Lines 61–63
int n = m_RoadEdgeQuery.CalculateEntityCount();
if (n > 0)
    EntityManager.AddComponent<Updated>(m_RoadEdgeQuery);
```

Adding `Updated` to edges triggers `GeometrySystem → NetCompositionSystem → LaneSystem → SecondaryLaneSystem` in sequence. SecondaryLaneSystem then re-reads the `SecondaryNetLane` buffers (which were updated by `UpdatePrefab` inside `ApplyOrUpdate`) and re-draws all markings.

### Is MarkingReapplySystem needed in v2?

In v2 we replaced `SecondaryLaneSystem` with `CustomSecondaryLaneSystem`, which is called during a different update phase. The "Reapply markings" button concept (to rebuild existing roads after a settings change) may still be needed, but the mechanism shifts:

- If the v2 system is already re-running every frame (it intercepts the normal pipeline), adding `Updated` to edges may be enough to trigger a rebuild.
- The `ParkingMarkingPatchSystem.ApplyOrUpdate()` step (step 1 above) is still needed if v2 still uses DuplicatePrefab-cloned marking prefabs whose mesh can be changed at runtime.
- If v2 instead embeds marking decisions directly in `CustomSecondaryLaneSystem` logic (no per-prefab mesh), step 1 is unnecessary and only the `Updated` bulk-add remains.

---

## 4. Parking lane availability matrix

Source: NOTES.md lines 280–291, cross-verified against `ParkingMarkingPatchSystem.cs` (which relies on this data).

### Boarding Lane 0

- Present on approximately **50 road types**: Small Road, Medium Road, Large Road, XL Road, all oneway variants, asymmetric variants, divided variants, bridges, quays.
- Width: **0 m** (zero).
- No vanilla marking prefab hosts it (NOTES.md line 282).
- **Always present** even when no actual parking spaces exist (e.g., wide sidewalk with no parking enabled).
- **Do not host the parking line here** — it would draw on all roads unconditionally and at the wrong lateral position (line would sit at the road center, not the parking edge).

### Parking Lane 2

- Present on approximately **26 road types**: Small/Medium/Large/XL Road + bridges/quays variants that include actual 2 m parallel parking.
- Width: **2 m**.
- Absent on: Small Road Oneway-3lanes, Medium Road Asymmetric, and other variants that have `Boarding Lane 0` but no physical parking zone.
- `slot = (2, 0)` → `SlotInterval == 0` → treated as a single slot, not subdivided per space.
- This is the correct host lane for the parking-line marking. Its presence is the accurate signal that a parallel parking zone exists.

### Car Bay Lane 3

- Width: **3 m**. Used for perpendicular and angled bay parking (driveable bays off the road).
- Hosted by vanilla `EU/NA Car Bay Line` on its `m_RightLanes`.
- NOT used for parallel street parking.
- Appears on roads like roads with angled/double-sided bay parking layouts.

### Implications for v2

- **Parallel parking line will appear** only on roads carrying `Parking Lane 2` (~26 road types).
- **Parallel parking line will NOT appear** on oneway roads, asymmetric roads, and narrow variants that only carry `Boarding Lane 0`.
- This is the intended behavior — the line should appear only where there is actual parallel parking space.

### Perpendicular / angled parking (not our concern now)

`Parking Lane - Perpendicular` / `Parking Lane - Angled67` prefabs exist for double-sided bay parking roads and are hosted by vanilla `EU/NA Parking Cross Line`. No mod action needed for these.

---

## 5. What NOT to repeat from v1.x

These systems existed in commits **between** v1.1 (342afa4) and v2 and caused save-game corruption or crashes. None of them existed at 342afa4 — they were introduced in subsequent experimental commits and then abandoned.

**Confirmed absent from 342afa4** (git returns `fatal: path does not exist`):
- `MarkingFlagMaskExpanderSystem.cs`
- `MarkingUpgradePrefabSystem.cs`
- `MarkingLaneSubstituteSystem.cs`

### What they did and why they failed

**`MarkingFlagMaskExpanderSystem`** (introduced ~`de9d8b4`): Modified lane flag masks on existing road entities at runtime to make the engine treat city lanes as highway-like for SecondaryLaneSystem purposes. This mutated ECS component data on live entities; Road Builder roads and runtime-generated roads caused the system to widen lanes it should not have touched, leading to corruption.

**`MarkingUpgradePrefabSystem`** / **`MarkingLaneSubstituteSystem`** (introduced ~`6ae5dc5`–`636de81`): Cloned **drive-lane prefabs** (not marking prefabs) — specifically `Car Drive Lane 3` — into "no-marking" variants, then tried to substitute them into road composition at runtime. Drive-lane prefab substitution interferes with `NetCompositionSystem` baking and `SearchSystem` indexing. Roads using substituted lane prefabs could not be saved correctly (serialization expects the original prefab references). Multiple fixup attempts (`cac9fb9`, `d4865e1`, `468b69c`, `636de81`) failed to stabilize them. The entire approach was abandoned at `342afa4`.

**The safe pattern (v1.1 / v2):** Only mutate **marking** NetLanePrefabs (SecondaryLane geometry prefabs), never drive-lane prefabs. Marking prefabs are not serialized per road segment — they are rebuilt from prefab data on load. Drive-lane prefabs are referenced in road composition data and must remain stable.

---

## 6. Registration order in Mod.cs

```csharp
// Mod.cs lines ~19–31 (OnLoad)
updateSystem.UpdateAt<RoadPrefabDumpSystem>(SystemUpdatePhase.PrefabUpdate);       // diagnostics only
updateSystem.UpdateAt<EdgeMarkingPatchSystem>(SystemUpdatePhase.PrefabUpdate);     // patches EU/NA Highway Edge Line
updateSystem.UpdateAt<ParkingMarkingPatchSystem>(SystemUpdatePhase.PrefabUpdate);  // clones parking marking prefabs
updateSystem.UpdateAt<MarkingReapplySystem>(SystemUpdatePhase.Modification1);      // idle; activated by settings button
```

### Dependencies

- `EdgeMarkingPatchSystem` and `ParkingMarkingPatchSystem` both run at `PrefabUpdate`. They are independent — neither calls the other.
- `MarkingReapplySystem` is at `Modification1`, which runs after `PrefabUpdate` is complete. This ensures that if a reapply is requested immediately after load, `ParkingMarkingPatchSystem` has already created the clone prefabs.
- Both patch systems are one-shot (they set `m_Done = true` and `Enabled = false` after first `OnUpdate`). They will only re-run if called directly (e.g., `ParkingMarkingPatchSystem.ApplyOrUpdate()` from `MarkingReapplySystem`).
- `PrefabUpdate` is the correct phase for prefab mutation — it runs after all vanilla prefabs are loaded and initialized, so `TryGetPrefab` calls return valid results, but before the net pipeline processes road segments.

### v2 note

In v2 `EdgeMarkingPatchSystem` and `ParkingMarkingPatchSystem` were dropped at commit `3e3cfa4` ("drop v1 prefab systems, replace vanilla SecondaryLaneSystem"). Their roles are being re-implemented — edge line was re-introduced at `8c9daa7` via injection into `CustomSecondaryLaneSystem`. Parking line re-implementation is still pending.
