using System.Collections.Generic;
using Colossal;

namespace TownRoadLane
{
    public class LocaleRU : IDictionarySource
    {
        private readonly TownRoadLaneSetting _setting;
        public LocaleRU(TownRoadLaneSetting setting) { _setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { _setting.GetSettingsLocaleID(), "Town Road Lane" },
                { _setting.GetOptionTabLocaleID(TownRoadLaneSetting.kSection), "Основное" },

                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kEdgeGroup), "Краевая линия у бордюра" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kParkingGroup), "Разметка параллельной парковки" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentGroup), "Редактор разметки — разрезание на сегменты" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentDevGroup), "Разрезание сегментов — тонкая настройка" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kKeybindGroup), "Горячие клавиши" },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)), "Краевая линия на городских дорогах" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)),
                    "Добавляет краевую линию у бордюра обычным городским дорогам (полосы 3 м) — так же, как на шоссе. Изменения вступают в силу после перезапуска игры." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)), "Стиль краевой линии" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)),
                    "Стиль меша только автоматической краевой линии — линии, нарисованные инструментом разметки, сохраняют собственные стили. Варианты «G87» требуют мод [G87] Road Markings; без него используется ванильный стиль. Изменения вступают в силу после перезапуска игры." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.YellowLeftLineEnabled)), "Жёлтая левая краевая (США)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.YellowLeftLineEnabled)),
                    "В городах с североамериканской темой односторонние и разделённые дороги получают жёлтую линию вдоль левого края проезжей части (со стороны медианы), как принято в США; белая краевая остаётся у бордюра. Города с европейской темой не затрагиваются. Изменения вступают в силу после перезапуска игры." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)), "Размечать зоны параллельной парковки" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)),
                    "Рисует линию вдоль зон параллельной уличной парковки с поперечной чертой на концах квартала. Дороги без сублейна Parking Lane 2 (односторонние трёхполосные, асимметричные варианты) остаются без разметки — то же покрытие, что и в v1.1. Изменения вступают в силу после перезапуска игры." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)), "Стиль линии парковки" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)),
                    "Продольная линия вдоль парковочной зоны. Варианты «G87» требуют мод [G87] Road Markings. Изменения вступают в силу после перезапуска игры." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)), "Стиль концевой черты парковки" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)),
                    "Короткая поперечная черта в начале и конце парковочного квартала. «Нет» отключает черты. Варианты «G87» требуют мод [G87] Road Markings. Изменения вступают в силу после перезапуска игры." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)), "Минимальная длина сегмента (м)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)),
                    "Пересекаясь, нарисованные линии режутся на сегменты (каждый можно скрыть или перекрасить). Сегменты короче этого значения сливаются с соседним. Меньше — более дробные сегменты на плотной разметке; больше — меньше «щепок» от линий, которые лишь слегка задевают друг друга. По умолчанию: 1,0 м. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)), "Мёртвая зона у точек-якорей (м)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)),
                    "Пересечения ближе этого расстояния к концу линии не режут её. Линии, выходящие из одной точки-якоря, первые метр-два идут внахлёст — без мёртвой зоны этот нахлёст порождает фантомные микросегменты. Меньше — резы разрешены ближе к точкам; больше — спокойнее возле якорей. По умолчанию: 2,0 м. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)), "Минимальный угол пересечения (°)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)),
                    "Линии, встречающиеся под углом меньше этого, считаются скользящим касанием, а не пересечением — без реза. При 0° режет любое касание, и почти параллельные линии могут дать пачку микросегментов. По умолчанию: 8°. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)), "Радиус склейки пересечений (м)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)),
                    "Пологое пересечение распознаётся как несколько почти совпадающих точек; точки в этом радиусе склеиваются в один рез. Меньше — больше таких почти-дублей выживает отдельными резами. По умолчанию: 1,5 м. Применяется к перекрёстку при следующей правке его линий, а ко всему — после перезагрузки сохранения." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)), "Активировать инструмент разметки" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)),
                    "Включает/выключает инструмент настройки разметки перекрёстков. То же, что горячая клавиша ниже, но работает всегда (кнопку не может перехватить другой мод)." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)), "Инструмент разметки (клавиша)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)),
                    "Включает или выключает инструмент настройки разметки перекрёстков. По умолчанию Ctrl+M. Если клавиша не срабатывает (перехвачена другим модом), используйте кнопку выше." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)), "Стиль линии по кругу (клавиша)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)),
                    "При активном инструменте листает стили: сплошная → пунктир → … Выбранный стиль применяется к СЛЕДУЮЩЕЙ линии. По умолчанию Y. Цвет точек-якорей отражает текущий стиль." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)), "Режим области (клавиша)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)),
                    "При выбранном узле запускает режим полигональной области: кликайте по опорным точкам, чтобы построить заливку. Повторное нажатие или Esc — отмена. По умолчанию A." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)), "Стиль области по кругу (клавиша)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)),
                    "При активном инструменте листает стиль заливки для СЛЕДУЮЩЕЙ замкнутой области (бетон → вафельная разметка → белая штриховка → жёлтая штриховка → велополоса → автобусная полоса → сначала). По умолчанию U. Стили G87 без установленного мода G87 заменяются бетоном." },

                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid), "Белая сплошная" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolidThick), "Белая сплошная (толстая)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed), "Белый пунктир" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid), "Жёлтая сплошная" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid_G87), "Белая сплошная (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed_G87), "Белый пунктир (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid_G87), "Жёлтая сплошная (G87)" },

                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashedDense), "Белый пунктир (частый)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed), "Белый пунктир" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid), "Белая сплошная" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed), "Жёлтый пунктир" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid), "Жёлтая сплошная" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid_G87), "Белая сплошная (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed_G87), "Белый пунктир (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid_G87), "Жёлтая сплошная (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed_G87), "Жёлтый пунктир (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueSolid_G87), "Синяя сплошная (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueDashed_G87), "Синий пунктир (G87)" },

                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.None), "Нет" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolid), "Белая сплошная" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolidThick), "Белая сплошная (толстая)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteTerminal_G87), "Белая концевая линия (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.YellowTerminal_G87), "Жёлтая концевая линия (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.BlueSolid_G87), "Синяя сплошная (G87)" },
            };
        }

        public void Unload() { }
    }
}
