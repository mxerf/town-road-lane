# RESEARCH: CS2 UI Framework for Per-Node Marking Tool

> Researched from decomp sources in `decomp/Game/` and Traffic mod reference in `_refs/Traffic/Code/`.  
> File references use paths relative to the repo root.

---

## 1. ToolSystem architecture

### Base class

```csharp
// decomp/Game/Game.Tools/ToolBaseSystem.cs:28
public abstract class ToolBaseSystem : GameSystemBase, IEquatable<ToolBaseSystem>
```

`ToolBaseSystem` extends `GameSystemBase` (Unity DOTS managed system). All tools inherit from it.

### Required overrides

```csharp
public abstract string toolID { get; }           // unique string key, shown in ToolSystem
public abstract PrefabBase GetPrefab();          // return null if tool owns no prefab
public abstract bool TrySetPrefab(PrefabBase prefab); // return false unless tool handles prefab picking
```

### Key virtual methods to override

```csharp
// Called every frame the tool is active; do all state/raycast logic here.
// Return the combined JobHandle from any jobs you schedule.
protected virtual JobHandle OnUpdate(JobHandle inputDeps) { return inputDeps; }

// Called BEFORE ToolRaycastSystem.OnUpdate so you can configure raycast params.
public virtual void InitializeRaycast() { /* set m_ToolRaycastSystem.typeMask etc. */ }

// Called on Enabled=true (tool activated)
protected override void OnStartRunning() { ... }

// Called on Enabled=false (tool deactivated)
protected override void OnStopRunning() { ... }
```

### Lifecycle and registration

In `OnCreate`, base class automatically calls:
```csharp
// ToolBaseSystem.cs:315
m_ToolSystem.tools.Add(this);   // self-registers with ToolSystem
base.Enabled = false;           // starts disabled
```

`ToolSystem.activeTool` is the single active tool. Setting it causes:
```csharp
// ToolSystem.cs:91-98
set {
    if (value != m_ActiveTool) {
        m_ActiveTool = value;
        RequireFullUpdate();
        EventToolChanged?.Invoke(value);
    }
}
```

Inside `ToolSystem.OnUpdate → ToolUpdate()` the active tool's `Enabled` is set to `true`; the previously active tool gets `Enabled = false`. The update phase is `SystemUpdatePhase.ToolUpdate`.

### Activating/deactivating our tool

```csharp
// To activate:
m_ToolSystem.activeTool = this;

// To deactivate:
m_ToolSystem.activeTool = m_DefaultToolSystem;
```

Traffic's `LaneConnectorToolSystem` provides a `ToggleTool(bool enable)` wrapper:
```csharp
// _refs/Traffic/Code/Tools/LaneConnectorToolSystem.cs:248
public void ToggleTool(bool enable) {
    if (enable && m_ToolSystem.activeTool != this) {
        m_ToolSystem.selected = Entity.Null;
        m_ToolSystem.activeTool = this;
    } else if (!enable && m_ToolSystem.activeTool == this) {
        m_ToolSystem.selected = Entity.Null;
        m_ToolSystem.activeTool = m_DefaultToolSystem;
    }
}
```

### Input action setup (built-in)

`ToolBaseSystem.OnCreate` fetches standard input actions from `InputManager`:
```csharp
// ToolBaseSystem.cs:301-305
m_DefaultApply   = InputManager.instance.toolActionCollection.GetActionState("Apply", name);
m_DefaultSecondaryApply = InputManager.instance.toolActionCollection.GetActionState("Secondary Apply", name);
m_DefaultCancel  = InputManager.instance.toolActionCollection.GetActionState("Cancel", name);
m_MouseApply     = InputManager.instance.toolActionCollection.GetActionState("Mouse Apply", name);
m_MouseCancel    = InputManager.instance.toolActionCollection.GetActionState("Mouse Cancel", name);
```

Protected accessors are `applyAction`, `secondaryApplyAction`, `cancelAction`. In `OnUpdate` call:
```csharp
if (applyAction.WasPressedThisFrame())   { ... }
if (cancelAction.WasPressedThisFrame())  { ... }
```

