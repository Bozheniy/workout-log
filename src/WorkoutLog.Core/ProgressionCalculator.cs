using System.Text.RegularExpressions;

namespace WorkoutLog.Core;

// Двойная прогрессия из плана тренера: сначала повторы, затем один шаг веса.
// Это ориентир следующей тренировки, а не предсказание силы или запись факта.
public static partial class ProgressionCalculator
{
    public static ProgressionAdvice Calculate(
        ProgressionContext context, PreviousExercise? previous, ExerciseProgressionSettings settings)
    {
        var range = ParseRange(context.Repetitions);
        if (range is null || context.SetCount is < 1 or > 30)
        {
            return NoCalculation(ProgressionAction.NeedData, "Нужен диапазон повторений",
                "Для расчёта нужен диапазон вроде 8–12 или одно число. Выполняй схему тренера.");
        }
        if (settings.WeightStep is <= 0 or > 100
            || decimal.Round(settings.WeightStep, 2) != settings.WeightStep
            || settings.FirstSetReserve is < 0 or > 20 || settings.LastSetReserve is < 0 or > 20)
        {
            return NoCalculation(ProgressionAction.NeedData, "Проверь параметры",
                "Шаг веса должен быть больше нуля, запас — от 0 до 20.");
        }
        var (minimum, maximum) = range.Value;
        if (previous is null)
        {
            return new ProgressionAdvice
            {
                Action = ProgressionAction.FirstWorkout,
                Title = "Первая точка отсчёта",
                Explanation = "Прошлого результата пока нет. Подбери вес для нижней границы диапазона с указанным запасом и чистой техникой.",
                Goals = Enumerable.Range(0, context.SetCount).Select(index =>
                    new SetGoal(index + 1, null, minimum, settings.ReserveFor(index, context.SetCount))).ToList()
            };
        }
        if (!previous.Parsed || !previous.Complete)
        {
            return NoCalculation(ProgressionAction.NeedData, "Недостаточно данных",
                "Прошлый результат неполный или записан в свободной форме. Автоматически повышать нагрузку по нему нельзя.");
        }
        if (previous.PlannedSets != context.SetCount || ParseRange(previous.Repetitions) != range)
        {
            return NoCalculation(ProgressionAction.PlanChanged, "В плане новая схема",
                "Изменились число подходов или диапазон повторений. Используй прошлые значения как справку и подбери нагрузку под новый план.");
        }
        if (previous.Sets.Count != context.SetCount
            || previous.Sets.Any(set => set.Reserve is null))
        {
            return NoCalculation(ProgressionAction.NeedData, "Нужен прошлый запас",
                "Вес и повторы известны, но без запаса нельзя проверить готовность к прибавке. Записывай запас каждого подхода.");
        }

        var allAtTop = previous.Sets.Select((set, index) =>
            set.Repetitions >= maximum && set.Reserve >= settings.ReserveFor(index, context.SetCount)).All(value => value);
        var techniqueFailed = previous.TechniqueMaintained == false;
        var canAddWeight = allAtTop && !techniqueFailed && previous.Sets.All(set => set.Weight > 0);
        var goals = new List<SetGoal>();
        var reduced = false;
        var increasedRepetitions = false;
        var unavailableStep = false;

        foreach (var (set, index) in previous.Sets.Select((value, index) => (value, index)))
        {
            var targetReserve = settings.ReserveFor(index, context.SetCount);
            var weight = set.Weight;
            // Оцениваем, сколько повторов останется, если сохранить заданный запас.
            // Прибавку +1 всё равно выполняют только при сохранении техники и запаса.
            var repetitionsAtTargetReserve = set.Repetitions + set.Reserve!.Value - targetReserve;
            int repetitions;
            if (techniqueFailed || repetitionsAtTargetReserve < minimum)
            {
                if (weight <= settings.WeightStep)
                {
                    unavailableStep = true;
                }
                else
                {
                    weight -= settings.WeightStep;
                }
                repetitions = minimum;
                reduced = true;
            }
            else if (canAddWeight)
            {
                weight += settings.WeightStep;
                repetitions = minimum;
            }
            else if (set.Reserve < targetReserve)
            {
                repetitions = Math.Clamp(repetitionsAtTargetReserve, minimum, maximum);
                reduced = true;
            }
            else
            {
                repetitions = Math.Clamp(set.Repetitions + 1, minimum, maximum);
                increasedRepetitions |= repetitions > set.Repetitions;
            }
            if (weight > 2000)
            {
                unavailableStep = true;
            }
            goals.Add(new SetGoal(set.Number, weight, repetitions, targetReserve));
        }

        if (unavailableStep)
        {
            return NoCalculation(ProgressionAction.NeedData, "Нужна другая ступень нагрузки",
                "Заданный шаг не подходит к этому весу. Для собственного веса или тренажёра с противовесом уточни способ облегчения у тренера; для отягощения уменьши шаг.");
        }
        return new ProgressionAdvice
        {
            Action = reduced ? ProgressionAction.Adjust
                : canAddWeight ? ProgressionAction.AddWeight
                : increasedRepetitions ? ProgressionAction.AddRepetitions : ProgressionAction.Hold,
            Title = techniqueFailed ? "Сначала восстанови технику"
                : reduced ? "Скорректируй нагрузку"
                : canAddWeight ? "Можно добавить вес"
                : increasedRepetitions ? "Сначала добавь повторения" : "Закрепи результат",
            Explanation = techniqueFailed
                ? "В прошлый раз техника нарушалась. Уменьши вес на выбранный шаг и начни с нижней границы; усложняй только после восстановления техники."
                : reduced
                    ? "Прошлый результат не укладывается в диапазон с нужным запасом. Где возможно, уменьшаем повторы; если их станет меньше минимума — снижаем вес на один шаг."
                    : canAddWeight
                        ? "Верхняя граница достигнута во всех рабочих подходах с нужным запасом. Добавь один выбранный шаг веса и вернись к нижней границе повторений."
                        : increasedRepetitions
                            ? "Сохрани вес. Попробуй добавить по одному повторению там, где ещё не достигнут верх диапазона, сохраняя заданный запас. Вес повышаем после всех подходов."
                            : "Сохрани вес и верхнюю границу повторений. Для упражнений с собственным весом способ усложнения выбирай с тренером.",
            TechniqueUnknown = previous.TechniqueMaintained is null,
            Goals = goals
        };
    }

    private static ProgressionAdvice NoCalculation(ProgressionAction action, string title, string explanation)
    {
        return new ProgressionAdvice { Action = action, Title = title, Explanation = explanation };
    }

    public static (int Minimum, int Maximum)? ParseRange(string value)
    {
        var match = RangePattern().Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var minimum))
        {
            return null;
        }
        var maximum = minimum;
        if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out maximum))
        {
            return null;
        }
        return minimum >= 1 && maximum >= minimum && maximum <= 500 ? (minimum, maximum) : null;
    }

    [GeneratedRegex(@"^\s*(\d+)\s*(?:[-–—]\s*(\d+))?\s*$")]
    private static partial Regex RangePattern();
}
