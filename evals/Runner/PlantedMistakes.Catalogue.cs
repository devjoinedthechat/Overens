using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>The mistakes that are planted, and the small edits to a model's answer that plant them.</summary>
public static partial class PlantedMistakes
{
    public static IReadOnlyList<Mistake> All { get; } =
    [
        // A figure the agreement prints nowhere.
        new("rate_invented", NotPrinted, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 0 } bands && Add(bands[^1], "hourly_rate", 0.37m)),
        new("increase_invented", NotPrinted, "minimum_wage", false, (rules, at) => Add(rules[at], "increase", 0.37m)),
        new("sum_made_by_the_model", NotPrinted, "minimum_wage", true, (rules, at) =>
        {
            // The model reports the rate after the increase, having added it up itself.
            if (Number(rules[at], "increase") is not { } increase || at == 0 || Items(rules[at - 1], "bands") is not { Count: > 0 } before)
            {
                return false;
            }

            var summed = new JsonArray();
            foreach (var band in before)
            {
                summed.Add(new JsonObject { ["from_months"] = band!["from_months"]!.GetValue<int>(), ["hourly_rate"] = Number(band, "hourly_rate")!.Value + increase });
            }

            rules[at]!["increase"] = null;
            rules[at]!["bands"] = summed;
            return true;
        }),
        new("seniority_invented", NotPrinted, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 1 } bands && Add(bands[^1], "from_months", 7)),
        new("daily_norm_invented", NotPrinted, "overtime", false, (rules, at) => Add(rules[at], "daily_norm_minutes", 7)),
        new("overtime_percent_invented", NotPrinted, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "percent", 0.37m)),
        new("tier_length_invented", NotPrinted, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "minutes", 37)),
        new("supplement_invented", NotPrinted, "inconvenient_hours", false, (rules, at) => Add(Items(rules[at], "windows")![0], "amount_per_hour", 0.37m)),
        new("window_time_invented", NotPrinted, "inconvenient_hours", false, (rules, at) => Later(Items(rules[at], "windows")![0]!, minutes: 17)),
        new("pension_percent_invented", NotPrinted, "pension", false, (rules, at) => Add(rules[at], "employer_percent", 0.37m)),
        new("qualifying_period_invented", NotPrinted, "pension", false, (rules, at) =>
            Number(rules[at], "qualifying_months") > 0 && Add(rules[at], "qualifying_months", 7)),
        new("share_worked_out_by_the_model", NotPrinted, "pension", true, (rules, at) =>
        {
            // The model reports the percentage it made of "half", not the share as printed.
            if (rules[at]!["employee_share"]?.GetValue<string>() is not { } share)
            {
                return false;
            }

            rules[at]!["employee_percent"] = Shares.Of(share, Number(rules[at], "employer_percent")!.Value);
            rules[at]!["employee_share"] = null;
            return true;
        }),
        new("another_share", NotPrinted, "pension", false, (rules, at) =>
        {
            if (rules[at]!["employee_share"]?.GetValue<string>() is not { } share)
            {
                return false;
            }

            rules[at]!["employee_share"] = share == "half" ? "third" : "half";
            return true;
        }),
        new("percent_invented", NotPrinted, "holiday_allowance", false, (rules, at) => Add(rules[at], "percent", 0.37m)),
        new("percent_invented", NotPrinted, "special_savings", false, (rules, at) => Add(rules[at], "percent", 0.37m)),

        // A figure one step from the right one.
        new("rate_a_krone_off", NearMiss, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 0 } bands && Add(bands[^1], "hourly_rate", 1)),
        new("increase_a_krone_off", NearMiss, "minimum_wage", false, (rules, at) => Add(rules[at], "increase", 1)),
        new("seniority_a_year_off", NearMiss, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 1 } bands && Add(bands[^1], "from_months", 12)),
        new("daily_norm_half_an_hour_off", NearMiss, "overtime", false, (rules, at) => Add(rules[at], "daily_norm_minutes", 30)),
        new("overtime_percent_five_points_off", NearMiss, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "percent", 5)),
        new("tier_an_hour_longer", NearMiss, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "minutes", 60)),
        new("supplement_a_krone_off", NearMiss, "inconvenient_hours", false, (rules, at) => Add(Items(rules[at], "windows")![0], "amount_per_hour", 1)),
        new("window_starts_an_hour_late", NearMiss, "inconvenient_hours", false, (rules, at) => Later(Items(rules[at], "windows")![0]!, minutes: 60)),
        new("pension_percent_half_a_point_off", NearMiss, "pension", false, (rules, at) => Add(rules[at], "employer_percent", 0.5m)),
        new("qualifying_period_three_months_longer", NearMiss, "pension", false, (rules, at) =>
            Number(rules[at], "qualifying_months") > 0 && Add(rules[at], "qualifying_months", 3)),
        new("percent_half_a_point_off", NearMiss, "holiday_allowance", false, (rules, at) => Add(rules[at], "percent", 0.5m)),
        new("percent_half_a_point_off", NearMiss, "special_savings", false, (rules, at) => Add(rules[at], "percent", 0.5m)),

        // The agreement's own figure, in the wrong place.
        new("rates_in_each_others_bands", Misplaced, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 1 } bands && Swap(bands[0], "hourly_rate", bands[^1], "hourly_rate")),
        new("another_years_rates", Misplaced, "minimum_wage", false, (rules, at) => FromAnotherRule(rules, at, "bands")),
        new("overtime_percents_swapped", Misplaced, "overtime", false, (rules, at) =>
            Items(rules[at], "tiers") is { Count: > 1 } tiers && Swap(tiers[0], "percent", tiers[^1], "percent")),
        new("sunday_given_the_weekday_percent", Misplaced, "overtime", false, (rules, at) =>
        {
            var weekday = Number(Items(rules[at], "tiers")![0], "percent");
            if (weekday == Number(rules[at], "sunday_percent"))
            {
                return false;
            }

            rules[at]!["sunday_percent"] = weekday;
            return true;
        }),
        new("supplements_swapped", Misplaced, "inconvenient_hours", false, (rules, at) =>
            Items(rules[at], "windows") is { Count: > 1 } windows && Swap(windows[0], "amount_per_hour", windows[1], "amount_per_hour")),
        new("window_back_to_front", Misplaced, "inconvenient_hours", false, (rules, at) =>
        {
            var window = Items(rules[at], "windows")![0];
            return Swap(window, "from", window, "to");
        }),
        new("another_years_supplements", Misplaced, "inconvenient_hours", false, (rules, at) => FromAnotherRule(rules, at, "windows")),
        new("shares_swapped", Misplaced, "pension", false, (rules, at) =>
            Number(rules[at], "employee_percent") is not null && Swap(rules[at], "employer_percent", rules[at], "employee_percent")),
        new("total_as_the_employers_share", Misplaced, "pension", false, (rules, at) =>
            Number(rules[at], "employee_percent") is { } employee && Add(rules[at], "employer_percent", employee)),
        new("another_periods_percent", Misplaced, "pension", false, (rules, at) => FromAnotherRule(rules, at, "employer_percent")),
        new("another_periods_percent", Misplaced, "holiday_allowance", false, (rules, at) => FromAnotherRule(rules, at, "percent")),
        new("another_periods_percent", Misplaced, "special_savings", false, (rules, at) => FromAnotherRule(rules, at, "percent")),

        // Something left out.
        new("top_band_left_out", LeftOut, "minimum_wage", false, (rules, at) => RemoveLast(Items(rules[at], "bands"), atLeast: 2)),
        new("first_tier_left_out", LeftOut, "overtime", false, (rules, at) =>
        {
            if (Items(rules[at], "tiers") is not { Count: > 1 } tiers)
            {
                return false;
            }

            tiers.RemoveAt(0);
            return true;
        }),
        new("window_left_out", LeftOut, "inconvenient_hours", false, (rules, at) => RemoveLast(Items(rules[at], "windows"), atLeast: 2)),
        new("weekday_left_out", LeftOut, "inconvenient_hours", false, (rules, at) =>
            RemoveLast(Items(rules[at], "windows")!.Select(window => Items(window, "days")).FirstOrDefault(days => days is { Count: > 1 }), atLeast: 2)),
        new("no_qualifying_period", LeftOut, "pension", false, (rules, at) =>
        {
            if (Number(rules[at], "qualifying_months") is not > 0)
            {
                return false;
            }

            rules[at]!["qualifying_months"] = 0;
            return true;
        }),
        new("rule_left_out", LeftOut, null, false, (rules, at) =>
        {
            rules.RemoveAt(at);
            return true;
        }),

        // The wrong days.
        new("starts_a_month_late", WrongDays, null, false, (rules, at) =>
        {
            rules[at]!["from"] = Day(rules[at], "from")!.Value.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }),
        new("starts_when_another_period_starts", WrongDays, null, false, (rules, at) =>
        {
            // A date the agreement prints, for the wrong rule: the later period's first day, or the earlier one's.
            var other = at + 1 < rules.Count ? at + 1 : at - 1;
            if (other < 0 || Day(rules[at], "until") is { } until && Day(rules[other], "from") >= until)
            {
                return false;
            }

            rules[at]!["from"] = rules[other]!["from"]!.GetValue<string>();
            return true;
        }),
        new("ends_a_month_early", WrongDays, null, false, (rules, at) =>
        {
            if (Day(rules[at], "until") is not { } until)
            {
                return false;
            }

            rules[at]!["until"] = until.AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }),
        new("never_ends", WrongDays, null, false, (rules, at) =>
        {
            if (Day(rules[at], "until") is null)
            {
                return false;
            }

            rules[at]!["until"] = null;
            return true;
        }),

        // The evidence.
        new("word_added_to_the_quote", Evidence, null, true, (rules, at) =>
        {
            var quote = rules[at]!["quote"]!.GetValue<string>();
            var space = quote.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                return false;
            }

            rules[at]!["quote"] = quote.Insert(space, " altid");
            return true;
        }),
        new("clause_that_does_not_exist", Evidence, null, true, (rules, at) =>
        {
            rules[at]!["clause_id"] = "§ 99, stk. 9";
            return true;
        }),
        new("quote_from_another_clause", Evidence, null, true, (rules, at) =>
        {
            // The quote is real, and filed under a clause that exists and does not hold it.
            var named = rules[at]!["clause_id"]!.GetValue<string>();
            var other = Items(rules[at], "also_from")!.Select(clause => clause!.GetValue<string>())
                .Concat(rules.Select(rule => rule!["clause_id"]!.GetValue<string>()))
                .FirstOrDefault(clause => clause != named);
            if (other is null)
            {
                return false;
            }

            rules[at]!["clause_id"] = other;
            return true;
        }),
    ];

    private static JsonArray? Items(JsonNode? node, string name) => node?[name] as JsonArray;

    private static decimal? Number(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number ? value.GetValue<decimal>() : null;

    private static DateOnly? Day(JsonNode? node, string name) =>
        node?[name]?.GetValue<string>() is { } text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static TimeOnly Clock(JsonNode node, string name) =>
        TimeOnly.ParseExact(node[name]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Moves the time a window starts.</summary>
    private static bool Later(JsonNode window, int minutes)
    {
        window["from"] = Clock(window, "from").AddMinutes(minutes).ToString("HH\\:mm", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Adds to a number. False when there is no number there.</summary>
    private static bool Add(JsonNode? node, string name, decimal amount)
    {
        if (Number(node, name) is not { } value)
        {
            return false;
        }

        node![name] = value + amount;
        return true;
    }

    /// <summary>Exchanges two values. False when they are the same, since nothing would change.</summary>
    private static bool Swap(JsonNode? first, string firstName, JsonNode? second, string secondName)
    {
        var (one, other) = (first![firstName]!.DeepClone(), second![secondName]!.DeepClone());
        if (JsonNode.DeepEquals(one, other))
        {
            return false;
        }

        first[firstName] = other;
        second[secondName] = one;
        return true;
    }

    /// <summary>Takes a value from the nearest other rule in the answer that states it differently.</summary>
    private static bool FromAnotherRule(JsonArray rules, int at, string name)
    {
        var mine = rules[at]![name];
        var other = Enumerable.Range(0, rules.Count)
            .Where(index => index != at)
            .OrderBy(index => Math.Abs(index - at))
            .Select(index => rules[index]![name])
            .FirstOrDefault(value => value is not null and not JsonArray { Count: 0 } && !JsonNode.DeepEquals(value, mine));
        if (other is null || mine is null or JsonArray { Count: 0 })
        {
            return false;
        }

        rules[at]![name] = other.DeepClone();
        return true;
    }

    private static bool RemoveLast(JsonArray? items, int atLeast)
    {
        if (items is null || items.Count < atLeast)
        {
            return false;
        }

        items.RemoveAt(items.Count - 1);
        return true;
    }
}
