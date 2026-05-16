# RESEARCH: Road Builder Interaction

_Researched 2026-05-16. Sources: `_refs/RoadBuilder/` (full source tree), git commit `5bb0b5e`, current v2 mod files._

---

## 1. What RB does at prefab level

### Road prefab construction

RB does **not** clone vanilla road prefabs. It constructs every RB road from scratch as a
`RoadBuilderPrefab : RoadPrefab` instance whose name is set to a generated GUID-based ID:

```
// NetworkPrefabGenerationUtil.cs:119
prefab.name = cfg.ID = $"{prefab.GetType().Name.ToLower()[0]}{Guid.NewGuid()}-{PlatformManager.instance.userSpecificPath}";
```

Result: a road prefab name like `r<guid>-<steamid>`, e.g. `r3f7a1b2c-...-76561198123456789`.

### Sections: vanilla or cloned?

RB builds its road's `m_Sections` by stitching together `NetSectionPrefab` objects. The crucial question
is whether those section prefabs are the **vanilla objects** or **clones**:

- **Vanilla car-drive sections are used directly** when RB selects a section by name from its internal
  dictionary (e.g. `"Car Drive Section 3"` → `NetSections["Car Drive Section 3"]`). No clone is made
  for car-drive sections in `RoadBuilderNetSectionsSystem`.

  `CarGroupPrefab.cs:84`:
  ```csharp
  SetUp(Sections["Car Drive Section 3"], "3m", "")
  ```
  The `SetUp` method calls `.AddOrGetComponent<RoadBuilderLaneGroup>()` on the **same prefab object**,
  adding an RB metadata component to the vanilla section — it does not clone it.

- **Some sections ARE cloned** by `RoadBuilderNetSectionsSystem` when RB needs a variant that doesn't
  exist in vanilla (e.g. `"RB Tiled Drive Section 3 - Car"`, parking sections `"RB Parking Piece
  Parallel"`, `"RB Tiled Median 0"`, etc.).

  `RoadBuilderNetSectionsSystem.cs:375`:
  ```csharp
  var newPiece = (NetPieces["Car Drive Piece 3"].Clone(item.Item1) as NetPiecePrefab)!;
  ```
  However, clones of **pieces** (not sections) don't carry the `SecondaryLane` component — that lives
  on `NetLanePrefab` objects, not on `NetPiecePrefab` or `NetSectionPrefab`.

### Marking prefabs

RB **does not touch** vanilla marking prefabs (`EU/NA Highway Edge Line`, `EU/NA Car Bay Line`, etc.)
at all. There is zero reference to `SecondaryLane` or `SecondaryNetLane` anywhere in the RB source.

**Conclusion:** The `SecondaryLane` component on marking prefabs (which our mod patches) is inherited
through the vanilla `NetLanePrefab` objects that vanilla `NetSectionPrefab` objects reference. Because
RB uses vanilla `NetSectionPrefab` objects (like `"Car Drive Section 3"`) directly rather than cloning
them, any patch our mod applies to marking prefabs (adding `SecondaryLaneInfo` entries for `'Car Drive
Lane 3'`) **will propagate into RB roads** that use the 3 m car-drive section.

---

## 2. RB road identification at runtime

### Prefab name pattern

The most reliable heuristic already in use across both v1 `MarkingReapplySystem` and v2
`MarkingToggleSystem`:

- Prefab name starts with `r` or `R`
- Contains ≥ 4 dash characters

This matches `r<guid>-<steamid>` (GUID has 4 dashes, so total dashes ≥ 4).

```csharp
// MarkingReapplySystem.cs — LooksLikeRoadBuilder()
if (name[0] != 'r' && name[0] != 'R') return false;
int dashes = 0;
foreach (var c in name) if (c == '-') dashes++;
return dashes >= 4;
```

### ECS components on the edge entity

RB adds `RoadBuilderNetwork : IComponentData` to road edge entities (and their start/end nodes) when
the user edits a road with RB:

```csharp
// RoadBuilderSystem.cs:226
EntityManager.TryAddComponent<RoadBuilderNetwork>(entity);
```

`RoadBuilderNetwork` is an empty marker struct (`IEmptySerializable`). However, it is **only
added when the road is actively edited** — it may not be present on all RB roads in a save that
was not touched during the current session.

### ECS components on the prefab entity

`RoadBuilderPrefab.GetPrefabComponents` adds `RoadBuilderPrefabData` (also an empty marker struct) to
the ECS entity for every non-deleted RB road prefab. This is the most reliable test:

```csharp
// RoadBuilderPrefab.cs:25
components.Add(ComponentType.ReadWrite<RoadBuilderPrefabData>());
```

**Practical detection priority:**

