# RESEARCH: Vanilla SecondaryLane Pipeline

> Sources:
> - `decomp/Game/Game.Net/SecondaryLaneSystem.cs` (1970 lines)
> - `decomp/Game/Game.Prefabs/NetInitializeSystem.cs` (bake loop at lines ~1640–1780)
> - `decomp/Game/Game.Prefabs/SecondaryLane.cs`
> - `decomp/Game/Game.Prefabs/SecondaryLaneInfo.cs`
> - `decomp/Game/Game.Prefabs/SecondaryLaneInfo2.cs`
> - `decomp/Game/Game.Prefabs/SecondaryNetLane.cs`
> - `decomp/Game/Game.Prefabs/SecondaryNetLaneFlags.cs`
> - `decomp/Game/Game.Prefabs/PrefabSystem.cs`
> - `decomp/Game/Game.Common/SystemOrder.cs`
> - `src/TownRoadLane/CustomSecondaryLaneSystem.cs` (our copy, ~2200 lines)

---

## 1. Bake phase: managed → ECS reverse indexing

### Managed source: `SecondaryLane` component on marking prefab

`SecondaryLane.cs` is a `ComponentBase` that sits on a `NetLanePrefab` (the marking prefab, e.g. "Road Lane Divider", "Parking Lot Line", etc.).

```csharp
// SecondaryLane.cs:10-18
public class SecondaryLane : ComponentBase
{
    public SecondaryLaneInfo[] m_LeftLanes;   // which primary-lane prefabs trigger this on the left
    public SecondaryLaneInfo[] m_RightLanes;  // ... on the right
    public SecondaryLaneInfo2[] m_CrossingLanes; // crosswalk / stop-line entries
    public bool m_CanFlipSides;
    public bool m_DuplicateSides;
    public bool m_RequireParallel;
    public bool m_RequireOpposite;
    // ... skip-overlap flags, position offsets, cut params, spacing...
}
```

Each `SecondaryLaneInfo` entry references a primary-lane prefab (`m_Lane`) and carries boolean require-flags (`m_RequireSafe`, `m_RequireUnsafe`, `m_RequireMerge`, etc.).

### NetInitializeSystem bake loop (PrefabUpdate phase)

`NetInitializeSystem` runs at `SystemUpdatePhase.PrefabUpdate` (SystemOrder.cs:1009). For every lane prefab entity that has a `SecondaryLane` managed component the system builds the ECS `SecondaryNetLane` **buffer** on the **primary-lane prefab entity** (the target/host, not the marking prefab). This is the reverse index.

Key section, `NetInitializeSystem.cs:1641–1763`:

```csharp
// NetInitializeSystem.cs:1641-1648 — detect which arrays are populated
Entity entity = nativeArray[num11];               // current lane-prefab entity
SecondaryLane component21 = prefab7.GetComponent<SecondaryLane>();
value9.m_Flags |= LaneFlags.Secondary;             // marks this entity as a secondary lane
bool flag7 = component21.m_LeftLanes  != null && component21.m_LeftLanes.Length  != 0;
bool flag8 = component21.m_RightLanes != null && component21.m_RightLanes.Length != 0;
bool flag9 = component21.m_CrossingLanes != null && component21.m_CrossingLanes.Length != 0;
```

The `SecondaryLaneData` ECS struct is populated from the managed bool/float fields (SkipSafe*, FitToParkingSpaces, EvenSpacing, InvertOverlapCuts, m_PositionOffset, m_LengthOffset, m_CutMargin, m_CutOffset, m_CutOverlap, m_Spacing). Lines 1649-1699.

Then base flags from `SecondaryLane` are gathered into a `SecondaryNetLaneFlags` local:

```csharp
// NetInitializeSystem.cs:1692-1708
SecondaryNetLaneFlags secondaryNetLaneFlags = (SecondaryNetLaneFlags)0;
if (component21.m_CanFlipSides)   secondaryNetLaneFlags |= SecondaryNetLaneFlags.CanFlipSides;
if (component21.m_DuplicateSides) secondaryNetLaneFlags |= SecondaryNetLaneFlags.DuplicateSides;
if (component21.m_RequireParallel) secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireParallel;
if (component21.m_RequireOpposite) secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireOpposite;
```

#### Left-lane entries (NetInitializeSystem.cs:1716-1726)