---

## 2. Raycast for node selection

### System overview

```
ToolRaycastSystem.OnUpdate()
  → calls m_ToolSystem.activeTool.InitializeRaycast()   // you configure params here
  → builds RaycastInput
  → calls m_RaycastSystem.AddInput(this, input)
```

`ToolRaycastSystem` wraps the lower-level `RaycastSystem`. The result is queried in the tool's `OnUpdate` via helpers from `ToolBaseSystem`:

```csharp
// ToolBaseSystem.cs:542-553
protected bool GetRaycastResult(out Entity entity, out RaycastHit hit) {
    if (m_ToolRaycastSystem.GetRaycastResult(out var result)
        && !EntityManager.HasComponent<Deleted>(result.m_Owner)) {
        entity = result.m_Owner;
        hit    = result.m_Hit;
        return true;
    }
    entity = Entity.Null;
    hit    = default;
    return false;
}
```

Higher-level variant (also sets `forceUpdate` if the original entity was deleted):
```csharp
protected bool GetRaycastResult(out Entity entity, out RaycastHit hit, out bool forceUpdate)
protected virtual bool GetRaycastResult(out ControlPoint controlPoint)
```

### `RaycastHit` fields of interest
```csharp
result.m_Hit.m_HitPosition   // float3 world position of hit
result.m_Owner               // Entity that was hit
result.m_Hit.m_CellIndex     // grid cell index (for terrain/zone tools)
```

### Filtering to nodes only

In `InitializeRaycast()`:
```csharp
// For the initial node-click phase (state == Default / hovering):
m_ToolRaycastSystem.typeMask    = TypeMask.Net;
m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.TrainTrack | /* etc. */;
m_ToolRaycastSystem.raycastFlags = RaycastFlags.SubElements
                                 | RaycastFlags.Cargo
                                 | RaycastFlags.Passenger
                                 | RaycastFlags.EditorContainers;
m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
```

Then in `OnUpdate`, verify the hit entity is a node:
```csharp
if (EntityManager.HasComponent<Game.Net.Node>(hitEntity)) { ... }
```

Traffic also checks `ConnectedEdge` buffer length ≥ 2 to exclude dead-ends:
```csharp
// _refs/Traffic/Code/Tools/LaneConnectorToolSystem.cs:520-528
if (!EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<ConnectedEdge> edges)
    || edges.Length < 2) return false;
```

### Custom raycast for connector dots

When hitting small world-space dots (not actual collider entities), vanilla `ToolRaycastSystem` cannot help. Traffic builds a custom `ModRaycastSystem` with a spatial search tree:

```csharp
// _refs/Traffic/Code/Systems/ModRaycastSystem.cs:149
public void SetInput(CustomRaycastInput input) { _input.Value = input; }
public bool GetRaycastResult(out CustomRaycastResult result) { ... }
```

The search tree is populated by `SearchSystem` when connector entities exist. The hit test is a sphere-radius check against the connector's `position` field.

**For our tool:** a simpler approach is to represent marking endpoints as `float3` positions stored in a `NativeList` and do a distance-to-screen-projection test each frame ourselves — or adopt Traffic's spatial-tree pattern.

---

## 3. Overlay rendering

### Getting a buffer

```csharp
// decomp/Game/Game.Rendering/OverlayRenderSystem.cs:592
public Buffer GetBuffer(out JobHandle dependencies)
```

Call from a rendering system's `OnUpdate`:
```csharp
OverlayRenderSystem.Buffer overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayDeps);
JobHandle jobHandle = new MyOverlayJob {
    overlayBuffer = overlayBuffer,
    ...
}.Schedule(JobHandle.CombineDependencies(Dependency, overlayDeps));
_overlayRenderSystem.AddBufferWriter(jobHandle);
Dependency = jobHandle;
```

The buffer is written each frame; there is no "clear"; it is rebuilt from scratch.

### Drawing primitives (Buffer inner struct methods)

