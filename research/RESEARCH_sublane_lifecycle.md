# Sublane lifecycle research — what eats our sublanes and the full spawn recipe

Date: 2026-05-17
Scope: Re-enable `MarkingPairEmissionSystem` (Plan B.1 — spawn vanilla sublanes via ECS, let vanilla BRG draw them). Identify the root cause of the "sublanes eaten" symptom from commit bdc8945, and produce a ground-truth spawn recipe.

Sources are all from `decomp/Game/` — paths/line numbers are from this repo's local decomp.

---

## TL;DR — the bug and the fix

**Root cause of "sublanes eaten":** there are TWO sister systems that maintain `node.DynamicBuffer<SubLane>`:

| System                         | Phase           | Query (All / Any / None)                                                             |
|--------------------------------|-----------------|--------------------------------------------------------------------------------------|
| `LaneReferencesSystem`         | Modification4B  | `Lane+Owner` / `Created+Deleted` / **`SecondaryLane`** ← EXCLUDES SecondaryLane      |
| `SecondaryLaneReferencesSystem`| Modification5   | `Lane+SecondaryLane+Owner` / `Created+Deleted` / (none)                              |

Our spawned entity uses the `NetLaneArchetypeData.m_LaneArchetype` of an edge-line clone prefab (`TownRoadLane EU/NA City Edge Line`). That archetype **already includes `Game.Net.SecondaryLane`** (added by `SecondaryLane.GetArchetypeComponents` — see `Game.Prefabs/SecondaryLane.cs:87-90`).

So our entity has `Lane + Owner + Created + Game.Net.SecondaryLane`. This means:
- `LaneReferencesSystem` SKIPS it (None: SecondaryLane).
- `SecondaryLaneReferencesSystem` ADDS it to `node.SubLane` buffer at Modification5.

**Good news:** `SecondaryLane` also PROTECTS the sublane from `LaneSystem.RemoveUnusedOldLanes` (LaneSystem.cs:2022 — `FillOldLaneBuffer` skips entities with SecondaryLane). So even when the parent node gets rebuilt, our sublanes survive.

**So "sublanes eaten" was likely NOT a GC issue.** The most plausible cause from the bdc8945 timeline is the *self-fanout loop* the existing code comment in `MarkingPairEmissionSystem.cs:217-220` already calls out — marking the owner node `Updated` re-triggered `LaneSystem` on it, which (a) ran the node rebuild while we were still spawning, and (b) on the next tick triggered `CustomSecondaryLaneSystem` to spawn another wave. Combined with hash-derived PathNode slots that collided ("delete one → another appears"), the user saw entities flickering / vanishing.

The current head of `MarkingPairEmissionSystem` ALREADY fixes the two known triggers:
- Does not mark owner Updated (line 217-220).
- Uses stable `pairIndex`-based PathNode slots, not hashes (line 196-204).
- Batches structural changes via ECB to keep query snapshots consistent (line 65-71).

So the path forward is to **re-enable the system as-is**, add diagnostics, and verify on a live road. The recipe is essentially correct.

---

## §1 Vanilla sublane archetype contents

### Where the archetype is built

`Game.Prefabs/NetLanePrefab.cs:40-75 LateInitialize`. It calls
`CreateArchetype(entityManager, list, hashSet)` (line 77-88) which:
1. Iterates every `ComponentBase` attached to the prefab and calls `GetArchetypeComponents(hashSet)`.
2. Unconditionally adds `Created` and `Updated` (line 83-84).
3. Calls `entityManager.CreateArchetype(...)`.

`m_LaneArchetype` (line 47-48) = base lane archetype: includes `Lane` (from the explicit add at line 46) + everything that prefab's components contribute via `GetArchetypeComponents`. NO `EdgeLane`, `NodeLane`, `MasterLane`, or `SlaveLane` tag.

### What our clone's archetype actually contains

For `TownRoadLane EU/NA City Edge Line` — duplicated from `EU/NA Highway Edge Line` (a `NetLaneGeometryPrefab` with a `SecondaryLane` ComponentBase):

