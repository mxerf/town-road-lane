# town-road-lane — заметки по разведке

Цель: заставить движок CS2 генерировать «дорожную» разметку у края проезжей части
(краевые линии, разделители полос, разметку перекрёстков) для городских дорог
с полосами 3 м — так же, как это уже делается для шоссейных дорог с полосами 4 м.
Важно не просто декаль, а полноценное использование инструментов движка
(чтобы парковки/тротуары/остановки реагировали корректно).

## Окружение (из переменных среды CSII_*)
- Игра: `C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II`
- Managed dll: `...\Cities2_Data\Managed\` (Game.dll и т.д.)
- Modding toolchain: `...\Content\Game\.ModdingToolchain\`
  - `ColossalOrder.ModTemplate.1.0.0.nupkg` — официальный шаблон мода (C#)
  - `ModPostProcessor.exe`, `ModPublisher.exe`
- `CSII_TOOLPATH = %LocalLow%\Colossal Order\Cities Skylines II\.cache\Modding`
  (там лежат Mod.props / Mod.targets, которые импортит .csproj шаблона)
- Unity 2022.3.62f2, Entities 1.3.10

## Декомпиляция
- Установлен `ilspycmd` (dotnet global tool).
- `Game.dll` декомпилирован в `./decomp/Game/` (4366 .cs файлов, ~41 МБ).
  Команда: `ilspycmd -p -o ./decomp/Game "<path>/Game.dll"`

## Что уже выяснено об архитектуре дорог
- Сечение дороги = `RoadPrefab`/`NetGeometryPrefab` → `NetSectionInfo` (`NetSectionPiece[]`)
  → `NetPiecePrefab` → `NetPieceInfo` + `NetPieceLanes` (`NetLaneInfo[]`: позиция + ссылка на `NetLanePrefab`)
- `NetLanePrefab` / `NetLaneGeometryPrefab` — полоса; `NetLaneGeometryPrefab` имеет `NetLaneMeshInfo[] m_Meshes`
  → линии разметки = `NetLaneGeometryPrefab` с мешем линии, размещённый в позиции края.
- `AuxiliaryLanes` (компонент на `NetLanePrefab`) → `AuxiliaryLaneInfo[]`:
  `{ m_Lane, m_Position, m_RequireAll/RequireAny/RequireNone : NetPieceRequirements[], m_Spacing, m_EvenSpacing, m_FindAnchor }`
  → даёт полосе дополнительные суб-полосы по условиям. `AuxiliaryNetLane` — рантайм-буфер.
- `NetPieceRequirements` enum содержит, среди прочего: `Edge`, `Sidewalk`, `OppositeSidewalk`,
  `ParkingSpaces`, `BusStop`, `Crosswalk`, `Intersection`, `Node`, `DeadEnd`, `Median`, `Pavement`, ...
- "marking"/"Marking" как имя типа в коде ОТСУТСТВУЕТ → разметка не отдельная сущность, а меш на lane/piece.
- `Game.Net.LaneSystem` читает `AuxiliaryNetLane` (генерирует aux-полосы).
- `NetCompositionHelpers`, `NetInitializeSystem` — заполняют буферы из `AuxiliaryLaneInfo`.

## ВАЖНАЯ ЗАЦЕПКА (от пользователя)
- В моде **Road Builder** разница воспроизводится чисто ШИРИНОЙ ПОЛОСЫ:
  полосы 4 м → богатая разметка (краевые линии, разделители, разметка перекрёстков);
  полосы 3 м → разметки у края нет.
- => где-то есть ПОРОГ ПО ШИРИНЕ (~4 м), по которому навешиваются marking-lane'ы.
  Искать: `4f`, `>= 4`, сравнения с `m_Width`/`width` в `Game.Net.LaneSystem`,
  `NetCompositionHelpers`, `NetInitializeSystem`, `NetSectionInfo`, а также проверить,
  не сидит ли этот порог в самом моде Road Builder (тогда он нам не нужен —
  цель = ванильные городские дорожные префабы).
- Конечная цель: ВАНИЛЬНАЯ городская дорога 3 м получает краевую разметку,
  при этом edge-lane / parking-физика остаётся корректной. БЕЗ хака "сделать полосу 4 м"
  (4 м в черте города — слишком широко и выглядит плохо).

## РЕЗУЛЬТАТ РАЗВЕДКИ (агент, 2026-05-11)
ГЛАВНОЕ: в коде CS2 НЕТ порога по ширине и НЕТ логики "если шоссе → разметка".
Разница шоссе/город = 100% в ДАННЫХ АССЕТОВ. Порог "4м" сидит ВНУТРИ мода Road Builder,
а не в движке => наш мод не про Road Builder, а про ванильные городские дорожные префабы.

Цепочка: RoadPrefab : NetGeometryPrefab → m_Sections : NetSectionInfo[]
  → NetSectionPrefab.m_Pieces : NetPieceInfo[] → NetPiecePrefab (меш асфальта/бордюра)
    + (опц.) NetPieceLanes.m_Lanes : NetLaneInfo[] { m_Lane:NetLanePrefab, m_Position:float3, m_FindAnchor }
       ← фиксированные lane'ы куска (тут сидит ОСЕВАЯ линия)
  NetLanePrefab → (опц.) AuxiliaryLanes.m_AuxiliaryLanes : AuxiliaryLaneInfo[]
     { m_Lane:NetLanePrefab(=NetLaneGeometryPrefab), m_Position:float3 (отн. хост-полосы),
       m_RequireAll/Any/None:NetPieceRequirements[], m_Spacing:float3 (x=боковой шаг пар, z=шаг штрихов),
       m_EvenSpacing, m_FindAnchor }
     ← так делаются РАЗДЕЛИТЕЛИ ПОЛОС и КРАЕВЫЕ ЛИНИИ; хост-полоса получает LaneFlags.HasAuxiliary
  NetLaneGeometryPrefab : NetLanePrefab → m_Meshes : NetLaneMeshInfo[] { m_Mesh:RenderPrefab(тонкий меш+декаль), m_RequireSafe/... }
     ← это и есть визуальная линия разметки

Бейк: NetInitializeSystem.cs — managed→ECS:
  - NetPieceLane buffer на entity куска (≈1320-1369), отсортирован по x
  - AuxiliaryNetLane buffer на entity LANE-prefab'а (≈1782-1818) + ставит LaneFlags.HasAuxiliary хосту
  - SubMesh / NetLaneGeometryData (≈1821-1859)
Runtime: NetCompositionSystem (NetCompositionHelpers.AddCompositionLanes ≈1180-1551) → NetCompositionLane buffer
  (тут попадают ФИКСИРОВАННЫЕ piece-lane'ы, aux — нет).
  LaneSystem.cs (≈2616-2929 edge, ≈6853-6905 node): для каждой composition-lane с HasAuxiliary берёт
  m_PrefabAuxiliaryLanes[lane], для каждого AuxiliaryNetLane: единственный гейт —
  NetCompositionHelpers.TestLaneFlags(aux, compositionData.m_Flags) против CompositionFlags
  (Inverted/Elevated/Node/Tunnel/...). Ширина/тип дороги НЕ проверяются. Дальше CreateEdgeLane вдоль края.

ТОЧКА РАСШИРЕНИЯ (выбрана): load-time prefab patcher через IMod, БЕЗ Harmony.
  1. IMod.OnLoad → одноразовая система после загрузки префабов (или хук PrefabSystem).
  2. PrefabSystem: prefabs / GetPrefab<T> / AddPrefab / UpdatePrefab / AddOrUpdatePrefab.
  3. Найти ванильные городские RoadPrefab'ы; найти крайнюю (у бордюра) проезжую NetLanePrefab в их сечении.
  4. GetComponent<AuxiliaryLanes>() (или AddComponent) → дописать AuxiliaryLaneInfo:
     { m_Lane = <краевая линия NetLaneGeometryPrefab, переиспользовать ту что у шоссе>,
       m_Position = float3(±offsetКБордюру, 0, 0), m_Spacing = float3.zero (сплошная),
       m_RequireAll/Any/None = подобрать (как у шоссе) }
  5. Если префабы уже инициализированы — PrefabSystem.UpdatePrefab(prefab) для ре-бейка.
  Альтернатива: дописать NetPieceLanes.m_Lanes у curb-NetPiecePrefab'а — но он шарится между дорогами,
  тогда сначала клонировать piece/section. (Менее предпочтительно.)
  Harmony на LaneSystem/NetCompositionHelpers — отвергнуто (хрупко, синтез AuxiliaryNetLane в Burst-джобе).

NB: AuxiliaryNet / AuxiliaryNetInfo / AuxiliaryNets — ДРУГАЯ фича (под-сети, напр. труба под дорогой),
НЕ путать с AuxiliaryNetLane / AuxiliaryLanes.

## ПРОЕКТ МОДА
- Шаблон `csiimod` установлен в `dotnet new`. Проект: `src/TownRoadLane/` (создан с `-I` = IncludeSetting).
- Сборка: `cd src/TownRoadLane && dotnet build -c Debug` → автодеплой в `%LocalLow%\...\Mods\TownRoadLane\`.
- Лог мода: `%LocalLow%\Colossal Order\Cities Skylines II\Logs\TownRoadLane.log` (логгер "TownRoadLane.Mod").
- Декомпил Game.dll: `decomp/Game/` (для справки по API).
- ВАЖНО про API доступа к префабам: `PrefabSystem.prefabs` — internal. Использовать
  EntityQuery по `PrefabData` + (`RoadData` для дорог / `NetLaneGeometryData` для marking-prefab'ов),
  затем `PrefabSystem.TryGetPrefab<T>(entity, out p)` (публичный). `PrefabBase.name`, `.components`,
  `TryGet<T>(out)`. Регистрация системы: `updateSystem.UpdateAt<T>(SystemUpdatePhase.PrefabUpdate)`.
- NB: `MasterLane`/`SecondaryNetLane` и т.п. — ECS-компоненты в `Game.Net`, НЕ ComponentBase на префабе.
  На префабе висят ComponentBase из `Game.Prefabs` (CarLane, PedestrianLane, ParkingLane, AuxiliaryLanes, NetPieceLanes, ...).

## Замечено в Logs/
- Установлен `C2VM.CommonLibraries.LaneSystem` (мод Traffic/Lane Connector от C2VM) — референс по lane-системе.
- Road Builder тоже наверняка установлен (искать его лог/мод). С ним v1 не возимся — патч ванильных
  lane-prefab'ов должен подтянуться в RB сам, спец-патч только если RB клонирует префабы.

## !!! ПРОРЫВ (2026-05-11, из дампа TownRoadLane.Mod.log) !!!
Разметка CS2 = механизм `SecondaryLane` / `SecondaryLaneSystem` (Game.Net/SecondaryLaneSystem.cs),
а НЕ AuxiliaryLanes. Раннее заключение "роль типа дороги нет" относилось к LaneSystem/AuxiliaryLanes —
ОШИБОЧНО для разметки. SecondaryLaneSystem процедурно навешивает линии слева/справа от каждой CarLane
на основе: соседних полос (бордюр/тротуар/парковка/другая полоса), темы (ThemeObject EU/NA),
типа дороги (highwayRules), контекста (intersection/roundabout/merge/transport).

Готовые marking-prefab'ы (NetLaneGeometryPrefab с comps=SecondaryLane[,ThemeObject]):
  'EU Highway Edge Line' / 'NA Highway Edge Line'  → mesh 'White Solid Line Mesh'  ← КРАЕВАЯ ЛИНИЯ ШОССЕ
  'EU Parking Edge Line' / 'NA Parking Edge Line'  → 'White Solid Line Mesh'
  'EU Car Center Line - Single Lane' → 'White Dashed Line Mesh'; 'NA ...' → 'Yellow Solid Line Mesh'
  'EU Car Lane Line' / 'NA Car Lane Line' → 'White Dashed Line Mesh'  ← разделитель полос
  + stop/yield/merge/bay/roundabout lines, 'Bike * Lane Line' (White Solid/Dashed Line Mesh)
  Generic meshes: White Solid/Dashed/Double Solid/Yield/Thick Line Mesh, Yellow Solid/Dashed/Double Solid Line Mesh.

ДОРОГИ:
  Small Road / Medium Road: проезжая = 'Car Drive Lane 3' (mesh 'Car Lane 3 Mesh', CarLane w=3 rt=Car|Bicycle),
    pos в куске (0,-0.2,0). Никакой краевой разметки.
  Highway Twoway/Oneway - 2/3/4 lanes: проезжая = 'Highway Drive Lane 4' (mesh 'Highway Lane 4 Mesh', w=4)
    ИЛИ для 3-lane версий — 'Highway Drive Lane 3' (mesh 'Car Lane 3 Mesh'!, w=3, highwayRules) — и У НИХ ЕСТЬ
    краевая разметка ХОТЯ ПОЛОСА 3м => разметку даёт highwayRules / SecondaryLaneSystem, НЕ ширина меша.
    На самих lane/road prefab'ах AUX-marking'ов НЕТ — всё генерит SecondaryLaneSystem.
  ВАЖНО: 'Highway Lane 4 Mesh' визуально шире и сам содержит часть текстуры — но краевые ЛИНИИ это
    отдельные SecondaryLane-prefab'ы, не текстура меша (см. 'EU/NA Highway Edge Line').

=> СЛЕДУЮЩИЙ ШАГ: читать Game.Net/SecondaryLaneSystem.cs — найти условие, при котором добавляется
   edge line / lane line (highwayRules? ширина? тип соседней полосы?). Это и есть точка мода.
   Возможно мод = просто пометить городские дорожные lane-prefab'ы как "highway-like" для целей разметки,
   ИЛИ Harmony на SecondaryLaneSystem, ИЛИ data-driven рычаг (если есть компонент-флаг).

## МЕХАНИЗМ РАЗМЕТКИ — ТОЧНО (из decompile)
`SecondaryLane` (Game.Prefabs/SecondaryLane.cs) — ComponentBase на NetLanePrefab:
  m_LeftLanes[] : SecondaryLaneInfo   ← линии слева от полосы
  m_RightLanes[] : SecondaryLaneInfo  ← линии справа
  m_CrossingLanes[] : SecondaryLaneInfo2 ← линии на перекрёстках (Stop/Yield/Pavement)
  + флаги: m_CanFlipSides, m_DuplicateSides, m_RequireParallel, m_RequireOpposite,
    m_Skip*Overlap, m_FitToParkingSpaces, m_EvenSpacing, m_InvertOverlapCuts,
    m_PositionOffset:float3, m_LengthOffset:float2, m_CutMargin, m_CutOffset, m_CutOverlap, m_Spacing
SecondaryLaneInfo = { m_Lane:NetLanePrefab(marking-prefab), m_RequireSafe/Unsafe/Single/Multiple/
    AllowPassing/ForbidPassing/Merge/Continue/SafeMaster/Roundabout/NotRoundabout : bool } → SecondaryNetLaneFlags
SecondaryLaneInfo2 = { m_Lane, m_RequireStop/Yield/Pavement/Continue : bool }
Бейк: NetLanePrefab.GetPrefabComponents добавляет SecondaryNetLane buffer (если НЕ SecondaryLane сам);
  компонент SecondaryLane → SecondaryLaneData + Game.Net.SecondaryLane archetype.
Runtime: Game.Net/SecondaryLaneSystem.cs (1969 строк) — для каждой CarLane берёт
  m_PrefabSecondaryLanes[prefabRef.m_Prefab] (буфер SecondaryNetLane), и если буфер не пуст —
  раскатывает линии слева/справа с учётом соседних полос/ширины/контекста. (стр.~370 — skip если буфер пуст).
  Никакого встроенного "если шоссе" — РАЗНИЦА ПОЛНОСТЬЮ В ДАННЫХ: какие SecondaryLaneInfo навешаны на lane-prefab.

ИТОГОВЫЙ ПЛАН МОДА (v1, без Harmony):
  1. IMod → система после загрузки префабов.
  2. PrefabSystem: найти 'Highway Drive Lane 3' (или 'Highway Drive Lane 4'/'Highway Drive Lane 3') —
     прочитать его SecondaryLane.m_RightLanes/m_LeftLanes, найти краевую линию ('EU/NA Highway Edge Line').
  3. Найти 'Car Drive Lane 3' (городская проезжая) — GetComponent<SecondaryLane>() или AddComponent.
     Добавить в соответствующий side[] новый SecondaryLaneInfo с m_Lane = краевая линия + те же requirements
     (учесть theme: 'EU Highway Edge Line' для EU темы, 'NA ...' для NA — возможно линия выбирается через
     ThemeObject автоматически; проверить как у highway lane сделано).
  4. PrefabSystem.UpdatePrefab('Car Drive Lane 3') если уже инициализирован.
  5. Также проверить: затрагивает ли это другие дороги, использующие 'Car Drive Lane 3'
     (Medium Road, alley?, oneway?). Если да — ок, всем краевая разметка.
  ВАЖНО открытый вопрос: как 'Highway Drive Lane 3' получает разметку — у него есть СВОЙ SecondaryLane,
  или это завязано на что-то ещё? Это покажет обновлённый дамп.

## !!! РАЗВЕДКА ЗАВЕРШЕНА — МЕХАНИЗМ ЯСЕН ПОЛНОСТЬЮ (2026-05-11) !!!
Связь "линия↔полоса" объявляется СО СТОРОНЫ ЛИНИИ (marking NetLaneGeometryPrefab),
через её компонент SecondaryLane: m_LeftLanes[]/m_RightLanes[] = "я рисуюсь слева/справа от этих lane-prefab'ов".
canFlip=True → может зеркалиться. У проезжих lane-prefab'ов ('Car Drive Lane 3', 'Highway Drive Lane 4'...)
своего SecondaryLane НЕТ — всё объявлено на линиях.

КЛЮЧЕВЫЕ ЛИНИИ:
'EU Highway Edge Line' (mesh 'White Solid Line Mesh', comps SecondaryLane+ThemeObject): canFlip=True, posOffset=0
  m_LeftLanes: 'Highway Drive Lane 4' req=Safe; 'Highway Drive Lane 4' req=Merge|SafeMaster;
               'Highway Drive Lane 4 - Transport' req=Safe; '... - Transport' req=Merge|SafeMaster;
               'Highway Drive Lane 3' req=Safe; 'Highway Drive Lane 3' req=Merge|SafeMaster
'NA Highway Edge Line' — ИДЕНТИЧНО (та же mesh, тот же список m_LeftLanes). Выбор EU/NA — через ThemeObject (авто по теме города).
'Highway Drive Lane 3' использует mesh 'Car Lane 3 Mesh' — ТОТ ЖЕ что у городской 'Car Drive Lane 3'.
  => геометрия краевой линии для 3м highway-полосы идеально подходит для городской 3м полосы.

'Car Drive Lane 3' (городская проезжая, mesh 'Car Lane 3 Mesh', CarLane w=3 rt=Car|Bicycle) — УЖЕ хост у:
  EU/NA Car Lane Line (разделитель полос, reqParallel), EU/NA Car Center Line - Single Lane (осевая, reqOpposite, cutMargin=3),
  EU/NA Car Center Line - Multi Lane, EU/NA Car Bay Line, EU/NA Car Merge Line, EU/NA Car Lane Line - Transport One,
  Bike Car Lane Line / - Cut, Bike Roundabout Lane Line 2.  НО НЕ у Highway Edge Line => нет краевой линии у бордюра.

'EU/NA Parking Edge Line' (mesh 'White Solid Line Mesh', fitToParking=True) — цепляются к ParkingLane-prefab'ам
  (Pathway/Alley Parking Lane ...) + Invisible Pedestrian Lane 0.5. К проезжей полосе НЕ относятся. Не трогать.

=== ПЛАН МОДА v1 (финальный, без Harmony, минимальный) ===
В 'EU Highway Edge Line' И 'NA Highway Edge Line' → добавить в m_LeftLanes записи:
  { m_Lane = 'Car Drive Lane 3', m_RequireSafe = true }
  { m_Lane = 'Car Drive Lane 3', m_RequireMerge = true, m_RequireSafeMaster = true }
  (опц.: то же для 'Car Drive Lane 3 - Tram' и 'Public Transport Lane 3' / '- Tram', если хотим и на трамвайных/PT городских — на v1 можно без)
Затем PrefabSystem.UpdatePrefab(edgeLinePrefab) для каждой изменённой линии.
Реализация: GetComponent<SecondaryLane>() у линии (он есть), Array.Resize(m_LeftLanes, +N), заполнить, UpdatePrefab.
Не забыть: новые SecondaryLaneInfo должны ссылаться на УЖЕ ЗАГРУЖЕННЫЙ 'Car Drive Lane 3' (PrefabSystem.GetPrefab/найти по имени).
Эффект: ВСЕ дороги, использующие 'Car Drive Lane 3' (Small Road, Medium Road, oneway-варианты, ...), получат
  краевую линию у бордюра ровно по тому же пути что шоссе; SecondaryLaneSystem сам разрулит парковки/тротуары/перекрёстки/мержи.
  В Road Builder 3м-полосы (если RB использует 'Car Drive Lane 3') — тоже подтянется автоматически.
Открытый вопрос для проверки в игре: не будет ли краевая линия "налезать" на парковочные карманы / bike lane / тротуар-выступ
  (SecondaryLaneSystem умеет cuts/overlaps — m_SkipSideCarOverlap и пр. на самой линии; у Highway Edge Line эти флаги
  не выставлены, у шоссе работает — должно работать и тут, но смотреть глазами).

## v1 — РАБОТАЕТ (проверено в игре 2026-05-11). screen2.JPG = текущее состояние.
Краевая линия появляется на городских 3м дорогах на участках БЕЗ парковки. В зоне параллельной
парковки линии нет (SecondaryLaneSystem обрезает её по соседнему ParkingLane) — это и есть задача v1.1.

## v1.1 — ПЛАН (зафиксировано с пользователем): разметка зоны ПАРАЛЛЕЛЬНОЙ уличной парковки
Хотим: разделитель в НАЧАЛЕ и В КОНЦЕ блока парковки + продольный ПУНКТИР по всей длине зоны.
БЕЗ разделения отдельных мест (без поперечных чёрточек между слотами).
Дыра в ваниле:
  - 'EU/NA Car Bay Line' (mesh 'White Dashed Line Mesh - Long', canFlip=True): LEFT='Car Drive Lane 3'/PT,
    RIGHT='Car Bay Lane 3' → рисуется ТОЛЬКО когда сосед = 'Car Bay Lane 3' (перпендикулярные/угловые карманы),
    НЕ для параллельной (там сосед — 'Boarding Lane 0' / 'Parking Lane 2', это ParkingLane не CarLane).
  - 'EU/NA Parking Edge Line' (fitToParking=True, 'White Solid Line Mesh'): хостит только
    Pathway/Alley Parking Lane - Perpendicular/Angled67 → не уличные параллельные.
  - 'EU/NA Parking Cross Line' (поперечные T-разделители): тоже только perpendicular/angled.
  => у параллельной уличной парковки в ваниле НЕТ продольной разметки вообще.
Реализация (того же типа что v1, data-driven):
  1. РАСШИРИТЬ RoadPrefabDumpSystem: явно выписать все ParkingLane-prefab'ы (имя, ширина, mesh, в каких дорогах,
     какие marking-линии на них хостятся) — нужно узнать ТОЧНОЕ имя prefab'а параллельной уличной парковки
     (предположит. 'Boarding Lane 0' и/или 'Parking Lane 2' — из дампа Small Road). Также проверить:
     'Car Bay Lane 3' это про что; есть ли отдельный prefab для параллельной vs перпендикулярной.
  2. Взять marking-prefab с пунктирным мешем — кандидат 'EU/NA Car Bay Line' ('White Dashed Line Mesh - Long')
     ИЛИ клонировать. Добавить в его m_LeftLanes/m_RightLanes (сторона со стороны проезжей части!) запись
     { m_Lane = <параллельный ParkingLane>, m_RequireSafe = true } → пунктир вдоль всей зоны.
  3. "Разделитель в начале/конце блока" — это либо короткая поперечная линия (как Parking Cross Line, но 1 на торец зоны),
     либо движок сам ставит торцы при cut'е (m_CutMargin/m_CutOffset). Понять при тесте. Возможно отдельный
     SecondaryLaneInfo2 в m_CrossingLanes у новой/клонированной линии, req на торец зоны.
  4. PrefabSystem.UpdatePrefab на изменённых marking-prefab'ах.
На будущее (v1.2+, кастом цвета — напр. ПЛАТНАЯ парковка синим пунктиром):
  'Blue Dashed Line Mesh' в ваниле НЕТ → нужен свой decal RenderPrefab + свой NetLaneGeometryPrefab + SecondaryLane.
  Если у платной парковки свой ParkingLane-prefab — хостим синюю линию на нём. Если флаг — сложнее (нужен хук/доп.логика).
  Изучить: как клонировать RenderPrefab+material в моде (PrefabSystem.DuplicatePrefab; подмена MeshMaterial buffer);
  или притащить свой материал-ассет через Colossal.IO.AssetDatabase.

## v1.1 — ГОТОВО (2026-05-11). ParkingMarkingPatchSystem.cs:
4 prefab'а (EU/NA × {Longitudinal, End}), все клоны ванильных:
  - Longitudinal: клон 'EU/NA Car Bay Line', mesh→'White Dashed Line Mesh - Dense', SecondaryLane:
    m_LeftLanes={Car Drive Lane 3/-Tram, Public Transport Lane 3/-Tram} req=Safe, m_RightLanes={Parking Lane 2} req=Safe,
    m_CrossingLanes=[], fitToParking=false, canFlip=true. → продольный мелкий пунктир вдоль parking-зоны.
  - End: клон 'EU/NA Parking Cross Line', mesh→'White Solid Line Mesh', SecondaryLane:
    m_Left/Right=[], m_CrossingLanes=[{Parking Lane 2, RequireContinue=false}], fitToParking=true (нужно для crossing-path),
    canFlip=true, m_LengthOffset=(-0.1,0), m_PositionOffset=(0.1,0,0). → 2 поперечные сплошные чёрточки на торцах блока.
  Хост ТОЛЬКО на 'Parking Lane 2' (не 'Boarding Lane 0'!) — Boarding Lane 0 присутствует всегда → перетёр бы краевую.
  Mesh-swap: ищем RenderPrefab по имени (query PrefabData+MeshData), кладём в m_Meshes[].m_Mesh.
  Создание prefab'ов: PrefabSystem.DuplicatePrefab(src, name) → клон с всеми компонентами/мешами/материалом → правим
  SecondaryLane → PrefabSystem.UpdatePrefab(clone). AddPrefab ставит Created → PrefabInitializeSystem подхватит.
  Тонкости из SecondaryLaneSystem (для подстройки): длина торцевой чёрточки = m_Width хост-полосы (Parking Lane 2 = 2м);
  m_LengthOffset.x<0 режет ОБА конца симметрично (broadcast скаляра); m_PositionOffset.x сдвигает поперёк
  (curve=OffsetCurveLeftSmooth(curve, m_CutOffset - m_PositionOffset.x)). Для slotInterval==0 (Parking Lane 2)
  fitToParking НЕ обрезает продольную линию (GetCutRanges не даёт cut ranges), но crossing-path рисует ровно
  2 чёрточки (m=0 старт, m=slotCount=1 конец).

## G87 ROAD MARKINGS — ОПЦИОНАЛЬНЫЙ ИСТОЧНИК АССЕТОВ (аудит 2026-05-11 20:30, G87 в playset'е)
G87 даёт ТОЛЬКО RenderPrefab'ы (меши/декали), НЕ NetLaneGeometryPrefab+SecondaryLane → сам не размещает их
процедурно (только как декали через свой brush). НАШ мод + G87-меш = процедурная разметка G87-стилем.
Полезные G87 RenderPrefab имена (для m_Meshes[].m_Mesh клона; soft-dep, фоллбэк на ваниль):
  'G87 UK Road Markings RoadMarking G87 RM Line Blue Dashed NetLaneDecal_RenderPrefab'  ← СИНИЙ ПУНКТИР (платная!)
  '... RM Line Blue ...' / '... RM Line Blue Double ...'  — синяя сплошная/двойная
  '... UK Carriageway Line {White,Yellow,Red} {,Dashed,Double,Thick} NetLaneDecal_RenderPrefab' — краевые разные
  '... RoadMarkings G87 UK Terminal Line {White,Red,Yellow,} Decal_RenderPrefab' — торцевые (альт. нашему White Solid)
  '... RoadMarkings G87 RM Parking {Blue,Yellow} {x1,x3,Slot,60 Degree,90 Degree,Disabled x1,x1 Diag,x3 Diag} Decal_RenderPrefab' — готовые декали парк-мест
  '... RoadMarking G87 CS2 {Green Bike Lane/Path,Red Bus Lane} {GO,OM,UM} NetLaneDecal_RenderPrefab' — заливки полос
  '... UK {Stop Line,Give Way Dashes ...,ZigZag ...,KC ...,Roundabout ...,Crossing Zebra} ...' — прочая разметка
  'G87 Road Markings SC RoadMarking G87 {Chevrons,Stripes} {1to1,2to1} {30,60,100}cm [Yellow] [Inv] [Wide] NetLane_RenderPrefab' — заливки медиан
NB: мой AppendSource() показал их как src=vanilla (нет platformID / isSubscribedMod=false для локально-кэшир. pdx-модов) — игнор, по имени всё ясно. G87 не добавляет ParkingLane prefab'ов.
Для v1.2+: Settings dropdown стиля; toggle "использовать G87 если установлен"; "платная парковка синим" = runtime-патч,
читающий парковочную политику сегмента (LanePoliciesSystem) и переключающий marking-prefab — меш уже есть.

## АУДИТ PARKING-LANE'ОВ (дамп 2026-05-11 18:21, 39 ParkingLane prefab'ов)
ПАРАЛЛЕЛЬНАЯ УЛИЧНАЯ ПАРКОВКА = пара prefab'ов, ОБА БЕЗ продольной разметки:
  - 'Boarding Lane 0'  slot=(0,0) angle=0  ← ГЛАВНЫЙ. ~50 дорог: Small/Medium/Large/XL Road + все oneway/asymmetric/
    divided варианты + мосты/набережные. Ширина 0. Хостит: НИЧЕГО.
  - 'Parking Lane 2'   slot=(2,0) angle=0  ← вторичный. ~26 дорог (Small/Medium/Large/XL + мосты/набережные).
    Ширина 2. Хостит только Stop/Yield Line (CROSSING×1). Есть НЕ во всех дорогах с парк. (нет в Small Road Oneway-3lanes, Medium Road Asymmetric и др. — там только Boarding Lane 0).
  slot.y=0 у обоих → SlotInterval==0 → FitToParkingLane НЕ делит на дискретные слоты (другая ветка по StartingLane/EndingLane).
  Из дампа Small Road: Boarding Lane 0 на pos.x≈-1, Parking Lane 2 на pos.x≈-1.5..-2 (от центра проезжей полосы). Стоят парой.
ПЕРПЕНДИКУЛЯРНАЯ/УГЛОВАЯ парковка дорог: 'Parking Lane - Perpendicular/Angled67 *' (Small/Medium - Double Sided Parking[Angled])
  → хостится 'EU/NA Parking Cross Line' (поперечные разделители мест, fitToParking, CROSSING×1). Edge-line у НЕЁ НЕТ
  ('EU/NA Parking Edge Line' хостит только Pathway/Alley варианты, НЕ дорожные Perpendicular!). Тоже дыра, но не наша сейчас.
Готовые пунктирные line-меши: 'White Dashed Line Mesh' / '- Dense' / '- Long' / '- Dense Long'. Синего НЕТ (только White/Yellow).
'EU/NA Car Bay Line' = меш 'White Dashed Line Mesh - Long', хостит 'Car Bay Lane 3' (RIGHT, для bay/перпенд. карманов CarLane w=3) + 'Car Drive Lane 3'/PT (LEFT). canFlip=True, fitToParking=False.

## ПРЕДЛОЖЕНИЕ v1.1 (мне, на возврат пользователя)
Цель: продольный пунктир вдоль всей зоны параллельной парковки + торцы блока. Без разделения отдельных мест.
Вариант A (минимальный): дописать в 'EU/NA Car Bay Line'.m_LeftLanes (или RIGHT — сторона проезжей части) запись
  { m_Lane='Parking Lane 2', m_RequireSafe=true } (Parking Lane 2 — т.к. у неё ширина 2 → внятный край у проезжей части;
  у Boarding Lane 0 ширина 0 → линия ляжет по оси). Но Parking Lane 2 не во всех дорогах → возможно хостить НА ОБОИХ
  (Boarding Lane 0 + Parking Lane 2) и подобрать визуально / через m_PositionOffset. PrefabSystem.UpdatePrefab.
Вариант B (чище, рекомендую если есть время): создать СВОЙ NetLaneGeometryPrefab 'TownRoadLane Parallel Parking Line'
  (PrefabBase.Create<NetLaneGeometryPrefab>; m_Meshes = [{ m_Mesh = ванильный RenderPrefab 'White Dashed Line Mesh - Long' }];
  добавить ComponentBase SecondaryLane с m_LeftLanes/m_RightLanes хостом на Boarding Lane 0 + Parking Lane 2, fitToParking=false,
  m_PositionOffset подобран; PrefabSystem.AddPrefab). Не загрязняет ванильный Car Bay Line, легче дать опции цвета/типа.
Торцы блока: (1) SecondaryLaneSystem сам обрезает по StartingLane/EndingLane — может торец выйдет естественно (линия кончается);
  (2) если нужна поперечная чёрточка — SecondaryLaneInfo2 в m_CrossingLanes с m_RequireContinue=false (как у Parking Cross Line:
  entries с req Continue = внутренние, без = торцы).
КАСТОМ "платная парковка синим" (v1.2+): синего меша нет → нужен свой decal-материал (проверить мод Recolor id 84638 — не хукает ли
  lane-декали; иначе притащить свой материал-ассет). + ВАЖНО: в дампе НЕТ "paid"-вариантов ParkingLane → платная парковка скорее
  ПОЛИТИКА сегмента (LanePoliciesSystem), не отдельный prefab → подсветить синим = runtime-патч смотрящий политику + переключающий
  marking. Сложнее, отдельная задача.

## ПРОБЛЕМА АВТОЗАПУСКА ИГРЫ ИЗ-ПОД АГЕНТА (2026-05-11)
Не получилось запустить CS2 программно: steam://run/949230 (Steam не был запущен — некому обработать протокол);
Cities2.exe напрямую (стартует и сразу падает — нет Steam DRM); steam.exe -applaunch 949230 (не поднялось за 7 мин — Steam
холодный старт мог хотеть логин/2FA). runOnce.txt с "--continuelastsave" положен в %UserDataPath% — должен сработать при ЛЮБОМ
следующем запуске (игра читает его и удаляет; формат опции через Mono.Options: -continuelastsave / --continuelastsave).
ВЫВОД: запуск игры — на пользователе. Сборку/деплой делаю автоматически по команде "закрыл". Свежий сейв = 11-мая-17-47-08.cok
(самый свежий → -continuelastsave грузит его). Сейва с именем "testtest" нет.

## АВТОНОМНЫЙ РЕЖИМ (пользователь отошёл 2026-05-11)
Разрешено: ТОЛЬКО аудит/диагностика. Код фичи v1.1 НЕ трогать — оставить разбор + предложение.
- Расширить RoadPrefabDumpSystem: выписать все ParkingLane-prefab'ы (имя/ширина/mesh/в каких дорогах/
  какие marking-линии хостятся), особенно — что стоит в Small/Medium Road с ПАРАЛЛЕЛЬНОЙ парковкой.
- Цикл: закрыть игру (если идёт) → dotnet build → деплой → запустить CS2 → снять Logs/TownRoadLane.Mod.log → анализ.
- Запуск игры: пробовать steam://run/949230 и прямой Cities2.exe. ВНИМАНИЕ: Paradox Launcher может мешать
  (не отдаёт аргументы / свой playset / своё окно). Если упрётся — не биться, записать в выводы, аудит сделать
  на уже имеющемся логе (tool-result b5fabltl8.txt — SecondaryLane дамп; boxy0weba.txt — road/laneGeom дамп).
- Автозагрузка сейва: у пользователя есть тестовый сейв "testtest"; он думает что есть launch-аргумент для
  автозагрузки (типа -continuelastgame / --loadGame <name>) — проверить в декомпиле SceneFlow/GameManager какой формат.
  Если не выйдет — диагностика отрабатывает и в главном меню (PrefabUpdate фаза), для parking-lane анализа карта не нужна.

## Реализация патчера v1 — ГОТОВ, РАБОТАЕТ
Файл: src/TownRoadLane/EdgeMarkingPatchSystem.cs (GameSystemBase, one-shot, фаза PrefabUpdate).
Логика: находит по имени NetLanePrefab'ы 'EU/NA Highway Edge Line' и
  'Car Drive Lane 3' / 'Car Drive Lane 3 - Tram' / 'Public Transport Lane 3' / '- Tram';
  у каждой edge-line берёт SecondaryLane, дописывает в m_LeftLanes по 2 SecondaryLaneInfo на каждую
  city-lane: {RequireSafe} и {RequireMerge,RequireSafeMaster} (зеркало того что там для 'Highway Drive Lane 3');
  пропускает дубликаты; вызывает PrefabSystem.UpdatePrefab(edgeLine).
Подключено в Mod.cs: UpdateAt<EdgeMarkingPatchSystem>(SystemUpdatePhase.PrefabUpdate).
RoadPrefabDumpSystem оставлен (для верификации до/после).
ТЕСТ: запустить CS2 + загрузить карту → смотреть глазами на городские 3м дороги (краевая линия у бордюра?),
  потом проверить Logs/TownRoadLane.Mod.log на "patched 'EU Highway Edge Line': added N ...".
Если краевая линия не появилась — возможные причины:
  - UpdatePrefab в фазе PrefabUpdate отрабатывает поздно → попробовать раньше (Modification* / при OnGamePreload).
  - SecondaryLaneSystem не пересоздаёт уже размещённые дороги → может нужно "тронуть"/перестроить дорогу в игре,
    либо инициировать пересборку lane'ов.
  - canFlip / сторона: edge line у шоссе в m_LeftLanes; для городской полосы "к бордюру" может быть другая сторона
    (но canFlip=True у 'EU/NA Highway Edge Line' — должно само зеркалиться). Если линия вылезла не с той стороны /
    по центру — поиграть с LEFT vs RIGHT, либо с m_PositionOffset / m_CanFlipSides.
- Написана `Diagnostics/RoadPrefabDumpSystem.cs` (one-shot, PrefabUpdate phase):
  дампит для каждого RoadPrefab — секции/куски/lane'ы (имя prefab'а, позиция, тип, ширина),
  какие lane'ы = NetLaneGeometryPrefab (=разметка) с именами мешей, у каких есть AuxiliaryLanes
  (с m_Position/m_Spacing/reqAll/Any/None). Плюс полный список всех NetLaneGeometryPrefab'ов.
- ЖДЁМ: пользователь запускает CS2 (хотя бы до главного меню) → читаем Logs/TownRoadLane.log.
- Цель чтения лога: узнать (1) какой именно marking-lane-prefab и с каким m_Position/reqAll даёт
  краевую линию у шоссе; (2) какой crefab используется как крайняя проезжая полоса у городских дорог;
  (3) подходящую геометрию/требования для нашего AuxiliaryLaneInfo.

## План работ
1. [done] Разведка архитектуры (subagent).
2. [done] Каркас мод-проекта из официального шаблона.
3. [in progress] Диагностика: дамп структуры дорожных префабов из рантайма.
4. [todo] Прототип: добавить краевой aux-lane (или piece-lane) городским дорожным prefab'ам, как у шоссе.
5. [todo] Тест в игре: парковки/тротуары/остановки/перекрёстки — нет ли регрессий.
6. [todo] Упаковка мода (PublishConfiguration, Thunderstore/PDX).
