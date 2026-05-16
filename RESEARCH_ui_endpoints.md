# RESEARCH: Marking Endpoint Extraction + UI Integration

---

## Part 1: Endpoint world-space extraction

### 1.1 How vanilla computes lane-corner positions

Source: `decomp/Game/Game.Net/SecondaryLaneSystem.cs` lines 359–422.

For every sublane `subLane` on an edge/node that is NOT a master-lane and NOT already a secondary-lane, and whose prefab has at least one `SecondaryNetLane` entry, vanilla computes four world-space corner points:

```
float2 float7 = math.normalizesafe(MathUtils.StartTangent(curve.m_Bezier).xz);   // tangent at start
float2 float8 = math.normalizesafe(MathUtils.EndTangent(curve.m_Bezier).xz);     // tangent at end
float3 a  = curve.m_Bezier.a;   // raw start point
float3 d  = curve.m_Bezier.d;   // raw end point
float3 d2 = curve.m_Bezier.d;
float3 a2 = curve.m_Bezier.a;
float2 width = netLaneData.m_Width;   // (x = left-side width, y = right-side width)
// ... optionally += componentData.m_WidthOffset from NodeLane ...

// Four corners:
a.xz  += MathUtils.Right(float7) * (width.x * 0.5f);   // start-right corner
d.xz  += MathUtils.Left(float8)  * (width.x * 0.5f);   // end-left corner (same side as start-right)
d2.xz += MathUtils.Right(float8) * (width.y * 0.5f);   // end-right corner
a2.xz += MathUtils.Left(float7)  * (width.y * 0.5f);   // start-left corner
```

Two `LaneCorner` structs are then emitted:

| Struct field        | Non-inverted corner             | Inverted corner                |
|---------------------|---------------------------------|--------------------------------|
| `m_StartPosition`   | `a`  (start, right side)        | `d`  (end, left side)          |
| `m_EndPosition`     | `d2` (end, right side)          | `a2` (start, left side)        |
| `m_Tangents`        | `float4(float7, float8)`        | `float4(float8, float7)`       |
| `m_StartNode`       | `lane.m_StartNode`              | `lane.m_EndNode`               |
| `m_EndNode`         | `lane.m_EndNode`                | `lane.m_StartNode`             |
| `m_Inverted`        | `false`                         | `true`                         |

The `m_StartPosition` / `m_EndPosition` of each `LaneCorner` are the exact world-space attachment points where vanilla tries to connect a secondary-lane (marking line). They sit at the edge of the lane (half-width offset from the centreline, perpendicular to the tangent).

These `LaneCorner` structs are internal to the Burst job — they are never exposed as ECS components. Our tool must recompute them at query time.

---

### 1.2 Relevant geometry components on an edge entity

| Component | Key fields | File:line |
|---|---|---|
| `Edge` | `m_Start : Entity` (start-node), `m_End : Entity` (end-node) | `Game.Net/Edge.cs:8-9` |
| `Curve` | `m_Bezier : Bezier4x3`, `m_Length : float` — `m_Bezier.a` = world-space start point, `m_Bezier.d` = end point | `Game.Net/Curve.cs:11` |
| `EdgeGeometry` | `m_Start : Segment`, `m_End : Segment` — cap geometry at each node end; `m_Bounds : Bounds3` | `Game.Net/EdgeGeometry.cs:9-13` |
| `StartNodeGeometry` | `m_Geometry : EdgeNodeGeometry` — node-cap geometry for the start node end of this edge | `Game.Net/StartNodeGeometry.cs:8` |
| `EndNodeGeometry` | `m_Geometry : EdgeNodeGeometry` — node-cap geometry for the end node end | `Game.Net/EndNodeGeometry.cs:8` |
| `EdgeNodeGeometry` | `m_Left : Segment`, `m_Right : Segment`, `m_Middle : Bezier4x3` — left/right cap boundaries | `Game.Net/EdgeNodeGeometry.cs:8-13` |
| `SubLane` buffer | `m_SubLane : Entity` — entity handle for each sublane | `Game.Net/SubLane.cs:11` |
| `EdgeLane` | `m_EdgeDelta : float2` — `(0,x)` = touches start node, `(x,1)` = touches end node | `Game.Net/EdgeLane.cs:9` |
| `Lane` | `m_StartNode : PathNode`, `m_EndNode : PathNode` — owner entity encoded in high 32 bits; `PathNode.GetOwnerIndex()` returns `(int)(key >> 32)` | `Game.Net/Lane.cs:10-14`, `Game.Pathfind/PathNode.cs:113-116` |

`PathNode.OwnerEquals(other)` compares only the high-32 bits (owner entity index). So to check whether lane `m_StartNode` belongs to a given node entity `N`: `lane.m_StartNode.GetOwnerIndex() == N.Index`.