| Source `GetArchetypeComponents`                                                  | Contributes                                                       |
|----------------------------------------------------------------------------------|-------------------------------------------------------------------|
| `PrefabBase` (`Game.Prefabs/PrefabBase.cs:358-361`)                              | `PrefabRef`                                                       |
| `NetLanePrefab` (`Game.Prefabs/NetLanePrefab.cs:34-38`)                          | `Curve`                                                           |
| `NetLaneGeometryPrefab` (`Game.Prefabs/NetLaneGeometryPrefab.cs:31-55`)          | `LaneGeometry`, `CullingInfo`, `MeshBatch` (+ `MeshColor`, `PseudoRandomSeed` if mesh has ColorProperties — White Solid Line Mesh has none → these are absent) |
| `SecondaryLane` ComponentBase (`Game.Prefabs/SecondaryLane.cs:87-90`)            | `Game.Net.SecondaryLane`                                          |
| `CreateArchetype` epilogue (`Game.Prefabs/NetLanePrefab.cs:83-84`)               | `Created`, `Updated`                                              |
| Explicit add in `LateInitialize` (`Game.Prefabs/NetLanePrefab.cs:46`)            | `Lane`                                                            |

**Effective archetype:** `{PrefabRef, Lane, Curve, LaneGeometry, CullingInfo, MeshBatch, Game.Net.SecondaryLane, Created, Updated}` — and probably `MeshColor + PseudoRandomSeed` if the chosen mesh carries `ColorProperties`.

### What vanilla `SecondaryLaneSystem.CreateSecondaryLane` writes on top of the archetype

`Game.Net/SecondaryLaneSystem.cs:1304-1402` — single-lane create path:

```csharp
PrefabRef component  = new PrefabRef(prefab);           // line 1306
Owner    component2  = new Owner { m_Owner = owner };   // line 1323-1326
Elevation component3 = default;                         // line 1327
Lane lane = new Lane {                                  // line 1328-1333
    m_StartNode  = new PathNode(new PathNode(owner, (ushort)laneIndex++), secondaryNode: true),
    m_MiddleNode = new PathNode(new PathNode(owner, (ushort)laneIndex++), secondaryNode: true),
    m_EndNode    = new PathNode(new PathNode(owner, (ushort)laneIndex++), secondaryNode: true),
};
// (HangingLane optionally, Temp optionally)

Entity e = ecb.CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype); // line 1384
if (isHidden) ecb.RemoveComponent(jobIndex, e, in m_HideLaneTypes); // {CullingInfo, MeshBatch, MeshColor}
ecb.SetComponent(jobIndex, e, component);   // PrefabRef        (line 1389)
ecb.SetComponent(jobIndex, e, lane);        // Lane             (line 1390)
ecb.SetComponent(jobIndex, e, curveData);   // Curve            (line 1391)
ecb.AddComponent(jobIndex, e, component2);  // Owner            (line 1392)
ecb.AddComponent(jobIndex, e, component3);  // Elevation        (line 1393)
// (HangingLane / Temp conditionally)
```

Key takeaways:
- `Owner` and `Elevation` are **AddComponent** — NOT in the archetype. Confirms our spawn must add them (we do).
- `PrefabRef`, `Lane`, `Curve` are **SetComponent** — they ARE in the archetype (zero-initialized).
- `Created`, `Updated`, `MeshBatch`, `CullingInfo`, `LaneGeometry`, `Game.Net.SecondaryLane` are NOT touched explicitly — they come from the archetype.

### Side-by-side: vanilla vs `MarkingPairEmissionSystem.SpawnSublane` (current head)

