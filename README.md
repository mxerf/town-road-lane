# Town Road Lane — Road Markings

A road-marking mod for **Cities: Skylines II**: a manual marking editor for intersections plus automatic edge/parking lane markings for ordinary town roads.

![Line editor](src/TownRoadLane/Properties/Screenshots/Screenshot_01.png)

## Features

### Marking editor
Click any intersection and draw your own markings:

- **Lines** between lane endpoint dots — Solid, Dashed, Double Solid, and G87 line styles, with adjustable curvature and per-segment visibility.
- **Area fills** over any polygon you outline — junction box (yellow box), white/yellow hatching, green bike lane, red bus lane, concrete.
- **Hide vanilla markings** per intersection to start from a clean slate.
- In-game panel (English + Russian) and hotkeys: `Ctrl+M` toggle tool, `Y` cycle line style, `A` area mode, `U` cycle area fill.

![Area fills](src/TownRoadLane/Properties/Screenshots/Screenshot_02.png)

### Automatic markings
Ordinary city roads with 3 m lanes get proper edge lane markings — the same way highways do — so parking, sidewalks and stops react correctly. Parking lane markings included.

![City overview](src/TownRoadLane/Properties/Screenshots/Screenshot_03.png)

## Dependencies

Line styles and area fills come from the G87 marking packs (installed automatically as PDX Mods dependencies):

- [G87] Road Markings (id 97828)
- [G87] Road Markings: Stripes and Chevrons (id 98624)

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
