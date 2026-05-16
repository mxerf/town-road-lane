# RESEARCH: Traffic Mod Patterns for Per-Node Marking UI

> Source root: `_refs/Traffic/Code/`
> All file:line citations are relative to that root.

---

## 1. ECS Storage for User Choices

### Buffer on the node entity: `ModifiedLaneConnections`

```csharp
// Components/LaneConnections/ModifiedLaneConnections.cs:10
[InternalBufferCapacity(0)]
public struct ModifiedLaneConnections : IBufferElementData, IEquatable<ModifiedLaneConnections>, ISerializable
{
    public int laneIndex;             // index into the edge's NetCompositionLane list
    public int2 carriagewayAndGroup;  // (carriageway, group) discriminator
    public float3 lanePosition;       // world-space position of the lane end
    public Entity edgeEntity;         // which edge this source lane belongs to
    public Entity modifiedConnections;// pointer to a separate entity holding GeneratedConnection buffer
}
```

Each entry in this buffer represents one **source lane end** on one **edge** of the node.
The user's chosen connection targets are stored NOT inline but on a satellite entity
(`modifiedConnections`) that holds a `DynamicBuffer<GeneratedConnection>`.

### Per-connection record: `GeneratedConnection`

```csharp
// Components/LaneConnections/GeneratedConnection.cs:14
[InternalBufferCapacity(0)]
public struct GeneratedConnection : IBufferElementData, ISerializable
{
    public Entity sourceEntity;             // source edge entity
    public Entity targetEntity;             // target edge entity
    public int2 laneIndexMap;               // (source lane index, target lane index)
    public int4 carriagewayAndGroupIndexMap;// (srcCarriageway, srcGroup, tgtCarriageway, tgtGroup)
    public float3x2 lanePositionMap;        // world positions of both ends
    public PathMethod method;               // Road | Track | Bicycle (bitmask)
    public bool isUnsafe;
}
```

### Entity hierarchy

```
node entity
  └─ DynamicBuffer<ModifiedLaneConnections>  (one entry per customized source lane end)
       └─ .modifiedConnections → satellite entity
              └─ DynamicBuffer<GeneratedConnection>  (one entry per user-defined connection)
```

The satellite entity also carries `DataOwner` and optionally `DataTemp` components that
Traffic uses to track ownership and temporary vs committed state.

The `EditIntersection` marker component (`Components/EditIntersection.cs`) is placed on a
**separate** definition entity (not the node itself) to signal that this node is currently
being edited by the tool:

```csharp
// Components/EditIntersection.cs:6
public struct EditIntersection : IComponentData
{
    public Entity node;  // the actual node entity being edited
}
```

---

## 2. Save / Load

Both `ModifiedLaneConnections` and `GeneratedConnection` implement
`Colossal.Serialization.Entities.ISerializable` explicitly. There is no custom serializer
framework — Unity Entities serialization calls `Serialize<TWriter>` / `Deserialize<TReader>`
on each buffer element automatically.

Both structs write a version integer first and handle migration in `Deserialize`:

```csharp
// ModifiedLaneConnections.cs:40-72
public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
{
    writer.Write(DataMigrationVersion.LaneConnectionDataUpgradeV1);
    writer.Write(laneIndex);
    writer.Write(carriagewayAndGroup);
    writer.Write(lanePosition);
    writer.Write(edgeEntity);
    writer.Write(modifiedConnections);
}

public void Deserialize<TReader>(TReader reader) where TReader : IReader
{
    reader.Read(out int v);
    if (v < DataMigrationVersion.LaneConnectionDataUpgradeV1)
    {
        // V0 compat path — carriagewayAndGroup / lanePosition were absent
        reader.Read(out laneIndex);
        reader.Read(out edgeEntity);
        reader.Read(out modifiedConnections);
        carriagewayAndGroup = TrafficDataMigrationSystem.InvalidCarriagewayAndGroup;
        lanePosition = float3.zero;
    }
    else { /* full read */ }
}
```

A separate `TrafficDataMigrationSystem` validates references after load and can clear
stale data (`Systems/Serialization/TrafficDataMigrationSystem.cs`). The satellite
`GeneratedConnection` entities must survive save alongside the node — Traffic achieves this
because those entities are ordinary ECS entities with serializable buffers.

---

## 3. Suppression Mechanism in Burst Job