---

### 1.3 Strategy to extract marking attachment points for our UI

**Goal**: given a selected node entity `N`, produce a list of `(Entity laneEntity, float3 attachPoint, float2 tangent, bool isStartSide)` — one entry per side of each qualifying sublane at that node.

**Algorithm (non-Burst managed system for UI query, mirroring ParkingPairDumpSystem pattern):**

```csharp
// 1. Get connected edges for node N
var connectedEdges = EntityManager.GetBuffer<ConnectedEdge>(N, isReadOnly: true);

var results = new List<LaneAttachPoint>();

foreach (var ce in connectedEdges)
{
    Entity edgeEntity = ce.m_Edge;
    Edge edge = EntityManager.GetComponentData<Edge>(edgeEntity);
    bool nodeIsStart = edge.m_Start == N;   // true → N is start node of this edge

    var subLanes = EntityManager.GetBuffer<SubLane>(edgeEntity, isReadOnly: true);

    foreach (var sl in subLanes)
    {
        Entity le = sl.m_SubLane;

        // Only EdgeLane sublanes have positions relevant to edge-ends at a node.
        // NodeLane sublanes are the crossing lanes that live ON the node — skip for now.
        if (!EntityManager.HasComponent<EdgeLane>(le)) continue;
        if (!EntityManager.HasComponent<Curve>(le)) continue;
        // Skip master lanes and already-secondary lanes (mirrors SecondaryLaneSystem line 362-365)
        if (EntityManager.HasComponent<MasterLane>(le)) continue;
        if (EntityManager.HasComponent<SecondaryLane>(le)) continue;

        var edgeLane = EntityManager.GetComponentData<EdgeLane>(le);
        // m_EdgeDelta.x == 0 → lane touches start node; m_EdgeDelta.y == 1 → touches end node
        bool touchesN = nodeIsStart
            ? edgeLane.m_EdgeDelta.x == 0f
            : edgeLane.m_EdgeDelta.y == 1f;
        if (!touchesN) continue;

        var curve = EntityManager.GetComponentData<Curve>(le);
        // Lane.m_StartNode owner index == edge entity index for EdgeLanes
        var lane = EntityManager.GetComponentData<Lane>(le);

        // Width from prefab data (same lookup as vanilla line 380)
        var prefabRef = EntityManager.GetComponentData<PrefabRef>(le);
        var netLaneData = prefabSystem.GetComponentData<NetLaneData>(prefabRef.m_Prefab);
        float2 width = netLaneData.m_Width;
        if (EntityManager.TryGetComponentData<NodeLane>(le, out var nodeLane))
            width += nodeLane.m_WidthOffset;

        float2 startTangent = math.normalizesafe(MathUtils.StartTangent(curve.m_Bezier).xz);
        float2 endTangent   = math.normalizesafe(MathUtils.EndTangent(curve.m_Bezier).xz);

        float3 a  = curve.m_Bezier.a;
        float3 d  = curve.m_Bezier.d;
        float3 d2 = curve.m_Bezier.d;
        float3 a2 = curve.m_Bezier.a;

        // Right-side offsets (same formulas as SecondaryLaneSystem lines 385-388)
        a.xz  += MathUtils.Right(startTangent) * (width.x * 0.5f);
        d.xz  += MathUtils.Left(endTangent)    * (width.x * 0.5f);
        d2.xz += MathUtils.Right(endTangent)   * (width.y * 0.5f);
        a2.xz += MathUtils.Left(startTangent)  * (width.y * 0.5f);

        // Attachment point at node N is whichever end of the lane touches N:
        float3 attachRight = nodeIsStart ? a  : d;    // right-side corner at N
        float3 attachLeft  = nodeIsStart ? a2 : d2;   // left-side corner at N
        float2 tangentAtN  = nodeIsStart ? startTangent : endTangent;

        results.Add(new LaneAttachPoint { Lane = le, Pos = attachRight, Tangent = tangentAtN, IsRight = true  });
        results.Add(new LaneAttachPoint { Lane = le, Pos = attachLeft,  Tangent = tangentAtN, IsRight = false });
    }
}
```

The `ConnectedEdge` buffer on a node entity is the starting point; `Edge.m_Start == N` distinguishes which end the node occupies. `EdgeLane.m_EdgeDelta` then confirms the sublane actually touches that end.

**Key pitfall**: sublanes that span the full edge have `m_EdgeDelta == (0, 1)` — they touch both ends. They will appear in the results for whichever node is being queried, which is correct — each end gets its own attach points.

---

### 1.4 Marking-specific positions vs. lane-corner positions