```csharp
// Circles (connector dots)
void DrawCircle(Color color, float3 position, float diameter)
void DrawCircle(Color outlineColor, Color fillColor, float outlineWidth,
                StyleFlags styleFlags, float2 direction,
                float3 position, float diameter)

// Straight lines
void DrawLine(Color color, Line3.Segment line, float width, bool cameraFacing = false)
void DrawLine(Color outlineColor, Color fillColor, float outlineWidth,
              StyleFlags styleFlags, Line3.Segment line, float width, float2 roundness)
void DrawDashedLine(Color color, Line3.Segment line, float width,
                    float dashLength, float gapLength)

// Bezier curves
void DrawCurve(Color color, Bezier4x3 curve, float width)
void DrawCurve(Color outlineColor, Color fillColor, float outlineWidth,
               StyleFlags styleFlags, Bezier4x3 curve, float width, float2 roundness)
void DrawDashedCurve(Color color, Bezier4x3 curve, float width, float dashLength, float gapLength)

// Custom meshes (Arrow, Cylinder, Plane)
void DrawCustomMesh(Color fillColor, float3 position, float height, float width,
                    CustomMeshType meshType, Quaternion rot = identity)
```

### StyleFlags

```csharp
[Flags] public enum StyleFlags {
    Grid           = 1,
    Projected      = 2,   // projects onto terrain surface (follows terrain height)
    DepthFadeBelow = 4,   // fades when occluded by geometry from below
}
```

`Projected` curves are terrain-projected (good for on-road overlays); `Absolute` curves float at the literal Y position.

### Color conventions (from Traffic reference)

Traffic uses `color.linear` when filling `CurveData`; the Buffer methods do this automatically. Pass `UnityEngine.Color` directly.

| Usage | Color |
|-------|-------|
| Source connector (car) | `new Color(0f, 0.83f, 1f, 1f)` cyan |
| Target connector | `new Color(0f, 0.56f, 0.87f, 1f)` blue |
| Active/hovered | `new Color(1f, 1f, 1f, 0.92f)` white |
| Connection line | same cyan/blue, width ~0.4 m |

### When to call

Overlay rendering must happen in a separate `GameSystemBase` (not the tool itself). Run it after `SystemUpdatePhase.ToolUpdate`:

```csharp
// Typical mod overlay system: register in a phase after ToolUpdate
// (Traffic uses the default phase, gated by activeTool check)
protected override void OnUpdate() {
    if (_toolSystem.activeTool != _ourTool) return;
    OverlayRenderSystem.Buffer buf = _overlayRenderSystem.GetBuffer(out JobHandle deps);
    JobHandle h = new DrawMarkingEndpointsJob { overlayBuffer = buf, ... }
                      .Schedule(JobHandle.CombineDependencies(Dependency, deps));
    _overlayRenderSystem.AddBufferWriter(h);
    Dependency = h;
}
```

### Connector dot example from Traffic

```csharp
// _refs/Traffic/Code/Rendering/ToolOverlaySystem.ConnectorsOverlayJob.cs:98-105
overlayBuffer.DrawCircle(
    isSource ? colorSet.outlineActiveColor : outlineColor,
    fillColor,
    outline,            // outlineWidth (meters)
    0,                  // no StyleFlags
    new float2(0f, 1f), // direction (up)
    position,           // float3 world position
    diameter);          // diameter in meters (~1 m)
```

---

## 4. UI panel / button binding (C# ↔ JS)

### UISystemBase

Mods inherit from `Game.UI.UISystemBase` (not `ToolBaseSystem`) for UI logic:

```csharp
// Traffic reference: _refs/Traffic/Code/UISystems/ModUISystem.cs:21
public partial class ModUISystem : UISystemBase, IPreDeserialize
```

### ValueBinding patterns