| Method | Reliability | Cost |
|--------|------------|------|
| Prefab name heuristic (≥4 dashes) | High — consistent with name generation | PrefabSystem lookup per edge |
| `RoadBuilderPrefabData` on prefab entity | Definitive — set at prefab creation time | Requires component lookup on prefab entity |
| `RoadBuilderNetwork` on edge entity | Partial — only present after user edits | Direct component check |

For `CustomSecondaryLaneSystem` (Burst job context): a prefab-entity component lookup
(`m_PrefabRoadBuilderData`) is the cleanest approach if ever needed inside the job. The name heuristic
is already proven and works on the main thread.

---

## 3. Lifecycle interaction

### When does RB register its prefabs?

```csharp
// RoadBuilder/Mod.cs
updateSystem.UpdateAfter<RoadBuilderGenerationDataSystem, PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
updateSystem.UpdateAt<RoadBuilderInitializerSystem>(SystemUpdatePhase.MainLoop);
```

- `RoadBuilderGenerationDataSystem` runs in `PrefabUpdate` **after** `PrefabInitializeSystem`.
- `RoadBuilderInitializerSystem` runs in `MainLoop` — this is the system that calls
  `roadBuilderSystem.AddPrefab(config)` for each saved RB road. It self-disables after first run.
- `RoadBuilderSystem` (which calls `prefabSystem.UpdatePrefab`) runs in `Modification1`.

**Our mod (v2) runs at:**
```
SystemUpdatePhase.Modification4B   ← CustomSecondaryLaneSystem (replacement of vanilla)
SystemUpdatePhase.PrefabUpdate     ← MarkingOverridePatchSystem (patches vanilla marking prefabs)
```

Timeline per game load:

```
PrefabUpdate phase:
  PrefabInitializeSystem              ← vanilla prefabs baked (CarDriveLane3, EdgeLine prefabs, etc.)
  RoadBuilderGenerationDataSystem     ← RB collects available vanilla sections/pieces into dictionaries
  MarkingOverridePatchSystem          ← OUR MOD patches EU/NA Highway Edge Line (adds CarDriveLane3 refs)
  PrefabSystem.UpdatePrefab(edgeLine) ← triggers "Updated" on marking prefab entities

MainLoop (game frame tick):
  RoadBuilderInitializerSystem        ← RB calls AddPrefab() for each saved config → prefabSystem.AddPrefab()
                                         → prefab enters PrefabUpdate queue for next frame

Next PrefabUpdate frame:
  PrefabInitializeSystem              ← bakes new RB road prefabs
  ...
Modification1 (ongoing):
  RoadBuilderSystem                   ← processes _updatedRoadPrefabsQueue, calls prefabSystem.UpdatePrefab()
  RoadBuilderPrefabUpdateSystem       ← on RB prefab Updated: marks affected edges Updated
```

**Race condition:** Our `MarkingOverridePatchSystem` runs before `RoadBuilderInitializerSystem`. When
RB road prefabs are created (in `MainLoop` / next `PrefabUpdate`), they reference the already-patched
vanilla `NetSectionPrefab` objects (which in turn reference `NetLanePrefab` objects whose
`SecondaryNetLane` buffer already contains our added `SecondaryLaneInfo` entries). So RB roads will
**automatically inherit** our edge-line markings on the 3 m car-drive lanes with no timing issue.

There is no race condition for the initial load. The only scenario where ordering could matter is
`UpdatePrefab` on marking prefabs happening while RB is simultaneously re-generating a road — but
both run on the main thread sequentially and this has not caused observed issues.

---

## 4. The v1.1 skip-RB-edges incident

### What happened

Commit `5bb0b5e` message (condensed):

> "The remaining crash was the 'Reapply markings' button: it marked all 1342 road edges Updated →
> SecondaryLaneSystem re-laid out the RB roads among them → crash. Now Reapply skips edges of Road
> Builder roads (prefab name `r<guid>-<steamid>`); RB roads pick up a style change on the next game
> load like styles always do."

### Root cause

In v1, `MarkingReapplySystem` mass-added `Updated` to **every** road edge. This caused vanilla
`SecondaryLaneSystem` to re-process RB road edges. At least one RB road in the test save was
highway-based with angled parking (an unusual combination). Re-running the `SecondaryLaneSystem` lane
layout on that edge's `SubLane` buffer produced a crash — presumably from an inconsistent internal
state or an invariant violation in the vanilla system when operating on RB-constructed lane geometry.

### The fix

The fix added a `LooksLikeRoadBuilder(name)` heuristic: edges whose road prefab name matches the RB
pattern are skipped by the "Reapply" operation. This is the same heuristic that was later copied into
v2's `MarkingToggleSystem`.

### Key implication

The crash was caused by **re-processing** RB lanes through `SecondaryLaneSystem`, not by RB roads
inheriting our markings during normal play. The initial game load (where each edge is processed once
at `Updated` from normal prefab initialization) was fine. Only the mass-reapply triggered it.

---