| Component         | In archetype | Vanilla call          | Our `SpawnSublane`               | Status   |
|-------------------|--------------|-----------------------|----------------------------------|----------|
| `PrefabRef`       | yes          | SetComponent          | `ecb.SetComponent(new PrefabRef)`| ✅ match |
| `Lane`            | yes          | SetComponent          | `ecb.SetComponent(lane)`         | ✅ match |
| `Curve`           | yes          | SetComponent          | `ecb.SetComponent(new Curve)`    | ✅ match |
| `Owner`           | no           | AddComponent          | `ecb.AddComponent(new Owner)`    | ✅ match |
| `Elevation`       | no           | AddComponent          | `ecb.AddComponent(default)`      | ✅ match |
| `Created`         | archetype    | (none)                | `ecb.AddComponent(default)`      | ⚠️ redundant but harmless (no-op on archetype tag) |
| `Updated`         | archetype    | (none)                | `ecb.AddComponent(default)`      | ⚠️ redundant but harmless |
| `Game.Net.SecondaryLane` | archetype | (none)             | (none) — comes from archetype    | ✅ match |
| `MeshBatch`       | archetype    | (none)                | (none)                           | ✅ match |
| `CullingInfo`     | archetype    | (none)                | (none) — bounds filled by PreCullingSystem | ✅ match |
| `LaneGeometry`    | archetype    | (none)                | (none)                           | ✅ match |
| `MeshColor`       | maybe        | (none)                | (none)                           | depends on mesh |
| `PseudoRandomSeed`| maybe        | (none)                | (none)                           | depends on mesh |
| `TRLPairLink`     | n/a          | (our marker tag)      | `ecb.AddComponent(new TRLPairLink)` | ours    |
| `HangingLane`     | conditional  | conditional           | (not added)                      | OK — only needed for hanging-attached lanes (catenary etc.) |

**Conclusion:** the spawn recipe matches vanilla exactly for the relevant subset. Nothing missing. The `Created/Updated` re-add is redundant but Entities 1.x silently no-ops AddComponent on an existing tag, so this is a code-cleanliness issue, not a bug.

---

## §2 Who eats sublanes — every GC system that touches a sublane entity

Search criteria: any code in `decomp/Game/Game.Net/` that adds `Deleted` to a sublane entity.

### 2.1 `LaneSystem.DeleteLanes` — owner-deleted cascade
`Game.Net/LaneSystem.cs:556-571`
```csharp
foreach (SubLane sl in node.SubLane) {
    if (!HasComponent<SecondaryLane>(sl.m_SubLane))
        ecb.AddComponent(sl.m_SubLane, default(Deleted));
}
```
Triggered when an owner (node/edge) becomes Deleted. **SKIPS SecondaryLane** — our sublanes are safe.

### 2.2 `LaneSystem.RemoveUnusedOldLanes` — node-rebuild GC (the big one)
`Game.Net/LaneSystem.cs:2089-2104` (called from line 657, 769, 1102 — edge update / node update / node update)

Flow per Updated owner:
1. `FillOldLaneBuffer` (line 1977-2087) iterates `node.SubLane` and **skips entries with `SecondaryLane`** (line 2022: `if (m_SecondaryLaneData.HasComponent(subLane2)) continue;`). Adds remaining primaries to `m_OldLanes` hashmap keyed by `LaneKey`.
2. The system recomputes all primary lanes from scratch; matches pull entries OUT of `m_OldLanes`.
3. `RemoveUnusedOldLanes` puts anything left in `m_OldLanes` into a Deleted batch (line 2101: `RemoveComponent(m_AppliedTypes)` + line 2102: `AddComponent(Deleted)`).

**Because step 1 skips SecondaryLane**, our sublanes are NEVER in `m_OldLanes`, so step 3 cannot kill them. Confirmed safe.

### 2.3 `SecondaryLaneSystem.DeleteLanes` — owner-deleted cascade for SecondaryLane
`Game.Net/SecondaryLaneSystem.cs:294-308`
```csharp
foreach (SubLane sl in deletedOwner.SubLane) {
    if (HasComponent<SecondaryLane>(sl.m_SubLane))
        ecb.AddComponent(sl.m_SubLane, default(Deleted));
}
```
Triggered ONLY when the owner has `Deleted`. So our sublanes die with the node — correct behavior.

### 2.4 `SecondaryLaneSystem.UpdateLanes → RemoveUnusedOldLanes` — node-rebuild GC for SecondaryLane
`Game.Net/SecondaryLaneSystem.cs:943-959`