For each entry in `m_LeftLanes`:
- OR in `SecondaryNetLaneFlags.Left` (and `OneSided` if no right array)
- OR in the entry's per-entry flags from `SecondaryLaneInfo.GetFlags()`
- Resolve the target primary-lane prefab entity via `m_PrefabSystem.GetEntity(secondaryLaneInfo.m_Lane)`
- **Add** a `SecondaryNetLane { m_Lane = entity (marking prefab), m_Flags = ... }` to the buffer on that primary-lane prefab entity

```csharp
// NetInitializeSystem.cs:1716-1726
for (int num13 = 0; num13 < component21.m_LeftLanes.Length; num13++)
{
    SecondaryLaneInfo secondaryLaneInfo = component21.m_LeftLanes[num13];
    SecondaryNetLaneFlags flags = secondaryNetLaneFlags2 | secondaryLaneInfo.GetFlags();
    Entity entity2 = m_PrefabSystem.GetEntity(secondaryLaneInfo.m_Lane);
    base.EntityManager.GetBuffer<SecondaryNetLane>(entity2).Add(new SecondaryNetLane
    {
        m_Lane = entity,   // entity = the SecondaryLane marking prefab
        m_Flags = flags
    });
}
```

#### Right-lane entries (NetInitializeSystem.cs:1728-1763)

Right-lane handling has a merge step: if a left-lane entry already wrote a buffer element for the same marking with the same base flags (differing only in Left/Right bit), it is **merged** into the existing entry by OR-ing `Right` into its flags. Otherwise a new entry is added.

```csharp
// NetInitializeSystem.cs:1741-1763  (simplified)
for (int num15 = 0; num15 < buffer.Length; num15++)
{
    SecondaryNetLane value16 = buffer[num15];
    if (value16.m_Lane == entity &&
        ((value16.m_Flags ^ secondaryNetLaneFlags4) & ~(Left | Right)) == 0)
    {
        value16.m_Flags |= secondaryNetLaneFlags4;  // merge Right bit in
        buffer[num15] = value16;
        break;
    }
}
// else: buffer.Add(new SecondaryNetLane { m_Lane = entity, m_Flags = secondaryNetLaneFlags4 });
```

This means a single buffer entry on a primary-lane prefab entity can carry **both** `Left` and `Right` bits if the same marking prefab appears in both arrays of the same `SecondaryLane`.

#### Crossing entries (NetInitializeSystem.cs:1765-1778)

Crossing entries use `SecondaryNetLaneFlags.Crossing` as the base; the per-entry flags come from `SecondaryLaneInfo2.GetFlags()` (RequireStop, RequireYield, RequirePavement, RequireContinue).

### Result

After bake: every **primary-lane prefab entity** (e.g. "Road Lane" or "Parking Lot Lane") has a `DynamicBuffer<SecondaryNetLane>` listing which marking prefabs should fire for it, with what directional/flag constraints. The marking prefabs themselves carry `SecondaryLaneData` (ECS struct with the geometric parameters).

This buffer is what `UpdateLanesJob` reads at runtime via `m_PrefabSecondaryLanes`.

---

## 2. Runtime pair-matching: the main loop

### System registration

`SecondaryLaneSystem` (and our `CustomSecondaryLaneSystem`) runs at **`SystemUpdatePhase.Modification4B`** (SystemOrder.cs:184):

```
LaneSystem            → Modification4
LaneOverlapSystem     → Modification4B  (must run BEFORE SecondaryLaneSystem)
SecondaryLaneSystem   → Modification4B
```

`SecondaryLaneSystem.OnCreate` (line 1873–1897) registers an entity query:

```csharp
m_OwnerQuery = GetEntityQuery(new EntityQueryDesc {
    All  = new[] { ComponentType.ReadOnly<SubLane>() },
    Any  = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
    None = new[] { OutsideConnection, Objects.OutsideConnection, Building, Area }
});
```

So it fires on any edge or node entity that was updated or deleted and has sublanes.

### UpdateLanesJob — `[BurstCompile]`

The job is `IJobChunk` and runs parallel over chunks (line 155). Because it is `[BurstCompile]`, no managed types are accessible inside; all data flows through ECS lookups. The ECB goes to `ModificationBarrier4B`.

### UpdateLanes (non-deleted path): main flow (lines 311–826)

For each entity (edge or node) in the chunk:

**Step 1: Collect old secondary lanes** (line 336)
```csharp
FillOldLaneBuffer(lanes, laneBuffer.m_OldLanes);
```
Scans `SubLane` buffer, finds existing secondary lanes, stores them in `m_OldLanes` keyed by `LaneKey(lane, prefab)`.

**Step 2: Read edge geometry** (lines 342-357)
For edges, reads `EdgeGeometry` to get `line` / `line2` (start-cap and end-cap lines across the road) and `float5`/`float6` (per-cap normal vectors). These are used later for crossing-lane intersection math.

**Step 3: Build LaneCorners** (lines 359-566)

For each sublane that is NOT a master lane or a secondary lane, and whose lane prefab has a `SecondaryNetLane` buffer:

```csharp
// SecondaryLaneSystem.cs:370-372
if ((netLaneData.m_Flags & LaneFlags.Secondary) != 0 ||
    !m_PrefabSecondaryLanes.TryGetBuffer(prefabRef.m_Prefab, out var bufferData) ||
    bufferData.Length == 0)
    continue;
```

Two `LaneCorner` entries are added per primary lane — **forward** and **inverted**:

```csharp
// SecondaryLaneSystem.cs:395-422 (forward + inverted pair)
laneBuffer.m_LaneCorners.Add(new LaneCorner {
    m_StartPosition = a,      // curve.a + width offset toward right side
    m_EndPosition   = d2,     // curve.d + width offset toward left side
    m_Tangents      = new float4(float7, float8),  // start-tangent, end-tangent
    m_Lane          = subLane,
    m_StartNode     = lane.m_StartNode,
    m_EndNode       = lane.m_EndNode,
    m_Inverted      = false,
    ...
});
laneBuffer.m_LaneCorners.Add(new LaneCorner {
    m_StartPosition = d,      // curve.d + width offset toward right (inverted)
    m_EndPosition   = a2,     // curve.a + width offset toward left (inverted)
    m_Tangents      = new float4(float8, float7),  // END-tangent first, then START-tangent
    m_Lane          = subLane,
    m_StartNode     = lane.m_EndNode,   // flipped!
    m_EndNode       = lane.m_StartNode, // flipped!
    m_Inverted      = true,
    ...
});
```

The width offsets are: `a.xz += Right(float7) * (width.x * 0.5f)` (right edge of lane at start), `d.xz += Left(float8) * (width.x * 0.5f)` (right edge of lane at end for forward view), etc.

**Crossing lanes** from `SecondaryNetLane` entries with the `Crossing` bit are separately accumulated into `laneBuffer.m_CrossingLanes` (lines 437-566) using `AddCrossingLane`.

### Step 4: Pair-matching loop (lines 568-811)

For each `LaneCorner` (`laneCorner` = the reference corner being examined):

**Proximity check** (lines 573-601): Two segment lines are built at the corner's start and end positions, extended by `num3 = distance(start,end) * 0.5f`. Another corner (`laneCorner3`) must have its endpoint within `(halfWidths)^2` of these segments.

```csharp
// SecondaryLaneSystem.cs:596-601 — distance tolerance
float2 float14 = (float12 + float13) * 0.25f;
float14 *= float14;
if ((...distanceSq(line11, laneCorner3.m_EndPosition) > float14.x) ||
    (...distanceSq(line12, laneCorner3.m_StartPosition) > float14.y))
    continue;
```

**Tangent matching / `.zwxy` swizzle** (lines 607-621):

```csharp
// SecondaryLaneSystem.cs:607-620
bool num6  = math.distancesq(laneCorner.m_Tangents, laneCorner3.m_Tangents.zwxy) < 0.01f;
bool flag10 = math.distancesq(laneCorner.m_Tangents, -laneCorner3.m_Tangents.zwxy) < 0.01f;
```

`.zwxy` swaps the xy (start tangent) and zw (end tangent) components. So `num6` checks whether the two corners run in the **same** direction (parallel lanes meeting at nodes), and `flag10` checks **anti-parallel** (opposite-direction lanes — the normal two-way case). `flag5 = flag10` is later used to set `RequireParallel` vs `RequireOpposite`.

The best match (minimum `distancesq` of the two endpoint pairs) wins and is stored in `laneCorner2`.