```csharp
using Colossal.UI.Binding;

// Push a value to JS (polled every frame):
AddUpdateBinding(new GetterValueBinding<int>(
    "modName", "bindingKey",
    () => someIntValue));

// Complex custom type:
AddUpdateBinding(new GetterValueBinding<SelectedIntersectionData>(
    "modName", "selectedIntersection",
    () => SelectedIntersection));   // type needs IWriter<T>

// Trigger from JS → C#:
AddBinding(new TriggerBinding<Entity>(
    "modName", "navigateToEntity",
    (entity) => { /* handle */ }));

// Trigger with enum:
AddBinding(new TriggerBinding<ModTool>(
    "modName", "toggleTool",
    ToggleTool,
    new EnumReader<ModTool>()));    // custom reader for enum
```

JS side calls `engine.trigger("modName", "toggleTool", 1)` and reads `engine.on("modName", "selectedIntersection", cb)`.

### For our tool (v1 scope)

A minimal UI surface is likely:
- A toolbar button (toggle the tool on/off) — wired via `TriggerBinding` or `ValueBinding<bool>`.
- No panel required for the first iteration; all interaction is click-in-world.

❓ **Unclear**: The exact HTML/JS module path for adding a button to the road tool toolbar vs. a standalone floating panel. Needs experiment or inspection of existing mod UI entry points.

---

## 5. Input handling

### Actions already in base class

`ToolBaseSystem` wires five standard actions automatically (see §1). In `OnUpdate`:

```csharp
applyAction.WasPressedThisFrame()    // left-click / Enter
applyAction.WasReleasedThisFrame()
applyAction.IsInProgress()           // held down

cancelAction.WasPressedThisFrame()   // Escape / right-click depending on mapping
secondaryApplyAction.WasPressedThisFrame() // secondary action
```

`shouldBeEnabled` on each action must be set to `true` inside `UpdateActions()` (called from base class when tool starts/stops). Example from `DefaultToolSystem.cs:736`:
```csharp
private protected override void UpdateActions() {
    using (ProxyAction.DeferStateUpdating()) {
        base.applyActionOverride = (m_LastRaycastEntity != Entity.Null)
            ? m_DefaultToolApply : m_MouseApply;
        base.applyAction.shouldBeEnabled = base.actionsEnabled;
        base.cancelAction.shouldBeEnabled = base.actionsEnabled;
    }
}
```

### Custom keybinds (Traffic pattern)

```csharp
// Declare in mod settings:
_applyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ApplyTool);
// Check in OnUpdate:
if (_applyAction.WasPressedThisFrame()) { ... }
// Enable/disable:
_applyAction.shouldBeEnabled = toolboxActive;
```

### Modifier keys (keyboard polling)

Traffic reads keyboard state directly for instant modifiers:
```csharp
// _refs/Traffic/Code/Tools/LaneConnectorToolSystem.cs:415-427
using UnityEngine.InputSystem;
if (Keyboard.current.ctrlKey.isPressed)  { ... }
if (Keyboard.current.shiftKey.isPressed) { ... }
if (Keyboard.current.altKey.isPressed)   { ... }
```

This is called inside `InitializeRaycast()` each frame.

### Drag mechanic

No dedicated drag API. The pattern (from `DefaultToolSystem`):
1. On `applyAction.WasPressedThisFrame()` → record start position, transition to `MouseDown` state.
2. Each frame in `MouseDown` state → read current raycast position, compute delta vs start.
3. If distance > threshold → transition to `Dragging`.
4. On `applyAction.WasReleasedThisFrame()` in `Dragging` → confirm.
5. On `cancelAction` → abort.

---

## 6. Reference: how Traffic mod did it

### Tool class

**File:** `_refs/Traffic/Code/Tools/LaneConnectorToolSystem.cs`  
**Class:** `Traffic.Tools.LaneConnectorToolSystem : ToolBaseSystem`  
**Tool ID:** `"LaneConnectorTool"` (from `UIBindingConstants.LANE_CONNECTOR_TOOL`)

### State machine (4 states)

```csharp
public enum State {
    Default,                   // waiting for intersection click
    SelectingSourceConnector,  // node selected, waiting for source dot click
    SelectingTargetConnector,  // source selected, waiting for target dot click
    ApplyingQuickModifications // bulk operation in flight
}
```

### Connector dot entity archetype

Connector dots are **full ECS entities** (not just immediate-mode overlay):

