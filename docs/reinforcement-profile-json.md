# Эпюра армирования для Revit — формат JSON

Файл `*.reinforcement.json` пишет модуль «Эпюры (PLAXIS)» приложения «Сечения БНС и СВГ».
Формат `section-app/reinforcement-profile`, версия 1. Кодировка UTF-8, числа — с точкой.

## Единицы и отсчёт

- Отметки `*_level_m` — вертикальная координата модели PLAXIS, м (вверх — плюс).
- `depth_m` — расстояние от верха элемента, м.
- Площади продольной арматуры — см² на элемент: для стены — на ширину сечения
  `element.wall.width_mm` (обычно 1000 мм, то есть на 1 м стены), для сваи — на одну сваю.
- `asw_cm2_per_m` — требуемая площадь поперечной арматуры на метр длины элемента, см²/м
  (Asw/sw для принятого числа ветвей; для сваи — две ветви витка спирали).
- Усилия огибающей — кН и кН·м на тот же элемент.

## Структура

```json
{
  "format": "section-app/reinforcement-profile",
  "version": 1,
  "created": "2026-10-20T10:00:00",
  "title": "СВГ-1",
  "element": {
    "type": "wall",                     // "wall" — стена в грунте, "pile" — свая
    "top_level_m": 0.0, "bottom_level_m": -15.0, "length_m": 15.0, "x_m": 5.0,
    "wall": { "width_mm": 1000, "thickness_mm": 600, "cover_mm": 50, "stirrup_diameter_mm": 10,
              "right_face_bars": 5, "left_face_bars": 5 }
    // для сваи: "pile": { "diameter_mm", "cover_mm", "spiral_diameter_mm", "bar_count", "spacing_m" }
  },
  "materials": { "concrete": "B30", "longitudinal": "A500", "transverse": "A240" },
  "faces": { "right": "грань со стороны большего X модели PLAXIS", "left": "…меньшего X…" },  // null для сваи
  "source": {
    "program": "PLAXIS 2D", "version": "22.01", "project": "Котлован",
    "forces": "envelope",               // envelope — огибающие Output по веткам; phases — по каждой фазе
    "objects": ["Plate_1"],
    "phases": [ { "name": "Phase_3", "title": "Дно", "kind": "main", "short_term": false,
                  "covers": ["InitialPhase", "Phase_1", "Phase_2", "Phase_3"] } ]   // kind: main | seismic | special; covers — фазы ветки огибающей
  },
  "design": {
    "code": "СП 63.13330.2018", "forces": "design",
    "force_width_m": 1.0,               // ширина стены или шаг свай, на который умножены погонные усилия PLAXIS
    "moment_sign": 1,                   // 1: M > 0 PLAXIS растягивает правую грань; -1: левую
    "station_step_m": 0.5, "min_percent": 0.1, "as_min_cm2": 5.3,
    "service_divisor": 1.0,             // нормативные = расчётные / service_divisor
    "sustained_share": 1.0,             // kдл
    "crack_limits_mm": { "long": 0.3, "short": 0.4 }, "crack_method": "conservative",
    "shear_check": true, "shear_pitch_mm": 200
  },
  "units": { "level": "m", "as": "cm2", "asw": "cm2/m", "force": "kN", "moment": "kN*m" },
  "stations": [
    {
      "level_m": -6.0, "depth_m": 6.0, "from_level_m": -5.25, "to_level_m": -6.75,
      "as_right_cm2": 22.61, "as_left_cm2": 5.30,      // для сваи вместо них "as_cm2"
      "asw_cm2_per_m": 3.93,                          // null — Q не проверялась или не обеспечивается
      "status": "ok",
      "governing": { "check": "Ширина раскрытия трещин, продолжительное", "combination": "Phase_3: Mmax", "k": 0.997 },
      "note": null
    }
  ],
  "envelope": [
    { "level_m": -6.0, "n_min_kn": -210.0, "n_max_kn": -120.0, "m_min_knm": 0.0, "m_max_knm": 280.0, "q_abs_max_kn": 15.0 }
  ]
}
```

## Сечения (`stations`)

Сечения идут сверху вниз с шагом `design.station_step_m`. Каждое представляет интервал
`from_level_m … to_level_m`: в его РСУ попадают все узловые значения PLAXIS внутри интервала,
поэтому пики между сечениями не теряются. Требуемая площадь сечения относится ко всему интервалу,
и эпюру можно строить ступенями по интервалам.

`status`:

| Значение | Смысл |
|---|---|
| `ok` | найдена наименьшая площадь, при которой выполнены все проверки (K ≤ 1) |
| `minimum` | определяет конструктивный минимум `as_min_cm2` |
| `exceeded` | не обеспечивается даже наибольшим диаметром стержней принятой раскладки |
| `indeterminate` | расчёт ядра не сошёлся — площадь не определена |
| `none` | в интервале нет усилий |

Для `exceeded` и `indeterminate` площади — последние опробованные значения, их нельзя
использовать как требуемые.

## Что учтено в требуемой площади

Прочность нормального сечения по деформационной модели с учётом прогиба (η) и случайного
эксцентриситета, продольный изгиб, ширина раскрытия трещин (или их отсутствие) по СП 63. Стена
считается полосой плоской задачи: изгиб только в плоскости стены. Раскладка стержней
(число по граням, защитный слой) берётся из сечения, диаметр меняется непрерывно.

`envelope` — огибающая всех включённых фаз в узлах PLAXIS. Знак M — как в PLAXIS,
связь с гранями задаёт `design.moment_sign`.