**Duplicate-suppression** (line 622-624): If there is a paired lane2 and we are not `DuplicateSides`, only process the pair once (by skipping when `laneCorner.m_Lane.Index > laneCorner2.m_Lane.Index`).

### Step 5: Flag computation for the matched pair (lines 626-757)

For each corner in the pair, the system computes a `SecondaryNetLaneFlags` value describing the runtime state of that corner's lane:

**Safe/Unsafe** (lines 658-659):
```csharp
secondaryNetLaneFlags = ((carLane.m_Flags & CarLaneFlags.Unsafe) == 0 &&
                         (pedestrianLane.m_Flags & PedestrianLaneFlags.Unsafe) == 0)
    ? secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireUnsafe   // lane is safe (normal)
    : secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireSafe;    // lane is unsafe (crossing)
```
"Safe" means car-safe (legal to cross) — i.e. solid line territory. "Unsafe" means the lane can be crossed — dashed line territory.

**ForbidPassing/AllowPassing** (lines 660-661): mirrors the `CarLaneFlags.ForbidPassing` flag.

**Roundabout** (lines 662-686): Sets `RequireRoundabout` / `RequireNotRoundabout` based on whether the lane carries `CarLaneFlags.Roundabout` without `Approach`.

**SlaveLane / merge logic** (lines 688-714): `SlaveLane.m_Flags.MultipleLanes` → `RequireSingle` vs `RequireMultiple`; `SlaveLane.m_Flags.MergingLane` → `RequireMerge` vs `RequireContinue`.

**SafeMaster** (lines 691, 705): If the slave lane's master is `Unsafe`, set `RequireSafeMaster`.

**Master-score node tiebreak** (lines 716-730): For edges where both corners share a node (`flag6`/`flag7`), the system computes a numeric score based on Safe and Continue flags. If both scores are zero (both lanes are normal), the pair is **skipped** at a node — marking only happens at actual merge/safe boundaries. `flag11 = (num8 > num9) ^ laneCorner.m_Inverted` controls which side is "merge end".

### Step 6: Prefab buffer lookup and flag matching (lines 731-811)

`secondaryNetLaneFlags3` encodes which side (Left/Right) relative to the corner:
```csharp
// SecondaryLaneSystem.cs:734
secondaryNetLaneFlags3 = (SecondaryNetLaneFlags)(
    (laneCorner.m_Inverted == m_LeftHandTraffic) ? 1 : 2);  // 1=Left, 2=Right
```

For each `SecondaryNetLane secondaryNetLane2` in the primary-lane's prefab buffer:

```csharp
// SecondaryLaneSystem.cs:760-767
bool2 bool6 = new bool2(
    (secondaryNetLane2.m_Flags & secondaryNetLaneFlags3) == secondaryNetLaneFlags3,  // exact Left/Right match
    (secondaryNetLane2.m_Flags & secondaryNetLaneFlags5) == secondaryNetLaneFlags5   // flipped-sides match (CanFlipSides)
);
if ((((secondaryNetLane2.m_Flags & secondaryNetLaneFlags) != 0) | !math.any(bool6)) ||
    !CheckRequirements(ref laneBuffer, secondaryNetLane2.m_Lane))
    continue;
```