```csharp
// _refs/Traffic/Code/Components/LaneConnections/Connector.cs
public struct Connector : IComponentData {
    public Entity edge;
    public Entity node;
    public int laneIndex;
    public int2 carriagewayAndGroupIndex;
    public float3 position;      // world-space center of dot
    public float3 lanePosition;
    public float3 direction;
    public VehicleGroup vehicleGroup;
    public ConnectorType connectorType; // Source | Target
}
```

Connector entities are created by `GenerateConnectorsSystem` when `EditIntersection` + `EditLaneConnections` + `Updated` components appear on a node entity. They are destroyed on `CleanupIntersectionHelpers()` by adding `Deleted` component.

### Spatial search tree for hit-testing

Traffic builds a custom `NativeKdTree` (or similar) via `SearchSystem` populated from connector positions. `ModRaycastSystem` runs a sphere test against it each frame.

### Overlay rendering calls

**File:** `_refs/Traffic/Code/Rendering/ToolOverlaySystem.cs`  
**System:** `Traffic.Rendering.ToolOverlaySystem : GameSystemBase`

Overlay is a **separate system** (not inside the tool class). It holds a reference to `_laneConnectorToolSystem` and checks `_toolSystem.activeTool`. Pattern:

```csharp
// Get buffer
overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayDeps);
// Schedule job
JobHandle h = new ConnectorsOverlayJob { overlayBuffer = overlayBuffer, ... }
                  .Schedule(JobHandle.CombineDependencies(deps, overlayDeps));
_overlayRenderSystem.AddBufferWriter(h);
```

The `ConnectorsOverlayJob` iterates all `Connector` entities and calls `overlayBuffer.DrawCircle(...)` for each. The `ConnectionsOverlayJob` draws lines between connected connectors.

### Click → connection-creation flow

1. User clicks intersection node → `Apply()` in `State.Default`
   - Validates entity is `Node` with ≥ 2 connected edges
   - Sets `EditIntersection` + `Updated` on node → triggers `GenerateConnectorsSystem`
   - Transitions to `State.SelectingSourceConnector`
2. `GenerateConnectorsSystem.OnUpdate` creates `Connector` entities for each lane endpoint
3. User moves mouse → `ModRaycastSystem` hits a connector entity → `GetCustomRaycastResult`
4. User clicks source connector → `Apply()` in `SelectingSourceConnector`
   - Adds controlPoint[0] = source connector
   - Transitions to `State.SelectingTargetConnector`
5. User clicks target connector → `Apply()` in `SelectingTargetConnector`
   - Sets `applyMode = ApplyMode.Apply`
   - Schedules `CreateDefinitionsJob` → creates `CreationDefinition` + `TempLaneConnection` entities
   - `ApplyLaneConnectionsSystem` picks up temp entities and writes `ModifiedLaneConnections`
6. Cancel at any state → `Cancel()` → `CleanupIntersectionHelpers()` → destroys all `Connector` entities

---

## 7. Recommended approach for our tool

### Class structure

```
MarkingNodeToolSystem : ToolBaseSystem
    — toolID = "MarkingNodeTool"
    — State { Default, NodeSelected, EndpointHovered, DraggingLine }
    — holds: Entity _selectedNode, NativeList<MarkingEndpoint> _endpoints
    — OnStartRunning / OnStopRunning: clean up helper entities
    — InitializeRaycast: typeMask = Net, netLayerMask = Road | …
    — OnUpdate: state machine (click node → show endpoints → drag to connect)

MarkingEndpointSystem : GameSystemBase
    — Generates MarkingEndpoint entities when EditMarkingNode appears on node
    — Each endpoint: position (float3), edgeIndex, side (left/right), existing connection

MarkingOverlaySystem : GameSystemBase
    — Runs after ToolUpdate
    — Calls _overlayRenderSystem.GetBuffer()
    — Draws endpoint circles, active-drag line, confirmed connections
```

### Connector/endpoint representation