The actual drawn marking (a `SecondaryLane` sublane entity) gets its curve from `CreateSecondaryLane()` (lines 1009–1110), which calls `NetUtils.OffsetCurveLeftSmooth(curve2.m_Bezier, leftWidth * -0.5f - secondaryLaneData.m_CutOffset)` or a weighted lerp between the two flanking lane curves. The marking endpoint (`m_Bezier.a` or `.d` of the marking sublane's `Curve`) lies offset from the lane-corner position by `m_CutOffset` and optionally `m_PositionOffset`.

**Recommendation**: for the per-node UI tool, use the **lane-corner positions** (the attachment algorithm above), not the marking sublane endpoints. Reasons:

1. Lane-corner positions are computed from first principles (edge curve + width) and do not require iterating existing secondary-lane entities. This matters because our tool must show potential attachment points even before a marking exists.
2. The offset between a lane-corner position and the actual marking endpoint is small (typically < 0.2 m) and consistent — imperceptible at UI dot scale.
3. Lane-corner positions are the canonical "candidate pairing point" that vanilla itself uses to decide whether two lane-corners get a marking (lines 591–621). Using the same points guarantees our UI dots correspond to exactly the pairs vanilla evaluates.

If sub-centimetre accuracy is needed later (e.g. to snap a drawn line to the real marking endpoint), read `Curve.m_Bezier.a`/`.d` from the existing `SecondaryLane` sublane entities that already have `Owner` == the edge entity.

---

## Part 2: UI integration for activating the tool

### 2.1 Traffic mod's activation mechanism

Traffic uses **both** a hotkey and a floating toolbar button in the game HUD.

**Hotkey path** (`_refs/Traffic/Code/ModSettings.Keybindings.cs:21`):
```csharp
[SettingsUIKeyboardAction(KeyBindAction.ToggleLaneConnectorTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
```
Default binding: `Ctrl+R` (`ModSettings.Keybindings.cs:58`).

**Runtime toggle** (`_refs/Traffic/Code/UISystems/ModUISystem.cs:134-136`):
```csharp
if (_toggleLaneConnectorToolAction.WasPerformedThisFrame())
{
    _toolSystem.activeTool = _toolSystem.activeTool == _laneConnectorTool ? _defaultTool : _laneConnectorTool;
}
```

**Floating toolbar button** (TypeScript/React UI):
- `_refs/Traffic/UI/src/index.tsx:6`: `moduleRegistry.append('GameTopLeft', ModUI)` — injects the mod's root component into the game's top-left panel slot.
- `_refs/Traffic/UI/src/modUI/modUI.tsx:64-69`: renders a `<Button variant="floating" src={trafficIcon} onSelect={toggleMenu}/>` using a custom SVG icon.
- Clicking it opens a `<ToolSelectionPanel>` popup. Clicking a panel button calls `trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, ModTool.LaneConnector)` which is received by `ModUISystem.ToggleTool()` on the C# side.

So the tool is activated by either the hotkey (C# `OnUpdate`) or the React button (C# `TriggerBinding`). Both ultimately do `_toolSystem.activeTool = _laneConnectorTool`.

---

### 2.2 Toolbar button technical path

The CS2 vanilla `ToolbarUISystem` (`decomp/Game/Game.UI.InGame/ToolbarUISystem.cs`) manages asset prefab groups and selection — it has no generic "add a mod button here" API. Mods cannot inject into the vanilla toolbar group data.

Traffic's approach is the correct one: **inject into a named UI slot** via the CS2 modding React API.

**C# side** (no special API needed beyond standard UISystemBase):
1. Register `TriggerBinding`s for tool activation and state read-back via `AddBinding(new TriggerBinding<ModTool>(Mod.MOD_NAME, "ToggleTool", ToggleTool, ...))`.
2. Expose `GetterValueBinding` for current tool state so React can highlight the active button.

**React/TypeScript side** (requires a compiled UI bundle shipped with the mod):
1. `index.tsx`: call `moduleRegistry.append('GameTopLeft', YourRootComponent)`.
2. Root component: render a floating `<Button variant="floating" src={yourIcon} onSelect={...}/>`.
3. On click: call `trigger(modId, 'ToggleTool', ...)`.

The `GameTopLeft` slot is the standard injection point used by Traffic. There is no HTML file to edit; the modding SDK's `moduleRegistry.append` handles the DOM insertion.

**Without a UI bundle** (C#-only, hotkey-only): the button cannot appear in-game without React code. The C# binding infrastructure only handles data and events; the visual button must be React.

---

### 2.3 Hotkey-only fallback

This is the minimal viable path for v1, requiring no TypeScript build toolchain.

**Pattern** (mirror `ModSettings.Keybindings.cs:21` + `ModUISystem.cs:57-61,134-136`):

```csharp
// In Setting.cs / ModSetting subclass — add attribute at class level:
[SettingsUIKeyboardAction(ToggleMarkingTool, Usages.kDefaultUsage, Usages.kToolUsage)]
public partial class Setting : ModSetting
{
    public const string ToggleMarkingTool = "ToggleMarkingTool";

    [SettingsUISection(kSection, kToggleGroup)]
    [SettingsUIKeyboardBinding(BindingKeyboard.M, ToggleMarkingTool, ctrl: true)]
    public ProxyBinding ToggleMarkingToolBinding { get; set; }
}

// In your UISystem or a GameSystemBase OnUpdate:
private ProxyAction _toggleAction;
private MarkingToolSystem _markingTool;
private DefaultToolSystem _defaultTool;
private ToolSystem _toolSystem;

protected override void OnCreate()
{
    _toolSystem    = World.GetOrCreateSystemManaged<ToolSystem>();
    _defaultTool   = World.GetOrCreateSystemManaged<DefaultToolSystem>();
    _markingTool   = World.GetOrCreateSystemManaged<MarkingToolSystem>();
    _toggleAction  = Mod.m_Setting.GetAction(Setting.ToggleMarkingTool);
}

protected override void OnUpdate()
{
    if (_toggleAction.WasPerformedThisFrame())
    {
        _toolSystem.activeTool = _toolSystem.activeTool == _markingTool
            ? _defaultTool
            : _markingTool;
    }
}
```

`Setting.GetAction(string name)` is the same method Traffic uses (`ModSettings.cs`, called at line 57). The key appears in the game's keybinding options screen under the mod's settings section automatically.

---

### 2.4 Recommendation for v1

**Use hotkey-only activation for the first cut.**

Rationale:
- No TypeScript/webpack toolchain required. Traffic's React bundle is a non-trivial build dependency (separate `package.json`, `webpack.config.js`, compiled output must be shipped alongside the DLL).
- The hotkey path is fully functional, user-configurable via the mod settings UI (the binding shows up in Options → Keybindings), and is what Traffic itself uses in addition to the button.
- The floating button (Traffic's `GameTopLeft` injection) is additive — it can be wired up in a later phase once the C# tool logic is proven. The C# `TriggerBinding` infrastructure is forward-compatible: adding React later just adds a UI skin on top of the existing C# bindings.

For v1: implement `SettingsUIKeyboardAction` + `GetAction(...).WasPerformedThisFrame()` → `_toolSystem.activeTool = _markingTool`. Ship no UI bundle. The user activates the tool via `Ctrl+M` (or whatever key is bound).

---

## Part 3: Open questions

- **Which sublanes have `SecondaryNetLane` prefab data?** Our algorithm filters by `EdgeLane` presence but does not (yet) check `m_PrefabSecondaryLanes.TryGetBuffer(prefabRef.m_Prefab, ...)`. Without this filter the attach-point list will include lanes that vanilla never generates markings for (e.g. utility lanes). Experiment: add this filter and compare the result count to what `CustomSecondaryLaneSystem` actually produces.

- **NodeLane sublanes at intersections** — when a node has its own `SubLane` buffer (intersection node with crossing lanes), do those `NodeLane` entities also need corner points? The algorithm above skips them (`!HasComponent<EdgeLane>`). Verify: does vanilla produce secondary lanes on `NodeLane` entities, or only on `EdgeLane` entities? Check `SecondaryLaneSystem` for calls to `CreateSecondaryLane` in the node-entity branch (look at the `isNode` flag path around line 319).

- **`EdgeLane.m_EdgeDelta` exact values for mid-edge lanes**: the serialization code shows common values are `(0, 0.5)`, `(0.5, 1)`, `(0, 1)`. Lanes with delta `(0.5, x)` do not touch the start node. The float comparison `== 0f` and `== 1f` is what vanilla itself does (line 427). Confirm no precision issues on specific road types by running the `ParkingPairDumpSystem`-style dump and checking `EdgeLane.m_EdgeDelta` values.

- **React UI bundle setup**: if/when a toolbar button is desired, the main unknown is the exact webpack/esbuild configuration required to produce a CS2-compatible bundle (cjs format, correct externals for `cs2/api`, `cs2/ui`, etc.). Traffic's `_refs/Traffic/UI/webpack.config.js` and `package.json` provide a working template. Experiment: copy that config, swap mod name, verify hot-reload against a running CS2 instance.

- **`MathUtils.Right` / `MathUtils.Left` availability**: these are `Colossal.Mathematics` extension methods. Confirm they are accessible from our assembly (not internal). Alternative: implement manually as `new float2(-v.y, v.x)` (right) and `new float2(v.y, -v.x)` (left) if not available.