`(secondaryNetLane2.m_Flags & secondaryNetLaneFlags) != 0` — the runtime flags and prefab-entry flags must NOT have any **conflicting** bit set. A conflict means the prefab requires e.g. `RequireSafe` but this lane is `RequireUnsafe` (they can't both be true). This is the main gate.

If there is a paired `laneCorner2`, the inner loop also checks that the other primary-lane's buffer contains an entry for the same marking prefab with compatible flags (lines 779-790).

---

## 3. CreateSecondaryLane signatures

### Overload 1: Paired/single-lane version (SecondaryLaneSystem.cs:1009)

Used for edge-line (single-sided) and divider (paired) markings.

```csharp
private void CreateSecondaryLane(
    int jobIndex,
    ref int laneIndex,
    Entity owner,          // the edge or node entity
    Entity leftLane,       // primary lane on the left side (Entity.Null for single-sided right)
    Entity rightLane,      // primary lane on the right side (Entity.Null for single-sided left)
    Entity prefab,         // the marking prefab entity
    DynamicBuffer<SubLane> lanes,
    LaneBuffer laneBuffer,
    float2 leftWidth,      // left lane width.xy (or 0 if no left lane)
    float2 rightWidth,     // right lane width.xy
    float2 startTangent,   // MathUtils.Left(float5) — road start normal
    float2 endTangent,     // MathUtils.Left(float6) — road end normal
    bool isHidden,
    bool isNode,
    bool invertLeft,       // whether to invert the left-lane bezier before interpolating
    bool invertRight,      // whether to invert the right-lane bezier before interpolating
    bool mergeStart,       // snap curve.a to the "merge side" offset
    bool mergeEnd,         // snap curve.d to the "merge side" offset
    bool mergeLeft,        // which lane is the merge reference (true=left, false=right)
    bool isTemp,
    Temp ownerTemp)
```

**When called:**
- Paired (both `leftLane` and `rightLane` non-null): curve is a bezier lerp between the two lane beziers, weighted by width. Lines 1024-1067.
- Left-only (`leftLane` non-null, `rightLane = Entity.Null`): curve is `OffsetCurveLeftSmooth(curve2, -halfWidth - cutOffset)`. Lines 1069-1088.
- Right-only (`rightLane` non-null, `leftLane = Entity.Null`): curve is `OffsetCurveLeftSmooth(curve3, +halfWidth - cutOffset)`. Lines 1090-1110.

After computing the curve, overlap cut-ranges are computed and the curve is segmented. If `m_Spacing > 0.1f`, the resulting curve is diced into evenly-spaced point markings (stop bars, tick marks). Otherwise the whole segment becomes one lane entity.

Call sites in `UpdateLanesJob.UpdateLanes` (lines 797-806):
- `DuplicateSides` path (line 797): `leftLane=Entity.Null, rightLane=laneCorner.m_Lane, invertRight=!laneCorner.m_Inverted`
- Normal paired path A (line 801): `leftLane=laneCorner2.m_Lane, rightLane=laneCorner.m_Lane`
- Normal paired path B (line 805): `leftLane=laneCorner.m_Lane, rightLane=laneCorner2.m_Lane`

### Overload 2: Inner geometry finalizer (SecondaryLaneSystem.cs:1304)

Called by overload 1 once the curve segment is determined:

```csharp
private void CreateSecondaryLane(
    int jobIndex,
    ref int laneIndex,
    Entity owner,
    Entity prefab,
    LaneBuffer laneBuffer,
    Curve curveData,          // the already-computed and cut bezier segment
    float2 startTangent,
    float2 endTangent,
    float2 hangingDistances,  // for utility cable lanes with m_Hanging != 0
    bool isHidden,
    bool isTemp,
    Temp ownerTemp)
```

This overload actually creates or updates the ECS lane entity via `m_CommandBuffer`. It allocates `PathNode` indices for start/middle/end (`laneIndex` incremented 3 times). Reuses existing entity if found in `laneBuffer.m_OldLanes`.

### Overload 3: Crossing version (SecondaryLaneSystem.cs:821)

```csharp
CreateSecondaryLane(
    chunkIndex, ref laneIndex,
    owner,
    crossingLane.m_Prefab,    // no explicit leftLane/rightLane — straight curve only
    laneBuffer,
    curveData,                 // pre-built straight bezier from m_CrossingLanes
    crossingLane.m_StartTangent,
    crossingLane.m_EndTangent,
    0f,                        // hangingDistances = 0
    crossingLane.m_Hidden,
    flag, ownerTemp)
```

Called at lines 813-822, after all paired matching is done. Each non-optional crossing lane in `laneBuffer.m_CrossingLanes` is emitted. Crosswalk/stop-line/yield-line segments are chained together in `AddCrossingLane` before this loop (lines 961-1007): if two crossing-lane candidates share an endpoint, they are merged into one longer segment.

---

## 4. Node pair-matching (for phase 4)

The same `UpdateLanesJob.UpdateLanes` runs on **node** entities (where `isNode = chunk.Has(ref m_NodeType)`). The `LaneCorner` list is built identically — each sublane of the node entity contributes two corners. The key difference is that node sublanes are routing lanes (turn lanes, continuation lanes) that cross between edges.

**Node-specific: master-score filter** (lines 716-730):

At a node, `flag6 = laneCorner.m_StartNode.Equals(laneCorner3.m_EndNode)` and `flag7 = laneCorner.m_EndNode.Equals(laneCorner3.m_StartNode)`. When both are true the two corners share the same network node — meaning they are edges meeting at an intersection.

```csharp
// SecondaryLaneSystem.cs:718-729
if (flag6 || flag7)
{
    int num8 = 0;
    int num9 = 0;
    num8 += math.select(0, 1, (secondaryNetLaneFlags  & SecondaryNetLaneFlags.RequireSafe)     != 0);
    num8 += math.select(0, 2, (secondaryNetLaneFlags  & SecondaryNetLaneFlags.RequireContinue) != 0);
    num9 += math.select(0, 1, (secondaryNetLaneFlags2 & SecondaryNetLaneFlags.RequireSafe)     != 0);
    num9 += math.select(0, 2, (secondaryNetLaneFlags2 & SecondaryNetLaneFlags.RequireContinue) != 0);
    if (num8 == 0 && num9 == 0)
        continue;   // both lanes are "normal" — skip this pair at node
    flag11 = (num8 > num9) ^ laneCorner.m_Inverted;
}
```

A score of 0 = normal through-lane (no safe boundary, no merge). Score ≥1 = some kind of boundary. Pairs where both scores are zero at a node are suppressed — no marking spawned. This prevents dividers from appearing inside intersections where there is no physical boundary.

When a pair does qualify, the pair-matching and `CreateSecondaryLane` calls are identical to the edge path. The crossing-lane list (`m_CrossingLanes`) is also processed identically — this is how crosswalks and stop lines appear at intersection nodes.

**Crossing lane specifics** (lines 437-566):

Crossing lanes are sourced from sublane prefabs whose `SecondaryNetLane` has the `Crossing` flag set. The position is computed by intersecting the crossing-lane prefab's entry normal against the node's start-cap/end-cap edge lines. For `FitToParkingSpaces` crossing lanes (parking end-tick marks), individual slot positions are computed by `FitToParkingLane` and one `AddCrossingLane` call is made per slot.

For stop-line / yield-line, `RequireStop` / `RequireYield` checks are done against `CarLaneFlags.Stop`, `CarLaneFlags.Yield`, `CarLaneFlags.LevelCrossing`, `CarLaneFlags.TrafficLights` (lines 447-455). If a car lane has none of these, `RequireStop` lanes are suppressed.

Crossing lanes are merged/chained before emission: if two candidates for the same prefab are within 1 metre of each other (end-to-start), `AddCrossingLane` stitches them into one longer segment (lines 961-1007). This is how a crosswalk spanning multiple driving lanes becomes one lane entity, not N separate ones.

---

## 5. MarkingOverride hook points

The job is `[BurstCompile]`. Any hook must be placed **before** a `CreateSecondaryLane` call and can only read ECS blittable component lookups (no managed types). Our `MarkingOverride` component is `IComponentData` (unmanaged), so it can be read from Burst.

### All CreateSecondaryLane call sites in UpdateLanes (SecondaryLaneSystem.cs)

| Line range | Context | What is spawned |
|---|---|---|
| ~797 | `DuplicateSides` single-sided path | Both-sides edge marking (center line) |
| ~801 | Paired path A (laneCorner2 is left) | Standard divider / edge line |
| ~805 | Paired path B (laneCorner is left) | Standard divider / edge line (flipped) |
| ~821 | Crossing loop | Crosswalk / stop line / yield line |

All four are inside `UpdateLanes` called from `Execute` (line 282).

**Recommended gate pattern** (Burst-safe):

```csharp
// Add m_MarkingOverrideData = GetComponentLookup<MarkingOverride>(isReadOnly: true) to the job fields.

// Before the pair-matching loop (line ~568) — can gate ALL pair-derived markings for the owner:
if (m_MarkingOverrideData.TryGetComponent(owner, out var ovr) && ovr.HideAll)
    goto skipMarkingGeneration;

// Per-pair gates before each CreateSecondaryLane call — for finer control:
bool hideEdge = m_MarkingOverrideData.TryGetComponent(owner, out var ovr2) && ovr2.Hides(MarkingCategory.EdgeLine);
if (!hideEdge) CreateSecondaryLane(...);
```

Our current code (`CustomSecondaryLaneSystem.cs:900-962`) uses a per-corner check that reads `m_MarkingOverrideData.TryGetComponent(owner, ...)` before the edge-line injection loop.

For vanilla markings (the four call sites above) the gate could be placed:
- After line ~626 (after pair is matched, before prefab buffer loop) — to gate all vanilla markings for this pair.
- Before line ~797/801/805/821 individually — for category-specific gating.

The `goto skipMarkingGeneration` label already exists in our copy (`CustomSecondaryLaneSystem.cs:963`) and jumps past all `CreateSecondaryLane` calls and the crossing loop to `RemoveUnusedOldLanes`.

---

## 6. UpdatePrefab lifecycle

### What `PrefabSystem.UpdatePrefab(prefab)` does (PrefabSystem.cs:306-309)

```csharp
public void UpdatePrefab(PrefabBase prefab, Entity sourceInstance = default(Entity))
{
    m_UpdateMap[prefab] = sourceInstance;
}
```

It only **enqueues** the prefab into a dictionary. The actual work runs in `PrefabSystem.OnUpdate` on the next `SystemUpdatePhase.MainLoop` frame:

```csharp
// PrefabSystem.cs:721-728
protected override void OnUpdate()
{
    bool num = UpdatePrefabs();
    m_UpdateSystem.Update(SystemUpdatePhase.PrefabUpdate);  // fires NetInitializeSystem etc.
    ...
}
```

### What `UpdatePrefabs()` does (PrefabSystem.cs:731-790)

For each queued prefab:
1. Marks the **old** prefab entity as `Deleted` (`EntityManager.AddComponent<Deleted>(value2)`).
2. Creates a **new** entity with the same component types plus `Created` + `Updated`.
3. Copies `PrefabData` from old to new.
4. Calls `ReplacePrefabSystem.ReplacePrefab(oldEntity, newEntity, sourceInstance)` — this updates all references to the old entity throughout the world.

Then `m_UpdateSystem.Update(SystemUpdatePhase.PrefabUpdate)` runs `NetInitializeSystem`, which re-bakes the new entity, rebuilding the `SecondaryNetLane` buffer with the patched managed arrays.

### Do edges refresh automatically?

After the new prefab entity is live with updated `SecondaryNetLane` buffers, roads do **not** automatically re-run `SecondaryLaneSystem`. You must mark road entities as `Updated` to trigger `UpdateLanesJob`. Our `MarkingToggleSystem` does this explicitly by calling `EntityManager.AddComponent<Updated>(entity)` on all edges and nodes.

**Timing summary:**
- Frame N: call `PrefabSystem.UpdatePrefab(markingPrefab)` — queued.
- Frame N+1 (MainLoop): `PrefabSystem.OnUpdate` runs → `UpdatePrefabs()` replaces entity → `PrefabUpdate` phase fires → `NetInitializeSystem` re-bakes `SecondaryNetLane` buffer.
- Frame N+1 (Modification4B): `CustomSecondaryLaneSystem` only processes entities already marked `Updated`. It does NOT see road edges yet.
- To force re-generation: in frame N or N+1, mark road edges as `Updated`.

Note: `UpdatePrefabs()` also adds `Updated` to the new prefab entity itself (line 760), but that is the prefab entity — `SecondaryLaneSystem.m_OwnerQuery` matches road-edge/node entities, not prefab entities. So the prefab `Updated` tag does not trigger secondary lane regeneration.

---

## 7. Save/load lifecycle

### Are `SecondaryNetLane` buffers persisted?

`SecondaryNetLane` buffers live on **prefab entities** (the lane prefab entity in ECS, not world objects). Prefab entities are re-created from managed `PrefabBase` data during each game load. They are **not serialized** — they are rebuilt by `NetInitializeSystem` running in `PrefabUpdate` during startup.

This is confirmed by: `PrefabSystem.Serialize` (line 792) only serializes `PrefabID` lists and `PrefabData` GUIDs — it does not serialize any ECS buffer contents. The actual prefab component data is stored as asset files, not save data.

### What happens at load

1. `PrefabSystem` loads all prefab assets and calls `AddPrefab` for each → creates fresh prefab entities.
2. `PrefabUpdate` phase fires → `NetInitializeSystem` initializes all lane prefab data including `SecondaryNetLane` buffers from managed `SecondaryLane` component arrays.
3. Road edge/node entities are deserialized from the save file. Their `SubLane` buffers (which contain secondary lane sub-entities) **are** serialized and restored.
4. However, each restored entity gets `Updated` tagged, which triggers `SecondaryLaneSystem` to rebuild secondary lanes from the (now freshly baked) prefab data.

**Implication for our prefab patching:** We must apply our `SecondaryLane.m_LeftLanes/m_RightLanes` patches **before** `PrefabUpdate` runs on load, or call `PrefabSystem.UpdatePrefab` afterward and force-mark edges as `Updated`. The `OnGameLoaded` or `OnPrefabsLoaded` hook (whichever fires before `PrefabUpdate`) is the correct injection point. The patches are **not persisted** across game restarts; they must be re-applied each session.

---

## Appendix A: SecondaryNetLaneFlags reference

```csharp
// SecondaryNetLaneFlags.cs — complete enum
[Flags]
public enum SecondaryNetLaneFlags
{
    Left                = 0x001,   // entry is for the left lane in a pair
    Right               = 0x002,   // entry is for the right lane in a pair
    OneSided            = 0x004,   // no partner lane exists (curb-edge or single-side)
    RequireSafe         = 0x008,   // only spawn if this lane is at a safe crossing (solid)
    CanFlipSides        = 0x010,   // allow Left↔Right flip for LHT
    RequireParallel     = 0x020,   // partner lane runs same direction (two parallel lanes)
    RequireOpposite     = 0x040,   // partner runs opposite direction (center-line case)
    RequireSingle       = 0x080,   // only one slave lane (no merge)
    RequireMultiple     = 0x100,   // multiple slave lanes present
    RequireAllowPassing = 0x200,   // dashed line: overtaking allowed
    RequireForbidPassing= 0x400,   // solid line: overtaking forbidden
    RequireMerge        = 0x800,   // at a merge/fork boundary
    RequireContinue     = 0x1000,  // continuous through-connection
    RequireStop         = 0x2000,  // stop line (crossing)
    Crossing            = 0x4000,  // entry is a crossing lane (stop line / crosswalk)
    RequireUnsafe       = 0x8000,  // spawn on unsafe (dashed) lane
    RequirePavement     = 0x10000, // only if road has pavement composition flag
    RequireYield        = 0x20000, // yield line (crossing)
    DuplicateSides      = 0x40000, // spawn on both sides independently (edge-line style)
    RequireSafeMaster   = 0x80000, // slave lane whose master lane is Unsafe
    RequireRoundabout   = 0x100000,  // only inside a roundabout
    RequireNotRoundabout= 0x200000,  // only outside a roundabout
}
```

---

## Appendix B: LaneCorner struct

```csharp
// SecondaryLaneSystem.cs:100-123
private struct LaneCorner
{
    public float3 m_StartPosition;   // right-edge position at lane start (relative to direction)
    public float3 m_EndPosition;     // right-edge position at lane end
    public float4 m_Tangents;        // xy = start tangent, zw = end tangent (normalized xz)
    public float2 m_Width;           // lane width at start (x) and end (y)
    public Entity m_Lane;            // the primary sublane entity
    public PathNode m_StartNode;
    public PathNode m_EndNode;
    public LaneFlags m_Flags;
    public bool m_Inverted;          // true for the backward-view copy of a lane
    public bool m_Duplicates;        // any entry in the prefab buffer has DuplicateSides
    public bool m_Hidden;            // true if the lane has no CullingInfo + has LaneGeometry
}
```

The `.zwxy` swizzle in tangent matching swaps (start,end) to (end,start), effectively asking: "does the other corner's curve flow in the reverse direction of mine?" — the canonical case for a divider between two opposing lanes.

---

## Appendix C: Key Burst constraint notes

- `UpdateLanesJob` is `[BurstCompile]` (line 154). No managed allocations, no string ops, no `List<T>`.
- `LaneBuffer` uses `NativeParallelHashMap`, `NativeList`, `NativeParallelHashSet` — all blittable.
- Our `MarkingOverride` struct is `IComponentData` (unmanaged) and is fine in Burst.
- Reading `m_MarkingOverrideData` (a `ComponentLookup<MarkingOverride>`) from inside the job is valid as long as it is declared `[ReadOnly]` in the job and passed in `OnUpdate`.
- Any new prefab entity you create at runtime must be pre-registered via `m_PrefabLaneArchetypeData` — the job reads `NetLaneArchetypeData` to find the archetype for `CreateEntity`. If you inject a completely new marking prefab at startup, ensure `NetInitializeSystem` has processed it before the first `UpdateLanesJob` run.