This is the core intercept pattern. Traffic injects into the vanilla `LaneSystem` job
(which it replaces outright — the system file is `Traffic_LaneSystem.cs`, a full copy of
the vanilla file with `/*NON-STOCK*/` blocks added throughout).

### Step 1: LaneEndKey — the hash key

```csharp
// Systems/Traffic_LaneSystem.cs:132-152
private struct LaneEndKey : IEquatable<LaneEndKey>
{
    private int2 _data;

    public LaneEndKey(Entity owner, int idx) {
        _data = new int2(owner.Index, idx);  // edge entity index + lane index
    }

    public bool Equals(LaneEndKey other) => _data.Equals(other._data);
    public override int GetHashCode() => _data.GetHashCode();
}
```

Key design: uses `Entity.Index` (not `Entity` itself) so it is blittable inside a
`NativeHashSet<>` in a Burst job.

### Step 2: Build the suppression set

```csharp
// Systems/Traffic_LaneSystem.cs:1030
NativeHashSet<LaneEndKey> tempModifiedLaneEnds = new NativeHashSet<LaneEndKey>(4, Allocator.Temp);

// Systems/Traffic_LaneSystem.cs:1074-1079
if (modifiedLaneConnections.Length > 0)
{
    DynamicBuffer<ModifiedLaneConnections> connections = modifiedLaneConnections[entityIndex];
    FillModifiedLaneConnections(connections, tempModifiedLaneEnds, tempComponents.Length != 0);
    testKeys = true;
}
```

```csharp
// Systems/Traffic_LaneSystem.cs:6050-6061
private void FillModifiedLaneConnections(
    DynamicBuffer<ModifiedLaneConnections> connections,
    NativeHashSet<LaneEndKey> output,
    bool isTemp)
{
    for (var i = 0; i < connections.Length; i++)
    {
        ModifiedLaneConnections connection = connections[i];
        // skip entries scheduled for deletion in a Temp context
        if (!isTemp || dataTemps.TryGetComponent(connection.modifiedConnections, out temp)
                       && (temp.flags & TempFlags.Delete) == 0)
        {
            output.Add(new LaneEndKey(connection.edgeEntity, connection.laneIndex));
        }
    }
}
```

### Step 3: Skip vanilla emission via `continue`

Inside the main lane-creation loop (two call sites, one for car lanes and one for track lanes):

```csharp
// Systems/Traffic_LaneSystem.cs:6921-6926
LaneEndKey item = new LaneEndKey(sourcePosition3.m_Owner, sourcePosition3.m_LaneData.m_Index);
if (modifiedLaneEndConnections.Contains(item))
{
    continue;   // skip vanilla creation for this source lane end
}

// Systems/Traffic_LaneSystem.cs:7010-7015  (second site, track lanes)
LaneEndKey item = new LaneEndKey(sourcePosition4.m_Owner, sourcePosition4.m_LaneData.m_Index);
if (modifiedLaneEndConnections.Contains(item))
{
    continue;
}

// Systems/Traffic_LaneSystem.cs:7385-7388  (third site, in track lane inner loop)
if (modifiedLaneEndConnections.Contains(new LaneEndKey(sourcePosition.m_Owner, sourcePosition.m_LaneData.m_Index)))
{
    continue;
}
```

The set is reset via `tempModifiedLaneEnds.Clear()` between node entities
(line 1510) and disposed at the end of the chunk (line 1554).

---

## 4. Explicit Creation of User Choices

After skipping vanilla, Traffic emits the user's chosen connections in a separate
inner loop that runs in the same job, immediately after `ProcessCarConnectPositions` returns:

```csharp
// Systems/Traffic_LaneSystem.cs:1258-1325
if (modifiedLaneConnections.Length > 0)
{
    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedLaneConnections[entityIndex];
    for (var i = 0; i < modifiedConnections.Length; i++)
    {
        ModifiedLaneConnections connectionsEntity = modifiedConnections[i];
        // guard: edge matches current sourceMainCarConnectPos.m_Owner
        if (connectionsEntity.edgeEntity != sourceMainCarConnectPos.m_Owner || ...) continue;

        DynamicBuffer<GeneratedConnection> connections =
            generatedConnections[connectionsEntity.modifiedConnections];

        // find source ConnectPosition in the pre-sorted list
        ConnectPosition cs = FindNodeConnectPosition(tempSourceConnectPositions,
            connectionsEntity.edgeEntity, connectionsEntity.laneIndex, TrackTypes.None, ...);

        for (var j = 0; j < connections.Length; j++)
        {
            GeneratedConnection connection = connections[j];
            ConnectPosition ct = FindNodeConnectPosition(tempTargetConnectPositions,
                connection.targetEntity, connection.laneIndexMap.y, TrackTypes.None, ...);

            if (ct.m_Owner != Entity.Null && !createdConnections.Contains(key))
            {
                // emit the actual node lane using the same CreateNodeLane helper vanilla uses
                CreateNodeLane(chunkIndex, ref idx, ref random4, ref curviness, ref isSkipped,
                    entity3, laneBuffer, middleConnections, cs, ct, intersectionFlags2,
                    group, 0, connection.isUnsafe, false, tempComponents.Length != 0,
                    trackOnly, yield2, ownerTemp3, isTurn, right, gentle, uturn, ...);

                createdConnections.Add(key);
            }
        }
    }
}
```