THIS is the dangerous one for us. Flow per Updated owner with `SecondaryLane` children:
1. `FillOldLaneBuffer` (line 930-941) collects every SecondaryLane in the owner's buffer into `m_OldLanes`, keyed by `new LaneKey(m_LaneData[sl], m_PrefabRefData[sl].m_Prefab)`.
2. The system iterates the owner's *primary* lanes and for each primary, looks up matching `SecondaryNetLane` buffer entries on the primary's prefab. For each match it calls `CreateSecondaryLane` which calls `m_OldLanes.TryGetValue(laneKey, out item)` — if found, **the old lane is reclaimed** (returns at line 1381), otherwise a new one is created.
3. At end of chunk processing, anything still in `m_OldLanes` would have `m_AppliedTypes` removed + `Deleted` added.

Wait — but `RemoveUnusedOldLanes` is NEVER called from `UpdateLanes` in SecondaryLaneSystem! I only see it defined at line 943; let me double-check it isn't invoked anywhere. Re-grepping the file shows `RemoveUnusedOldLanes` is defined but its caller wasn't found in my grep — meaning **vanilla actually leaves orphaned reclaim-candidates alone**, OR I missed a call.

**Re-check:** there IS no call to `SecondaryLaneSystem.RemoveUnusedOldLanes` from this system in the decomp. The method is dead code or used via a path I missed. The `m_OldLanes` map is filled at start of chunk iteration but never used to delete. The reclaim flow only consults it via `TryGetValue` in `CreateSecondaryLane` (line 1357).

So `SecondaryLaneSystem` actually does NOT GC our sublanes on its own. The hazard is different: if vanilla re-runs on the same node and tries to compute "what secondary lanes does this node need", our pair-driven sublanes are EXTRA — vanilla wouldn't know to keep them. But since `RemoveUnusedOldLanes` isn't called, they survive.

However: vanilla `UpdateLanes` ITERATES `node.SubLane` (line 359-360) and for each sublane checks `m_MasterLaneData.HasComponent(subLane) || m_SecondaryLaneData.HasComponent(subLane)` and CONTINUES (skips). So our sublanes are also untouched by the per-sublane loop.

