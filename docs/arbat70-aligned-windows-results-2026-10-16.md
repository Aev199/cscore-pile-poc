# АРБАТ × CScore: результат выравнивания геометрии, Windows CI №38866856236

Дата проверки: 16.10.2026. Источник: https://github.com/Aev199/cscore-pile-poc/actions/runs/38866856236, SHA 34a9c349ba3d6e4f18706e2438cc2c754935bd52.

## Покрытие и воспроизводимость

Windows workflow полностью прошёл, все 486 целевых тестов CScore. Исходная и отдельно исправленная по тексту АРБАТ геометрии матрицы рассчитаны по 70 загружениям каждая. В ОБЕИХ матрицах 59/70 численных результатов, 11/70 indeterminate — геометрическое выравнивание не решает проблемы сходимости.

В исходном независимом RTF АРБАТ ширина трещин (коэффициенты) напечатана в 27/70 случаях. Из них 25 имеют численный результат CScore, но два исключаются по несовместимой длительной продольной силе T10, а 14 — по остающейся неопределённости геометрии/осей прямоугольников. По 9 предварительно сопоставимым случаям MAPE длительной ширины **4.929%**, кратковременной **9.531%**. Отсутствие ширины в RTF нельзя трактовать как zero/pass.

## Выравнивание прямоугольных сечений: измеренное влияние на длительное раскрытие

Данные ниже рассчитаны построчно из исходного `sls_matrix/cross_section_sls_matrix.csv`, нового `sls_matrix_aligned/cross_section_sls_matrix.csv` и независимого `arbat70_aligned_comparison.csv` одного и того же CI. T10 в MAPE не входит; T09 включён, если CScore численно завершил расчёт.

| Сечение | Печатных сравнимых пар* | MAPE до, % | MAPE после, % | Кратковременная MAPE до → после, % |
|---|---:|---:|---:|---:|
| 300×500, 4Ø20 (без изменений) | 2 | 2.81 | 2.81 | 9.27 → 9.27 |
| 400×600, 6Ø20 | 7 | **27.56** | **5.46** | 30.29 → 9.59 |
| 500×700, 8Ø25 | 5 | **16.97** | **4.32** | 26.02 → 16.51 |
| 400×400, 8Ø16, два ряда | 2 | **8.09** | **2.46** | 14.26 → 8.63 |
| Ø600, 8Ø20 (без изменений) | 7 | 5.54 | 5.54 | 9.61 → 9.61 |

(*) Только пары с коэффициентом ширины АРБАТ, конечным расчётом CScore и без T10. **Важно:** прямоугольные пары — исследовательские, а не геометрически окончательно подтверждённые: текст RTF S1/S2 не задаёт все координаты и локальную ориентацию однозначно. Поэтому эти MAPE НЕ должны использоваться как нормативное доказательство верификации.

Для 400×600 без T09, то есть только при полностью длительных действиях, MAPE **23.88% → 3.01%**. Самый крупный оставшийся длительный разброс в этом сечении — **T09 (20.12%)**, связанный с разделением длительной/полной нагрузки.

Примеры: 400×600 T02 ARBAT 1.690, CScore до 2.188, после 1.739; T06 ARBAT 0.510, до 0.710, после 0.523. 500×700 T02 ARBAT 0.664, до 0.822, после 0.681. Таким образом на этих случаях главная причина прежней большой разницы — именно дискретная схема арматуры, а не общая формула раскрытия.

## Оставшиеся инженерно значимые пограничные случаи

- 300×500 T01: ARBAT Klong=0.987, CScore 1.03163; отличие приводит к разным формальным заключениям, но оба случая требуют инженерного рассмотрения. Предварительная полоса ±3% — продуктовая эвристика и не нормативная погрешность.
- 300×500 T06: ARBAT Kshort=0.964, CScore предварительный 1.01424. Аналогично небольшой консервативный разрыв около предела.
- 400×600 T09: ARBAT Kshort=0.943, CScore предварительный 1.04597; остаётся консервативная разница по частично длительной нагрузке, при этом геометрия прямоугольника по-прежнему гипотеза.
- В новом наборе выявленных **неконсервативных ложных допусков** CScore относительно напечатанной ширины АРБАТ не обнаружено; известные расхождения идут в сторону запрета. Это не доказательство отсутствия таких случаев за пределами исследования.
- 500×700 T09/T10 остаются indeterminate. Другие 9 сомнительных состояний не изменились; нужно отдельно искать upstream fallback/несходимость.

## Выявленный дефект нашей предварительной методики: a_short != a_long при одинаковых нагрузках

В исходном CScore на всех 48 численно рассчитанных полностью длительных случаях T01–T08 выполнено **a_crc,short = a_crc,long** с максимальной разницей порядка 1e-16 мм. Это следует из СП 63 формулы a_short=a1+a2−a3 и тождества a2=a3 при равенстве полной и длительной частей и одинаковой диаграмме.