Key points:
- Same job, same chunk iteration — no separate pass.
- Calls the unmodified `CreateNodeLane` helper that vanilla uses, so resulting lane
  entities are identical in structure.
- `createdConnections` (a `NativeParallelHashSet<ConnectionKey>`) prevents duplicates.

---

## 5. UI Tool Architecture

### ToolBaseSystem extension

```csharp
// Tools/LaneConnectorToolSystem.cs:35
public partial class LaneConnectorToolSystem : ToolBaseSystem
{
    public override string toolID => UIBindingConstants.LANE_CONNECTOR_TOOL;
    // ...
}
```

Traffic does NOT Harmony-patch the ToolSystem. It extends `ToolBaseSystem` (a vanilla
abstract class) and sets `m_ToolSystem.activeTool = this` to activate.

### State machine

Three meaningful states drive all behaviour:

```csharp
// Tools/LaneConnectorToolSystem.cs:50-68
public enum State {
    Default,                  // waiting for node click
    SelectingSourceConnector, // node selected, waiting for source dot click
    SelectingTargetConnector, // source selected, dragging to target
    ApplyingQuickModifications,
}
```

Transitions:
- `Default` → `SelectingSourceConnector`: user clicks a node (via vanilla `ToolRaycastSystem`)
- `SelectingSourceConnector` → `SelectingTargetConnector`: user clicks a source dot
  (via custom `ModRaycastSystem`)
- `SelectingTargetConnector` → `SelectingTargetConnector` (stay): user clicks a target dot →
  applies connection and resets to source selection

### Node detection (step 1)

Uses the vanilla `m_ToolRaycastSystem` with `TypeMask.Net` and checks that the hit entity
has `Node` + `ConnectedEdge` + `SubLane` components and at least 2 road/track edges:

```csharp
// Tools/LaneConnectorToolSystem.cs:496-546
protected override bool GetRaycastResult(out ControlPoint controlPoint) {
    if (base.GetRaycastResult(out controlPoint)) {
        return IsCompatibleRaycastResult(controlPoint.m_OriginalEntity);
    }
    return false;
}
```

### Connector dot detection (steps 2-3)

Uses a custom `ModRaycastSystem` which does **not** use the vanilla raycast. Instead:
1. A `SearchSystem` maintains a persistent `NativeQuadTree<Entity, QuadTreeBoundsXZ>`
   of all live `Connector` entities (updated on `Updated`/`Deleted` tags).
2. `ModRaycastSystem.PerformRaycast()` calls `FindConnectionNodeFromTreeJob` against
   the quad tree, then `RaycastLaneConnectionSubObjects` to get the closest hit.
3. Result is stored in a `NativeReference<CustomRaycastResult>` and retrieved each frame
   via `GetCustomRayCastResult`.

```csharp
// Systems/ModRaycastSystem.cs:86-95
RaycastJobs.FindConnectionNodeFromTreeJob job = new RaycastJobs.FindConnectionNodeFromTreeJob()
{
    input = input,
    entityList = entities,
    searchTree = _searchSystem.GetSearchTree(true, out JobHandle dependencies)
};
```

### Connector entity archetype

Each visible dot is an ECS entity with:
- `Connector` (IComponentData) — position, direction, edge, node, laneIndex, vehicleGroup, connectorType
- `LaneConnection` (IBufferElementData) — list of active connection entities
- `Updated` tag