**Option A — Immediate-mode only** (simpler):
- Store endpoint positions in a `NativeList<float3>` on the tool system.
- No ECS entities for dots; draw in `MarkingOverlaySystem` by iterating the list.
- Hit-test by screen-space distance each frame (project each endpoint through camera, compare to mouse position).
- Pros: No entity lifecycle complexity. Cons: No spatial search tree → O(n) per frame, fine for ≤ ~20 endpoints per node.

**Option B — Entities (like Traffic):**
- Create a `MarkingEndpoint` ECS entity per lane endpoint when a node is selected.
- Build a simple `NativeList<(Entity, float3)>` search structure (no need for a full kd-tree for 5–20 dots).
- Hit-test with sphere-radius check (`math.distance(screenProjected, mouse) < radius`).
- Pros: Correct DOTS patterns, extendable. Cons: More entity lifecycle boilerplate.

**Recommendation:** Start with Option A (immediate-mode endpoints). Upgrade to Option B if the endpoint count or performance demands it.

### State machine

```
Default
  │ click on node (TypeMask.Net, filter HasComponent<Node>)
  ▼
NodeSelected           ← generates endpoint list, draws dots
  │ hover over dot (screen-dist check)
  ▼
EndpointHovered        ← highlighted dot, tooltip
  │ click on dot
  ▼
DraggingLine           ← source dot fixed, line follows cursor to terrain hit
  │ click on target dot
  ├─► (valid) → write MarkingOverride entry, stay in NodeSelected
  └─► (invalid / cancel) → stay in NodeSelected, clear drag
  │ Escape / click elsewhere
  ▼
Default                ← clean up helpers
```

### Overlay rendering plan

```csharp
// In MarkingOverlaySystem.OnUpdate:
var buf = overlayRenderSystem.GetBuffer(out JobHandle deps);

// Endpoint dots (~1 m diameter):
foreach (var ep in endpoints)
    buf.DrawCircle(hovered == ep ? white : cyan, ep.position, 1.0f);

// Drag line (if dragging):
buf.DrawLine(white, new Line3.Segment(sourcePos, cursorTerrainPos), 0.2f);

// Confirmed connections:
foreach (var conn in connections)
    buf.DrawCurve(new Color(0.3f, 1f, 0.3f), conn.curve, 0.15f);
```

### Estimated complexity

| Sub-task | Complexity | Biggest unknown |
|---|---|---|
| Tool system scaffold | **Low** | — |
| Node raycast | **Low** | — |
| Endpoint position extraction from our marking data | **Medium** | How to map `SecondaryLane` data to world-space endpoints |
| Immediate-mode overlay drawing | **Low** | — |
| Screen-space dot hit-test | **Low** | — |
| Drag → new override entry | **Medium** | Our `MarkingOverride` data model may need a "connection list" field |
| UI button (toggle tool) | **Medium** | ❓ JS/HTML entry point for toolbar button unknown |
| Persisting per-node connections across saves | **High** | ❓ Serialization hooks for new connection component |

### Biggest unknowns to experiment

1. **❓ Endpoint world positions**: We need to map each `SecondaryLane` marking segment to a start/end `float3`. The geometry is inside `EdgeGeometry` / `StartNodeGeometry` / `EndNodeGeometry` buffers. Needs experiment to read the exact positions.

2. **❓ Toolbar button integration**: How to add a button to the in-game road-tool panel or create a standalone panel. Traffic uses a full `UISystemBase` + React frontend. We may be able to reuse our existing `MarkingToggleSystem` binding pattern or add a new button binding.

3. **❓ Saving connection data**: Our `MarkingOverride` component currently stores a bitmask. A per-node "which endpoints are connected" structure will need a `DynamicBuffer` and a serialization hook. Needs design before implementation.

4. **❓ `applyAction` vs mouse click disambiguation**: When our tool is active, left-click on UI panels must not trigger endpoint clicks. The `RaycastFlags.UIDisable` flag on `m_ToolRaycastSystem.raycastFlags` handles this — confirm it also blocks our custom hit-test (it does not automatically; we must gate on `!UIDisabled` check like Traffic does at `LaneConnectorToolSystem.cs:156`).