## 5. v2 Risk Assessment

### Will RB roads inherit our patched markings?

**Yes — and this is expected and probably wanted.**

Because RB uses vanilla `NetSectionPrefab` objects (`"Car Drive Section 3"`) directly (not clones),
and we patch the marking prefabs (`EU/NA Highway Edge Line`) that reference `'Car Drive Lane 3'`
inside those sections, RB 3 m car-drive roads will display our edge-line markings automatically.

- RB 3 m car lanes without `m_HighwayRules = true` → our edge line appears (wanted for city-style RB roads).
- RB highway-category roads (`m_HighwayRules = true`, `"Highway Drive Section 4"`) → already had the
  edge line in vanilla; our patch does not affect them because `'Highway Drive Lane 4'` is unchanged.

The only case where this might be unwanted: an RB road with 3 m car lanes that the user deliberately
configured without edge markings (the RB UI has a "No Markings" option per lane group,
`CarGroupPrefab.cs:94`: `private NetSectionPrefab SetUp(..., bool noMarkings = false)`). When
`noMarkings = true`, RB uses `"Alley Drive Section 3"` instead of `"Car Drive Section 3"`. Our patch
only affects `'Car Drive Lane 3'` — the alley section uses a different lane prefab — so the RB "No
Markings" option **already excludes** our edge line naturally.

### Do we need a skip-RB-edges guard in CustomSecondaryLaneSystem?

**Not for the normal lane-layout path.** The v1 crash was specific to a mass-Updated operation
forcing re-processing of all edges simultaneously. Our `CustomSecondaryLaneSystem` replaces vanilla
and processes edges one at a time via normal `Updated` events — this is the same path that worked
fine in v1 day-to-day play.

The existing guards in `MarkingToggleSystem` (which also mass-marks entities `Updated`) already
skip RB edges with the proven heuristic, reproducing the v1 protection. That is sufficient.

### Does our MarkingOverride system need RB-awareness for phase 4?

Phase 4 (per-segment toggle tool) will add/remove `MarkingOverride` on individual edges. There is no
reason to special-case RB roads there: `MarkingOverride` is just a component on the edge entity;
`CustomSecondaryLaneSystem` reads it and suppresses markings. This is safe for any edge type.

However, if we ever expose a "reapply all" operation again (like v1's button), we **must** keep the
RB-skip guard. The existing `MarkingReapplySystem` already has it.

---

## 6. Recommendations

### Immediate (v2 phase 1–2)

1. **No new RB guard needed in `CustomSecondaryLaneSystem` itself.** The system processes edges
   individually via `Updated` events — this is the same normal path that was safe in v1.

2. **Keep the RB-skip in `MarkingToggleSystem`.** Already done. This prevents the v1-style mass-
   re-process crash on saves with problematic RB highway-based roads.

3. **Consider upgrading RB detection to `RoadBuilderPrefabData`** for any future code that needs
   to distinguish RB roads inside a Burst job. `RoadBuilderPrefabData` is an empty ECS component on
   the prefab entity — it can be looked up via a `ComponentLookup<RoadBuilderPrefabData>` in the
   job without touching managed code. The name heuristic works on the main thread but is not
   available inside Burst.

### Validation test

To confirm RB 3 m roads correctly inherit edge markings:

1. Open a save with at least one vanilla Small Road and one RB road built with 3 m car lanes.
2. Confirm edge line appears on both.
3. Open an RB road with 3 m lanes + "No Markings" option enabled (uses `"Alley Drive Section 3"`).
4. Confirm **no** edge line appears on that road.
5. Use the "Toggle all markings" button; confirm RB edges are skipped (check log: `skipped N
   Road-Builder edge(s)`), and vanilla edges hide/show correctly.

### Non-recommendation

Do **not** attempt to add edge markings to RB-only section prefabs (like `"Alley Drive Section 3"`
or the RB parking sections). Those are internal to RB and our mod should stay data-driven on vanilla
prefabs only.

---

## Summary

| Question | Answer |
|----------|--------|
| Does RB clone vanilla marking prefabs? | No. RB does not touch marking prefabs at all. |
| Does RB clone vanilla car-drive sections? | No — uses vanilla `NetSectionPrefab` objects directly. |
| Will RB 3 m roads inherit our edge line? | Yes (wanted). RB "No Markings" option uses alley section → excluded naturally. |
| Runtime marker for RB roads? | Prefab name `r…-…` with ≥4 dashes; or `RoadBuilderPrefabData` on prefab entity. |
| Race condition on load? | None — our patch runs before RB initializes its prefabs. |
| The v1 crash cause? | Mass-Updated on all edges including RB → vanilla SecondaryLaneSystem re-laid exotic RB geometry → crash. Not a per-edge / normal flow issue. |
| Action needed in v2? | None beyond existing RB-skip in MarkingToggleSystem. |