```csharp
// Components/LaneConnections/Connector.cs:8
public struct Connector : IComponentData
{
    public Entity edge;
    public Entity node;
    public int laneIndex;
    public int2 carriagewayAndGroupIndex;
    public float3 position;        // world-space dot position
    public float3 lanePosition;    // position in composition space
    public float3 direction;       // tangent for arrow gizmo
    public VehicleGroup vehicleGroup;
    public ConnectorType connectorType; // Source | Target | TwoWay
}
```

The node's definition entity holds a `DynamicBuffer<ConnectorElement>` pointing to each dot:

```csharp
// Components/LaneConnections/ConnectorElement.cs:6
[InternalBufferCapacity(0)]
public struct ConnectorElement : IBufferElementData { public Entity entity; }
```

### Overlay rendering

`ToolOverlaySystem` (`Rendering/ToolOverlaySystem.cs`) is a `GameSystemBase` that
activates only when `_toolSystem.activeTool == _laneConnectorToolSystem`. It schedules
three Burst jobs into `OverlayRenderSystem.GetBuffer()`:

| Job | What it draws |
|-----|---------------|
| `HighlightIntersectionJob` | road edge outline for the selected node |
| `ConnectorsOverlayJob` | coloured circles at each dot position |
| `ConnectionsOverlayJob` | lines between connected dots |

All drawing uses `OverlayRenderSystem` (vanilla). No custom renderer.

### Click / drag mechanic

A `NativeList<ControlPoint> _controlPoints` holds 0, 1, or 2 points:
- 0 points → hovering over node (Default)
- 1 point → source dot selected
- 2 points → source + current target hover

`UpdateDefinitions()` runs `CreateDefinitionsJob` every frame when in
`SelectingTargetConnector` state, which creates temporary `ConnectionDefinition` entities
that drive `GenerateLaneConnectionsSystem` to produce preview lane geometry. On confirmed
click, `applyMode = ApplyMode.Apply` causes vanilla's Temp system to commit the changes.

---

## 6. Adjacency / Geometry Helpers

### GenerateConnectorsJob — building the dot list

The job (`Systems/LaneConnections/GenerateConnectorsSystem.GenerateConnectorsJob.cs`)
iterates every edge connected to the selected node and calls `GetNodeConnectors()` per edge.

Key algorithm (lines 97-280):

1. Walk each `SubLane` on the edge, skip secondary lanes and utility/parking lanes.
2. For the remaining sublane, compute `EdgeDelta` to know which end of the edge this is.
3. Find the **closest** `NetCompositionLane` entry by projecting the sublane's curve start
   onto the composition lane's normalised lateral position and minimising distance:

```csharp
// GenerateConnectorsSystem.GenerateConnectorsJob.cs:199-226
for (int k = 0; k < netCompositionLanes.Length; k++)
{
    NetCompositionLane netCompositionLane = netCompositionLanes[k];
    // flags check skipped for brevity...
    float num3 = netCompositionLane.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
    if (MathUtils.Intersect(
            new Line2(edgeGeometry.m_Start.m_Right.a.xz, edgeGeometry.m_Start.m_Left.a.xz),
            new Line2(curve.m_Bezier.a.xz, curve.m_Bezier.b.xz), out float2 t))
    {
        float num4 = math.abs(num3 - t.x);
        if (num4 < num2) { compositionLaneIndex = k; num2 = num4; }
    }
}
```

4. A `bool* visitedCompositionLanes` (stackalloc) prevents two sublanes from mapping to
   the same composition slot, so each dot appears exactly once even for two-way lanes.

5. The resulting `ConnectPosition` records `order` (lateral fraction 0→1) that is used to
   sort dots left-to-right in the UI.

6. Two-way lanes (e.g. bus lanes marked `LaneFlags.Twoway`) are added to **both** source
   and target lists.

### GenerateConnectionLanesJob — building the connection line list

(`GenerateConnectorsSystem.GenerateConnectionLanesJob.cs`)

Walks the node's actual sublanes (not the edge's composition), retrieves `sourceEdge`
and `targetEdge` by matching `Lane.m_StartNode` / `Lane.m_EndNode` to `ConnectedEdge`,
and emits `Connection` buffer entries keyed to source connectors via
`NodeEdgeLaneKey(node.Index, edge.Index, sourceConnectorIndex)`.

---

## 7. What to Take vs Leave for Our Mod

### Take directly

**`LaneEndKey` hashset suppression pattern** (Sections 3–4)

Traffic's exact pattern (int2 of edge entity index + lane index, NativeHashSet, `continue`
in the inner loop) is applicable verbatim for marking suppression. Our mod already replaces
`SecondaryLaneSystem`, so we can inject the same suppression set at the top of our job and
skip vanilla marking emission for any lane end the user has customised.

