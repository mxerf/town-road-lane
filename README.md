# Town Road Lane — Road Markings

**English** | [Русский](README.ru.md)

[![PDX Mods subscribers](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.paradox-interactive.com%2Fmods%3FmodId%3D150863%26os%3Dwindows&query=%24.modDetail.subscriptions&label=PDX%20Mods&suffix=%20subscribers&color=2d6cdf&cacheSeconds=3600)](https://mods.paradoxplaza.com/mods/150863/Windows)
[![Likes](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.paradox-interactive.com%2Fmods%3FmodId%3D150863%26os%3Dwindows&query=%24.modDetail.ratingsTotal&label=likes&color=e05d6f&cacheSeconds=3600)](https://mods.paradoxplaza.com/mods/150863/Windows)
[![Mod version](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.paradox-interactive.com%2Fmods%3FmodId%3D150863%26os%3Dwindows&query=%24.modDetail.userModVersion&label=version&color=3fb950&cacheSeconds=3600)](https://mods.paradoxplaza.com/mods/150863/Windows)
[![Game version](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.paradox-interactive.com%2Fmods%3FmodId%3D150863%26os%3Dwindows&query=%24.modDetail.requiredVersion&label=Cities%3A%20Skylines%20II&color=8a63d2&cacheSeconds=3600)](https://mods.paradoxplaza.com/mods/150863/Windows)

A road-marking mod for **Cities: Skylines II**: a manual marking editor for intersections plus automatic edge/parking lane markings for ordinary town roads.

![Intersection markings with curved area fills](src/TownRoadLane/Properties/Screenshots/Screenshot_04.jpg)

## Features

### Marking editor
Click any intersection and draw your own markings:

- **Lines** between anchor dots — solid, dashed (short / normal / long), double solid, and G87 white or yellow styles, with adjustable curvature and per-segment visibility.
- **Anchor dots everywhere you need them** — on every lane boundary, on the carriageway edge (including past parking lanes), and in a second "setback" row 8 m before the junction, so you can draw the solid no-lane-change stretch in front of the stop line.
- **Area fills** over any polygon you outline — junction box (yellow box), white/yellow hatching, green bike lane, red bus lane, concrete, asphalt patch.
- **Hide vanilla markings** per intersection to start from a clean slate.
- In-game panel (English + Russian) and hotkeys: `Ctrl+M` toggle tool, `Y` cycle line style, `A` area mode, `U` cycle area fill.

![Zebra crossing and custom junction markings](src/TownRoadLane/Properties/Screenshots/TRL_9f56d721.jpg)

![Y-junction with custom lane guidance](src/TownRoadLane/Properties/Screenshots/TRL_f43d4d3e.jpg)

![Area fills](src/TownRoadLane/Properties/Screenshots/TRL_a051f879.jpg)

### Automatic markings
Ordinary city roads with 3 m lanes get proper edge lane markings — the same way highways do — so parking, sidewalks and stops react correctly. Parking lane markings included.

![Forest road with edge markings](src/TownRoadLane/Properties/Screenshots/TRL_7f19dcc1.jpg)

![City overview](src/TownRoadLane/Properties/Screenshots/TRL_4996f529.jpg)

## Known limitations

- **The mod is purely visual.** Markings never affect how vehicles actually drive. To change real lane behavior use [Traffic](https://github.com/krzychu124/Traffic) — set up lane connections and directions there first, then draw your markings to match. The two mods don't sync automatically.
- **Nearly-tangent line contacts** (well under ~8°) are treated as a graze rather than a crossing — no anchor dot appears at such touch points (by design; prevents jittery duplicate anchors).
- **Stretched junction connections** — highway ramps/merges and other junctions where the road/junction boundary is not perpendicular to the road — can misplace the anchor dots relative to the painted lines: the paint ends square to each lane, while the dots sit on the skewed boundary. Fix: normalize the junction with the **Node Controller** mod so the connection cross-lines run perpendicular to the road (strongly recommended). The setback dot row 8 m before the junction also stays usable.
- **Area fill edges** curve along a line only when that line was drawn with this mod. The game's own markings are not traced — along them the fill edge stays a straight segment between its points.
- **Move It:** after moving or reshaping a road, line markings adapt to the new geometry, but area fills may not — delete and redraw them.

## Dependencies

Line styles and area fills come from the G87 marking packs (installed automatically as PDX Mods dependencies):

- [G87] Road Markings (id 97828)
- [G87] Road Markings: Stripes and Chevrons (id 98624)

Optional: the **Asphalt patch** area fill uses the separate "G87 Vanilla Asphalt Pavement" mod — without it that fill falls back to concrete.

## Building

Requires the official CS2 modding toolchain (`CSII_TOOLPATH` set up by the game's mod project wizard) and Node.js for the UI bundle.

```powershell
cd src/TownRoadLaneUI
npm install          # once
cd ..
dotnet build src/TownRoadLane/TownRoadLane.csproj
```

The build compiles the C# systems, bundles the React UI via webpack, and deploys everything to the local `Mods/TownRoadLane` folder.

## Project layout

- `src/TownRoadLane/` — C# mod: ECS systems for marking topology, emission, rendering, and the in-game tool.
- `src/TownRoadLaneUI/` — React (cohtml) UI: tool panel, toolbar button, localization.

## Credits

- **G87** — the marking prefab packs this mod builds its styles on.
- Author: **mxerf**

## License

[GPL-3.0](LICENSE)