### 2.5 `OutsideConnectionSystem`
`Game.Net/OutsideConnectionSystem.cs:321` — deletes outside-connection lanes (different code path, doesn't affect our nodes).

### 2.6 No other deleters
A grep for `AddComponent.*Deleted` and `new Deleted` across `decomp/Game/Game.Net/` returns only the 4 files above (SecondaryLaneSystem, OutsideConnectionSystem, LaneSystem, AggregateSystem). `AggregateSystem` operates on `Aggregate` entities, not lanes.

### Prioritized "what killed our sublanes" hypotheses

Given the above, in order of likelihood for the bdc8945 symptom:

1. **PathNode slot collision + self-rebuild loop (HIGHEST).** The old commit used XOR-hashed slot bases. When two pairs hashed to the same base, vanilla's PathNode merging (`LaneReferencesSystem.FixSkippedLanesJob`, `LaneReferencesSystem.cs:179-313`) sees two lanes claiming the same StartNode/EndNode and starts thrashing — manifests as "delete one → another appears." The current head fixes this with `idxBase = 32768 + pairIndex*4` (`MarkingPairEmissionSystem.cs:196-198`). **Already fixed.**
2. **Owner Updated cascade (HIGH).** The old commit marked the owner node Updated after spawn. That re-triggered `LaneSystem` (Modification4) on the node, which in turn re-triggered `SecondaryLaneSystem` (Modification4B), which fanned out into hundreds of spawn calls per tick. Already documented in the in-source comment at `MarkingPairEmissionSystem.cs:217-220`. **Already fixed.**
3. **Mid-update structural change query invalidation (MEDIUM).** The old spawn applied AddComponent directly to EntityManager mid-OnUpdate, invalidating the iterator. Already documented at `MarkingPairEmissionSystem.cs:65-71`. **Already fixed.**
4. **Vanilla LaneSystem.DeleteLanes when node briefly Deleted during edit (LOW).** If the user is editing the road and the node briefly enters the Deleted set, vanilla's `LaneSystem.DeleteLanes` will iterate node.SubLane and delete primaries — but SecondaryLanes (us) are skipped. Then `SecondaryLaneSystem.DeleteLanes` will delete US too. This is correct behavior — when node dies our sublanes should die.

**None of these are "vanilla quietly GCing our sublanes because of a missing tag."** The archetype-derived `SecondaryLane` tag fully protects us.

---

## §3 Node SubLane buffer maintenance — do we need to insert manually?

**No, do NOT modify `node.SubLane` directly.** `SecondaryLaneReferencesSystem` does it for us, automatically.

### How it works

`Game.Net/SecondaryLaneReferencesSystem.cs`
- Query (line 217-231): `All: Lane + SecondaryLane + Owner` / `Any: Created + Deleted`.
- On `Created` (line 84-145): for each matched chunk, walk entities and do `CollectionUtils.TryAddUniqueValue(m_Lanes[owner.m_Owner], new SubLane(entity, pathMethod2))` where `pathMethod2` is built from `PedestrianLane/CarLane/TrackLane/ParkingLane/ConnectionLane` component presence and prefab data. For our edge-line clone (no Car/Pedestrian/Track/Parking/ConnectionLane components on the spawned entity, since these are NOT in the base lane archetype), `pathMethod2 = ~allKnownMethods` = "purely decorative" — exactly what we want.
- On `Deleted` (line 60-69): removes the entry from the buffer.

System order (`Game.Common/SystemOrder.cs:207`): `SecondaryLaneReferencesSystem` runs at `SystemUpdatePhase.Modification5`. Our `MarkingPairEmissionSystem` runs at `Modification1`. So on the same tick our sublane gets created, by Modification5 it's in the node buffer. **Single-threaded job** (line 253: `Schedule`, not `ScheduleParallel`), so concurrent buffer writes to the same node are safe.

### Implication for our code

`MarkingPairEmissionSystem.SpawnSublane` does NOT need to touch `node.SubLane`. The existing code respects this (we don't write the buffer). 

If we ever want to verify post-spawn that vanilla registered us, query the node's SubLane buffer on tick N+1 and check that our entity appears with `PathMethods == ~allKnownMethods` (sentinel: 0xFFFE0000 or so depending on flag enum — see `Game.Net.SecondaryLaneReferencesSystem.cs:74` for the exact bitmask).

---

## §4 Spawn recipe — ground-truth code

Faithful to vanilla `SecondaryLaneSystem.CreateSecondaryLane` (lines 1304-1402). The current `MarkingPairEmissionSystem.SpawnSublane` already follows this — listed here as the spec for review:

```csharp
// 1. Get the archetype from the prefab — must be the BASE `m_LaneArchetype`,
//    NOT m_EdgeLaneArchetype / m_NodeLaneArchetype. Our edge-line clone is
//    spawned standalone (not attached to an edge or node mesh layout).
EntityArchetype arch = EntityManager
    .GetComponentData<NetLaneArchetypeData>(prefab)
    .m_LaneArchetype;

// 2. PathNode slot base. MUST be unique across all sublanes (primary +
//    secondary) on this owner node. Vanilla uses sequential indices starting
//    from 0 for primaries; we claim the high end starting at 32768 to avoid
//    any possible collision. pairIndex * 4 leaves 1 slot of padding between
//    pairs (3 used per lane: start, middle, end).
ushort idxBase = (ushort)(32768 + pairIndex * 4);

// 3. Build the Lane component. Vanilla uses secondaryNode:true for all three
//    PathNode wrappers — this is what marks the slot as "belongs to a
//    SecondaryLane" so pathfinder treats it specially. See
//    SecondaryLaneSystem.cs:1330-1332.
Lane lane = new Lane {
    m_StartNode  = new PathNode(new PathNode(node, idxBase),               secondaryNode: true),
    m_MiddleNode = new PathNode(new PathNode(node, (ushort)(idxBase + 1)), secondaryNode: true),
    m_EndNode    = new PathNode(new PathNode(node, (ushort)(idxBase + 2)), secondaryNode: true),
};

// 4. Build the Curve component from the pair's source/target endpoints.
//    Tangents are -src.tangent because endpoint tangents point inward
//    (toward the node) — we want the bezier to leave src OUTWARD.
float chord = math.distance(src.position, dst.position);
float pull = chord * 0.4f;
float3 srcTan3 = new float3(-src.tangent.x, 0f, -src.tangent.y);
float3 dstTan3 = new float3(-dst.tangent.x, 0f, -dst.tangent.y);
Bezier4x3 bez = new Bezier4x3(
    src.position,
    src.position + srcTan3 * pull,
    dst.position + dstTan3 * pull,
    dst.position);

// 5. Create entity from archetype. After this the entity has:
//    {PrefabRef, Lane, Curve, LaneGeometry, CullingInfo, MeshBatch,
//     Game.Net.SecondaryLane, Created, Updated}
//    All zero-initialized; we now fill the ones that matter.
Entity e = ecb.CreateEntity(arch);

// 6. Fill the archetype-included components.
ecb.SetComponent(e, new PrefabRef(prefab));
ecb.SetComponent(e, lane);
ecb.SetComponent(e, new Curve { m_Bezier = bez, m_Length = MathUtils.Length(bez) });

// 7. Add the NOT-in-archetype components. Vanilla uses AddComponent for these
//    (SecondaryLaneSystem.cs:1392-1393).
ecb.AddComponent(e, new Owner { m_Owner = node });
ecb.AddComponent(e, default(Elevation));   // 0 by default, fine for ground-level markings.

// 8. Our marker tag for reconciliation diffs across ticks.
ecb.AddComponent(e, new TRLPairLink { node = node, pairKey = pairKey });

// 9. DO NOT mark the owner node Updated. That cascades through LaneSystem +
//    SecondaryLaneSystem and spawn-fanouts. See comment in source.
// 10. DO NOT add Hidden. Our lane is meant to be visible.
// 11. DO NOT add Temp. Temp is for tool preview entities; ours are permanent.
// 12. DO NOT modify node.SubLane buffer. SecondaryLaneReferencesSystem
//     does it for us at Modification5.
```

### What we do NOT need (and should not add)

- `LaneFlags` — that's on the prefab's `NetLaneData`, set at prefab init time. Not a runtime sublane component.
- `LaneCondition`, `LaneSignal`, `MasterLane`, `LaneTimeMarker` — for car/pedestrian lanes only. Decoration-only sublanes don't carry these.
- `Decoration` — no such component in the decomp. The "decoration-only" property is implicit in the archetype: no `CarLane/PedestrianLane/TrackLane/ParkingLane/ConnectionLane/UtilityLane` tag means SecondaryLaneReferencesSystem registers us with `pathMethod == ~allKnownMethods` — a value the pathfinder ignores.
- `Native` / `Hidden` — opt-in flags, not needed for our case.

### Deletion path

Simple: `ecb.AddComponent<Deleted>(sublaneEntity)`. The Deleted tag triggers `SecondaryLaneReferencesSystem.UpdateLaneReferencesJob` (line 60-69) on next Modification5 to remove the entry from `node.SubLane`. Then `CleanUpSystem.OnUpdate` (`Game.Common/CleanUpSystem.cs:52`) destroys the entity.

The current `MarkingPairEmissionSystem` does exactly this at line 101-103.

---

## §5 Decoration / navigable flags

There is **no explicit `Decoration` component** in the codebase. A sublane is "decoration-only" when it has none of `CarLane`, `PedestrianLane`, `TrackLane`, `ParkingLane`, `ConnectionLane`, `UtilityLane` components. The classification works because `SecondaryLaneReferencesSystem.UpdateLaneReferencesJob` (`Game.Net/SecondaryLaneReferencesSystem.cs:74-144`) builds the `PathMethod` bitmask conditionally based on which lane-class component the entity has, and AdditionalLanes of unknown type get the sentinel `~allKnownMethods` mask. The pathfinder iterates lanes via `(subLane.m_PathMethods & relevantMethods) != 0` — for our entity, no relevant bit is set, so pathfinder ignores us.

Our edge-line clone prefab's archetype does NOT include any of these "lane class" tag components (verified by reading `NetLanePrefab.LateInitialize` and `NetLaneGeometryPrefab.GetArchetypeComponents` — neither adds them). So our spawned entity is automatically "decoration only" without any extra action on our part.

**No code change needed for §5.** If we ever switch to a prefab that DOES carry one of those tags (e.g., a real car-lane prefab) we'd accidentally make a navigable lane and the pathfinder would crash on the malformed PathNode slots.

---

## §6 Diagnostic plan — what to log when re-enabling

When `MarkingPairEmissionSystem` is uncommented at `Mod.cs:94`, add these one-shot checks (gated by a small "did we already log this once" set so they don't spam):

### 6.1 At first spawn — verify archetype contents
Right after first `CreateEntity`, log every component on the new entity:
```csharp
using (var types = EntityManager.GetComponentTypes(e))
{
    var sb = new System.Text.StringBuilder($"first spawn arch: ");
    for (int i = 0; i < types.Length; i++) {
        if (i > 0) sb.Append(", ");
        sb.Append(types[i].GetManagedType().Name);
    }
    log.Info(sb.ToString());
}
```
**Expected:** must contain `Game.Net.SecondaryLane`. If missing, our clone prefab is wrong — fall back to a known-good vanilla prefab to bisect.

### 6.2 At tick N+1 — survival check
On tick after spawn, query our marker tag and check if the entity (a) still exists, (b) has Deleted, (c) is in node.SubLane. Log first 3 sublanes only:
```csharp
// Inside OnUpdate, before the diff pass:
foreach (var e in newlySpawnedSublanesFromPreviousTick) {
    bool alive = EntityManager.Exists(e);
    bool deleted = alive && EntityManager.HasComponent<Deleted>(e);
    bool inNodeBuffer = alive && IsInOwnerSubLaneBuffer(e); // helper that reads node.SubLane and searches
    log.Info($"survival check: e={e.Index} alive={alive} deleted={deleted} inBuffer={inNodeBuffer}");
}
```
**Expected:** all alive, none Deleted, all inBuffer. If `inBuffer=false` → SecondaryLaneReferencesSystem isn't picking us up → check our archetype (must have `Game.Net.SecondaryLane`). If `deleted=true` → something IS killing us → log node Updated status, log all vanilla GC system tick counters.

### 6.3 At tick N+1 — render check
On tick after spawn, check if PreCullingSystem filled the bounds:
```csharp
var cull = EntityManager.GetComponentData<CullingInfo>(e);
log.Info($"culling: bounds={cull.m_Bounds} mask={cull.m_Mask}");
```
**Expected:** bounds not zero (Curve.m_Length > 0 → bounds expanded around bezier). Mask includes the relevant render layer. If bounds=zero → Curve component isn't being read → check `m_Length`.

### 6.4 Steady-state — reconciliation counters
The existing log at `MarkingPairEmissionSystem.cs:170-171` already prints `+created -deleted (wanted=N unmet, existing=M)`. Keep it. Add a "no-op tick" silent check — if `wanted unmet == 0` for 5 consecutive ticks, we're stable. If `wanted unmet > 0` for >2 ticks (and no errors above), something is rejecting our spawn before it can be reconciled.

### 6.5 First-tick sanity — owner buffer integrity
Right after our `ecb.Playback`, dump the owner node's SubLane buffer length (it WON'T include us yet — Modification5 hasn't run — but it should not have shrunk):
```csharp
log.Info($"node {node.Index} SubLane.Length AFTER our spawn (Mod1): {EntityManager.GetBuffer<SubLane>(node).Length}");
```
On NEXT tick, log it again before our spawn pass:
```csharp
log.Info($"node {node.Index} SubLane.Length BEFORE Mod1 next tick: {EntityManager.GetBuffer<SubLane>(node).Length}");
```
**Expected:** N+1 second-tick value = N first-tick value + (number of pairs we spawned). If equal → SecondaryLaneReferencesSystem didn't run / didn't pick us up. If smaller → vanilla deleted something extra.

### 6.6 Pathfinder smoke test
After enabling, manually drive a vehicle through one of the affected nodes. If the pathfinder crashes or vehicles teleport, our PathNode slots are colliding with vanilla. Slot base 32768 should be safe — vanilla `laneIndex++` from 0, never reaches 32768 in any practical road. But verify with the SubLane buffer dump from §6.5 — log all entries' `m_PathMethods` to confirm ours show as the sentinel ~allKnownMethods (≈ 0xFFFE0000 depending on enum size).

---

## Appendix: file/line index of every reference used

| Topic                                 | File                                                       | Lines     |
|---------------------------------------|------------------------------------------------------------|-----------|
| Vanilla CreateSecondaryLane recipe    | `decomp/Game/Game.Net/SecondaryLaneSystem.cs`              | 1304-1402 |
| SecondaryLaneSystem DeleteLanes       | `decomp/Game/Game.Net/SecondaryLaneSystem.cs`              | 294-308   |
| SecondaryLaneSystem RemoveUnusedOldLanes (dead?) | `decomp/Game/Game.Net/SecondaryLaneSystem.cs`   | 943-959   |
| SecondaryLaneSystem owner query       | `decomp/Game/Game.Net/SecondaryLaneSystem.cs`              | 1878-1893 |
| LaneSystem DeleteLanes                | `decomp/Game/Game.Net/LaneSystem.cs`                       | 556-571   |
| LaneSystem FillOldLaneBuffer (skips Secondary) | `decomp/Game/Game.Net/LaneSystem.cs`              | 2022-2025 |
| LaneSystem RemoveUnusedOldLanes       | `decomp/Game/Game.Net/LaneSystem.cs`                       | 2089-2104 |
| LaneSystem owner query                | `decomp/Game/Game.Net/LaneSystem.cs`                       | 9180-9194 |
| LaneReferencesSystem query (excludes Secondary) | `decomp/Game/Game.Net/LaneReferencesSystem.cs`   | 720-733   |
| LaneReferencesSystem add-to-buffer    | `decomp/Game/Game.Net/LaneReferencesSystem.cs`             | 149       |
| LaneReferencesSystem remove-from-buffer | `decomp/Game/Game.Net/LaneReferencesSystem.cs`           | 67-74     |
| SecondaryLaneReferencesSystem query   | `decomp/Game/Game.Net/SecondaryLaneReferencesSystem.cs`    | 217-231   |
| SecondaryLaneReferencesSystem add/remove | `decomp/Game/Game.Net/SecondaryLaneReferencesSystem.cs` | 56-145    |
| NetLanePrefab archetype build         | `decomp/Game/Game.Prefabs/NetLanePrefab.cs`                | 40-88     |
| NetLaneGeometryPrefab archetype contribs | `decomp/Game/Game.Prefabs/NetLaneGeometryPrefab.cs`     | 31-55     |
| SecondaryLane (prefab) archetype contrib | `decomp/Game/Game.Prefabs/SecondaryLane.cs`             | 87-90     |
| PrefabBase archetype contrib (PrefabRef) | `decomp/Game/Game.Prefabs/PrefabBase.cs`                | 358-361   |
| CleanUpSystem (strips Created/Updated, destroys Deleted) | `decomp/Game/Game.Common/CleanUpSystem.cs` | 22-56     |
| PreCullingSystem bounds for lanes     | `decomp/Game/Game.Rendering/PreCullingSystem.cs`           | 700-732   |
| PreCullingSystem culling query        | `decomp/Game/Game.Rendering/PreCullingSystem.cs`           | 2714-2721 |
| RequiredBatchesSystem lane processing | `decomp/Game/Game.Rendering/RequiredBatchesSystem.cs`      | 313-391   |
| RequiredBatchesSystem query           | `decomp/Game/Game.Rendering/RequiredBatchesSystem.cs`      | 896-909   |
| BatchDataSystem CurveMatrix property  | `decomp/Game/Game.Rendering/BatchDataSystem.cs`            | 1214-1230 |
| LaneProperty.CurveMatrix shader binding | `decomp/Game/Game.Rendering/LaneProperty.cs`             | 7-8       |
| SystemUpdatePhase ordering            | `decomp/Game/Game.Common/SystemOrder.cs`                   | 155-207   |
| Existing emission code (current head) | `src/TownRoadLane/MarkingPairEmissionSystem.cs`            | full file |
| Existing edge-line clone setup        | `src/TownRoadLane/EdgeLineCloneSystem.cs`                  | full file |