**`IBufferElementData` + `ISerializable` storage** (Section 2)

Storing user choices as a buffer on the node entity with embedded `Serialize`/`Deserialize`
is the correct CS2 pattern. We get free save/load from Unity Entities serialization. Use
the same versioned integer header for future migration headroom.

**`ToolBaseSystem` extension** (Section 5)

Extending `ToolBaseSystem` is the vanilla extension point. No Harmony needed for tool
registration; just set `m_ToolSystem.activeTool = this` and override `OnUpdate`.

**`OverlayRenderSystem.GetBuffer()`** (Section 5)

Vanilla overlay rendering is the correct channel for custom gizmos. Traffic's
`ConnectorsOverlayJob` and `ConnectionsOverlayJob` show the full pattern: get the buffer
handle, schedule a Burst job, call `AddBufferWriter`.

### Adapt

**`ModifiedLaneConnections` / `GeneratedConnection` structs** (Section 1)

Fields like `PathMethod method` and `isUnsafe` are vehicle-routing concepts. For marking
we need different fields: e.g. `MarkingFlags suppressionMask`, `Entity markingPrefabOverride`.
Keep the structural pattern (buffer on node, satellite entity per source lane, one record
per target connection) but swap the payload.

**`GenerateConnectorsJob` adjacency detection** (Section 6)

The composition-lane proximity sort is reusable as-is, but we only care about marking
endpoints (where secondary lanes originate), not all road/track sublanes. Filter to
sublanes that have `SecondaryLane` or are the host lane for a marking start. The
`visitedCompositionLanes` deduplication technique is directly usable.

**`ModRaycastSystem` + `SearchSystem`** (Section 5)

The NativeQuadTree approach for clicking connector dots is solid. We can lift the
pattern, but our `Connector`-equivalent will store `MarkingEndpoint` instead, and the
spatial radius may differ (marking endpoints are at lane edges, not lane centrelines).

**`EditIntersection` + two-entity pattern** (Sections 1, 5)

Traffic creates a temporary definition entity with `EditIntersection` (pointing to the
node) and `EditLaneConnections` to drive its generation systems. We need a similar
approach (`EditMarkingNode` + `EditMarkingConnections`) so our overlay/generation systems
can query for "currently active node" without touching the node entity directly.

### Skip

**Vehicle routing logic** (Sections 3–4, all of `PathMethod`, `isUnsafe`, `VehicleGroup`)

All vehicle-specific filtering (road vs track vs bike, unsafe connections, highway rules)
has no meaning for markings. Skip entirely.

**Priority tool system** (`PriorityToolSystem`, `LanePriority`, `LaneHandle`)

Orthogonal feature, irrelevant.

**`SyncCustomLaneConnectionsSystem`** (Section 2 support)

Traffic needs a sync system to remap connection data when road geometry changes (split,
merge). For Phase 3 (marking UI) we can defer this; our marking data is indexed by edge
entity + lane index and we already handle re-baking in `MarkingToggleSystem`. A full sync
system is future work.

**`TrafficDataMigrationSystem` V0→V1 migration**

Starting from scratch means we start at V1. Build the migration infrastructure only when
we actually introduce a breaking schema change.

### Caveats

**Traffic replaces the vanilla LaneSystem entirely (`Traffic_LaneSystem.cs`)**

This is a Harmony-free approach only because they ship a full copy of the 9 000-line file
with `/*NON-STOCK*/` markers. Our mod already replaces `SecondaryLaneSystem` using
`Modification4B` registration; the suppression injection must go into our replacement
system. If Colossal updates vanilla we must re-merge — exactly the same burden Traffic
carries for their file.

**Two-system ordering constraint**

Traffic's `ApplyLaneConnectionsSystem` runs at `ToolOutputBarrier` (which fires before the
next simulation step). Our equivalent must commit changes before our replacement
`CustomSecondaryLaneSystem` runs. Verify barrier ordering in `Modification.cs`.

**`Entity.Index` vs `Entity` equality in NativeHashSet**

Using `Entity.Index` (discarding the version number) inside `LaneEndKey` is intentional —
it avoids the version bump that happens when a temp entity is promoted to permanent.
Double-check this holds for our use case; if marking entities are never promoted, we may
be able to use full `Entity` equality instead and skip the custom hash struct.