В ARBAT для полностью длительных сочетаний также a_short≈a_long (с учётом округления коэффициентов). Но наш основной режим пересчитывал long по pre-crack гипотезе, а short — по независимой грубой psi_s=1 оценке, нарушая это тождество и завышая short. Поэтому большой MAPE кратковременного раскрытия в режиме fully sustained **в значительной степени создавал сам адаптер**.

ПОСЛЕ успешной сборки 38866856236 внесено отдельное исправление: при идентичном N/M в полной и длительной частях и подтверждённом upstream тождестве ширин основная методика устанавливает prelim a_short=prelim a_long. Если upstream тождество нарушено — расчёт остаётся indeterminate (fail-closed). При частично длительных усилиях прежняя консервативная проверка psi_s=1 остаётся отдельно, без попытки подгонки. Исходный native-refined CScore никак не менялся.

Добавлен тест `check_fully_sustained_width_identity`, автоматически запускаемый на реальном численном выводе матрицы, а также регрессионный unit test. **Этот последний фикс не проверен Windows CI**; требуется одна новая сборка перед численными утверждениями о кратковременном режиме.

## Следующие шаги

1. Прогнать CI после исправления тождества long/short и проверить сохранение 59/70 рассчитанных результатов, статус каждого indeterminate и новых MAPE.
2. Разобраться с первопричиной 11 неопределённых (Cracked=false при моменте выше Mcrc либо silent fallback), не отключая защитные проверки.
3. Проверить визуально координаты и оси армирования трех прямоугольников в АРБАТ. Не выдавать результаты 14 геометрически неподтверждённых пар за эталон.
4. После этого отдельно исследовать T09/T10 и различие полного/длительного N, не вводя произвольных множителей для подгонки АРБАТ.

Справка: `docs/arbat-70-crackwidth-second-run-2026-10-16.md`. Основной предварительный метод `arbat_conservative`, авторский уточнённый `native_refined` сохраняется как необязательный. Ни один из результатов не является окончательным допуском для КЖ.


## Follow-up Windows validation — 38868762683

Full Windows Actions run: https://github.com/Aev199/cscore-pile-poc/actions/runs/38868762683, commit `e014799fdb82b680a67d2e75221878d3edf61726`, **passed**. 66 Python contract tests, 486 pinned OpenCS tests. The new `check_fully_sustained_width_identity` succeeded on **48/48 computed fully sustained** test records in each of two research matrices. It is an explicit numerical confirmation that `a_crc,short = a_crc,long` under the stated identical full/sustained moments.

Automatic strict ARBAT comparison still includes 9 previously eligible cases. Main conservative method MAPE:
- Long crack width utilization **4.92936158%** (unchanged).
- Short crack width utilization **3.98596654%** (down from **9.53059791%**).
- Original author's native `CrackWidthSolver` was **not modified**.

There are still exactly **59/70 research-calculated** and **11/70 indeterminate** results in each matrix. The eleven are not a single failure class:

1. **Eight post-threshold zero-result cases:** R300×500 T02, T03, T04, T05, T07 and R400×400 T02, T03, T07. All eight have `M_long > Mcrc` and the upstream result `CrcConverged=true, Cracked=false`. The same eight combinations exceed the **ARBAT ULS normal-section checks**; they are physically poor candidates, but this does NOT identify exactly which hidden upstream numerical fallback returned zero. Do not conflate a valid failure of strength with successful crack solver convergence.
2. **Three long/total load threshold splits:** R500×700 T09, T10 and Ø800 T09. Each has `M_long < Mcrc < M_total`; upstream source `CrackWidthSolver.Compute()` exits at `if (mLong <= mcrc || !crcRes.StrainPlane.HasValue) return zero;` **before total load is independently solved**. Numerical examples from aligned matrix:
   - R500×700 T09: `M_long=80`, `M_total=160`, `Mcrc≈121.535` kN·m.
   - R500×700 T10: `M_long≈80.623`, `M_total≈161.245`, `Mcrc≈96.203` kN·m.
   - Ø800 T09: `M_long=80`, `M_total=160`, `Mcrc≈155.557` kN·m.

**Crucial normative caveat:** independent ARBAT R500×700 T09 reports `Klong=0.284`, `Kshort=0.371` despite `M_long < Mcrc(CScore)`; T10 reports `Klong=0.296`, `Kshort=0.398`, but its sustained N is not comparable in the current CScore API. Therefore **do not force long-crack width to zero or treat Mcrc(CScore) as independently validated**. Investigate the concrete tensile law, cracking threshold and long/total load split before any recovery of these cases. Ø800 T09 has no printed ARBAT width and does not independently validate zero.

Recommended implementation order: (a) diagnose long/total threshold mismatches explicitly in adapter statuses; (b) separately verify `CrackingSolver` threshold and material law against independently obtained ARBAT crack initiation evidence; (c) use distinct sustained/total N in a dedicated research adaptation if required; (d) ensure upstream internal convergence is explicitly observable, with all failures still fail-closed. Do not rewrite or weaken the author's refined crack algorithm.

The numeric comparison CSV and raw JSONs are in the Windows artifact; last-run diagnostic classification was also exported as `cscore_38868762_11_indeterminate.csv` for review.
