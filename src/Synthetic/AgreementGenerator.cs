using Overens.Documents;
using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>
/// Writes a collective agreement in Danish from rates it chooses itself, and returns the rules
/// the text states. The rules come first and the text is built from them with fixed phrasings,
/// so the answer is known before anything reads the document.
///
/// The organisations and the agreements' names are invented, and chosen so that they cannot
/// be taken for real ones: Tværhavn and Elmekær are not places. The structure follows real agreements: numbered
/// sections and clauses, rates that step up each 1 March, tables, and protocol amendments.
/// </summary>
public static class AgreementGenerator
{
    private static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static readonly DayOfWeek[] EveryDay =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static readonly (string Title, string Work)[] Trades =
    [
        ("Tværhavn-overenskomsten for lagerarbejde", "lager- og logistikarbejde"),
        ("Tværhavn-overenskomsten for butiksarbejde", "arbejde i butikker og varehuse"),
        ("Tværhavn-overenskomsten for produktionsarbejde", "produktionsarbejde"),
        ("Tværhavn-overenskomsten for servicearbejde", "rengørings- og servicearbejde"),
    ];

    public static SyntheticAgreement Generate(AgreementSpec spec) => Generate(spec, null);

    /// <summary>
    /// The agreement that replaces <paramref name="previous"/> when its term ends: the same
    /// parties and trade, with rates that carry on upwards from where the old ones stopped.
    /// </summary>
    public static SyntheticAgreement Renew(SyntheticAgreement previous, AgreementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var rules = previous.Truth.Rules;
        T Last<T>()
            where T : Rule => rules.OfType<T>().MaxBy(rule => rule.InForce.From)!;

        var wage = Last<MinimumWageRule>();
        var windows = Last<InconvenientHoursRule>().Windows;
        var pension = Last<PensionRule>();
        var saturday = windows.FirstOrDefault(window => window.Days is [DayOfWeek.Saturday]);
        return Generate(spec, new(
            Array.FindIndex(Trades, trade => previous.Document.Title.StartsWith(trade.Title, StringComparison.Ordinal)),
            wage.Bands[0].HourlyRate,
            wage.Bands[1].HourlyRate - wage.Bands[0].HourlyRate,
            wage.Bands.Count > 2 ? wage.Bands[2].HourlyRate - wage.Bands[0].HourlyRate : null,
            (int)Last<OvertimeRule>().Tiers[0].Hours!.Value,
            windows[0].From,
            windows[0].AmountPerHour,
            windows[^1].AmountPerHour - windows[0].AmountPerHour,
            saturday is null ? null : saturday.AmountPerHour - windows[0].AmountPerHour,
            pension.EmployerPercent,
            pension.EmployeePercent,
            pension.QualifyingMonths,
            Last<SpecialSavingsRule>().Percent));
    }

    private static SyntheticAgreement Generate(AgreementSpec spec, Baseline? previous)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var random = new Random(spec.Seed);
        var hard = spec.Difficulty;
        var start = new DateOnly(spec.StartYear, 3, 1);
        DateOnly[] steps = hard == 1 ? [start] : [start, start.AddYears(1), start.AddYears(2)];
        Period StepPeriod(int step) => new(steps[step], step + 1 < steps.Length ? steps[step + 1] : null);
        var whole = new Period(start);

        // The rates, chosen before any text exists.
        var (title, work) = Trades[random.Next(Trades.Length)];
        var starterFirst = 130 + (random.Next(0, 400) * 0.05m);
        var afterOneYear = random.Next(20, 37) * 0.25m;
        decimal? afterThreeYears = hard >= 2 && random.Next(2) == 0 ? afterOneYear + (random.Next(12, 25) * 0.25m) : null;
        var firstTierHours = random.Next(1, 4);
        var eveningFrom = new TimeOnly(random.Next(17, 19), 0);
        var eveningFirst = 25 + (random.Next(0, 200) * 0.05m);
        var nightExtra = random.Next(32, 61) * 0.25m;
        var saturdayExtra = hard >= 2 ? random.Next(60, 101) * 0.25m : (decimal?)null;
        var employerPension = new[] { 8.0m, 8.5m, 9.0m, 10.0m }[random.Next(4)];
        var employeePension = new[] { 4.0m, 4.5m, 5.0m }[random.Next(3)];
        var qualifyingMonths = new[] { 2, 3, 6 }[random.Next(3)];
        decimal savingsFirst = 4 + random.Next(0, 4);

        // A renewal keeps what the old agreement settled and starts its rates above the old ones.
        if (previous is not null)
        {
            (title, work) = Trades[previous.Trade];
            starterFirst = previous.Starter + 4.00m;
            (afterOneYear, afterThreeYears) = (previous.AfterOneYear, previous.AfterThreeYears);
            (firstTierHours, eveningFrom) = (previous.FirstTierHours, previous.EveningFrom);
            eveningFirst = previous.Evening + 1.00m;
            (nightExtra, saturdayExtra) = (previous.NightExtra, hard >= 2 ? previous.SaturdayExtra ?? saturdayExtra : null);
            (employerPension, employeePension, qualifyingMonths) = (previous.EmployerPension, previous.EmployeePension, previous.QualifyingMonths);
            savingsFirst = previous.Savings + 0.5m;
        }

        var starter = Climb(starterFirst, steps.Length, () => 3.50m + (random.Next(0, 7) * 0.25m));
        var evening = Climb(eveningFirst, steps.Length, () => 0.75m + (random.Next(0, 4) * 0.25m));
        var savings = Climb(savingsFirst, steps.Length, () => random.Next(1, 3) * 0.5m);
        var variant = random.Next(2);

        var sections = new List<Section>();
        var rules = new List<Rule>();
        string Next() => $"§ {sections.Count + 1}";
        void Add(string heading, params Block[][] clauses)
        {
            var number = Next();
            sections.Add(new(number, heading, [.. clauses.Select((blocks, index) => new Clause($"{number}, stk. {index + 1}", blocks))]));
        }

        string[] stepHeader = [.. steps.Select(step => $"Pr. {Danish.Date(step)}")];

        // ---------------------------------------------------------------- opening sections

        Add("Overenskomstens område",
            [Text($"Overenskomsten omfatter medarbejdere, der er beskæftiget med {work} i virksomheder, som er medlem af Erhvervsforeningen Tværhavn.")],
            [Text("Overenskomsten omfatter ikke ledende medarbejdere, som har ret til at ansætte og afskedige personale.")]);

        Add("Ansættelse",
            [Text("Ved ansættelsen udleveres et ansættelsesbevis senest syv kalenderdage efter, at arbejdet er påbegyndt.")],
            [Text("De første tre måneder af ansættelsen er prøvetid, hvor opsigelse kan ske med 14 dages varsel fra begge sider.")]);

        var workingTime = Next();
        Add("Arbejdstid",
            [Text("Den normale ugentlige arbejdstid er 37 timer.")],
            [Text("Den daglige normale arbejdstid er 7 timer og 24 minutter.")],
            [Text("Arbejde betales efter de satser, der gælder på den dag, hvor arbejdet påbegyndes.")],
            [Text("Der holdes en daglig spisepause på 30 minutter, som ikke medregnes i arbejdstiden.")]);

        // ---------------------------------------------------------------- minimum wage

        var pay = Next();
        var wageClause = $"{pay}, stk. 1";
        Block[] wageBlocks;
        string wageQuote;
        if (hard == 1)
        {
            wageQuote = $"Mindstebetalingen pr. time udgør {Danish.Kroner(starter[0])}";
            wageBlocks =
            [
                Text($"{wageQuote} Efter 12 måneders anciennitet udgør mindstebetalingen {Danish.Amount(starter[0] + afterOneYear)} kr. pr. time."),
            ];
        }
        else
        {
            wageQuote = variant == 0
                ? "Mindstebetalingen pr. time udgør følgende satser:"
                : "Der betales mindst følgende timeløn:";
            var rows = new List<IReadOnlyList<string>>
            {
                Row(variant == 0 ? "Under 1 års anciennitet" : "0-11 måneders anciennitet", starter, 0),
                Row(variant == 0 ? "Efter 1 års anciennitet" : "Fra 12 måneders anciennitet", starter, afterOneYear),
            };
            if (afterThreeYears is { } third)
            {
                rows.Add(Row(variant == 0 ? "Efter 3 års anciennitet" : "Fra 36 måneders anciennitet", starter, third));
            }

            wageBlocks = [Text(wageQuote), new TableBlock(["Anciennitet", .. stepHeader], rows)];
        }

        for (var step = 0; step < steps.Length; step++)
        {
            var bands = new List<SeniorityBand> { new(0, starter[step]), new(12, starter[step] + afterOneYear) };
            if (afterThreeYears is { } third)
            {
                bands.Add(new(36, starter[step] + third));
            }

            rules.Add(new MinimumWageRule(Id("minimum_wage", steps[step]), StepPeriod(step), new(wageClause, wageQuote), bands));
        }

        List<Block[]> payClauses =
        [
            wageBlocks,
            [Text("Anciennitet regnes i hele måneder fra ansættelsesdatoen.")],
            [Text("Ved lønnen forstås i denne overenskomst timeløn, overarbejdstillæg og tillæg for forskudt tid.")],
            [Text("Alle beløb afrundes til nærmeste øre.")],
        ];
        if (hard >= 3)
        {
            // Rates that look like minimum wages and are not: the rule model has no young workers.
            string[][] others =
            {
                Row("Ungarbejdere under 18 år", starter, random.Next(180, 241) * -0.25m),
                Row("Lærlinge, 1. år", starter, random.Next(240, 281) * -0.25m),
                Row("Lærlinge, 2. år", starter, random.Next(140, 181) * -0.25m),
            };
            payClauses.Add(
            [
                Text("For ungarbejdere under 18 år og for lærlinge gælder nedenstående satser. Satserne er ikke omfattet af stk. 1."),
                new TableBlock(["Gruppe", .. stepHeader], others),
            ]);
        }

        Add("Løn", [.. payClauses]);

        // ---------------------------------------------------------------- overtime

        var overtime = Next();
        var tierHours = hard == 1 ? firstTierHours.ToString(System.Globalization.CultureInfo.InvariantCulture) : Danish.Word(firstTierHours);
        var tierQuote = firstTierHours == 1
            ? "For den første times overarbejde pr. dag betales et tillæg på 50,0 pct. af timelønnen."
            : $"For de første {tierHours} timers overarbejde pr. dag betales et tillæg på 50,0 pct. af timelønnen.";
        Add("Overarbejde",
            [
                Text(hard == 1
                    ? "Overarbejde er arbejde ud over den daglige normale arbejdstid på 7 timer og 24 minutter. Arbejdstiden opgøres samlet for alle vagter, der påbegyndes samme dag."
                    : $"Overarbejde er arbejde ud over den daglige normale arbejdstid, jf. {workingTime}, stk. 2. Arbejdstiden opgøres samlet for alle vagter, der påbegyndes samme dag."),
            ],
            [Text($"{tierQuote} For overarbejde herudover betales et tillæg på 100,0 pct. af timelønnen.")],
            [Text("For arbejde på søndage betales et tillæg på 100,0 pct. af timelønnen for alle timer. Søndagstimer medregnes ikke ved opgørelsen efter stk. 1.")],
            [Text("Overarbejde skal så vidt muligt varsles senest dagen før.")]);
        rules.Add(new OvertimeRule(
            "overtime",
            whole,
            new($"{overtime}, stk. 2", tierQuote),
            7.4m,
            [new(firstTierHours, 50.0m), new(null, 100.0m)],
            100.0m));

        // ---------------------------------------------------------------- inconvenient hours

        var shifted = Next();
        var windowClause = $"{shifted}, stk. 1";
        string windowQuote;
        Block[] windowBlocks;
        if (hard == 1)
        {
            windowQuote = $"For arbejde mandag til fredag mellem {Danish.Clock(eveningFrom)} og kl. 22.00 betales et tillæg på {Danish.Kroner(evening[0])} pr. time.";
            windowBlocks =
            [
                Text($"{windowQuote} For arbejde alle ugens dage mellem kl. 22.00 og kl. 06.00 betales et tillæg på {Danish.Kroner(evening[0] + nightExtra)} pr. time."),
            ];
        }
        else
        {
            windowQuote = variant == 0
                ? "For arbejde på forskudt tid betales følgende tillæg pr. time:"
                : "Tillæg for arbejde på forskudt tid udgør pr. time:";
            var rows = new List<IReadOnlyList<string>>
            {
                Row($"Mandag til fredag {Danish.Clock(eveningFrom)}-22.00", evening, 0),
                Row("Lørdag kl. 14.00-24.00", evening, saturdayExtra!.Value),
                Row("Alle dage kl. 22.00-06.00", evening, nightExtra),
            };
            windowBlocks = [Text(windowQuote), new TableBlock(["Tidsrum", .. stepHeader], rows)];
        }

        Add("Forskudt arbejdstid",
            windowBlocks,
            [Text("Hvor flere tillæg dækker samme tidsrum, betales alene det højeste tillæg.")],
            [Text("Tillæg for forskudt tid betales ud over eventuelt overarbejdstillæg.")]);
        for (var step = 0; step < steps.Length; step++)
        {
            var windows = new List<TimeWindow> { new(Weekdays, eveningFrom, new(22, 0), evening[step]) };
            if (saturdayExtra is { } saturday)
            {
                windows.Add(new([DayOfWeek.Saturday], new(14, 0), new(0, 0), evening[step] + saturday));
            }

            windows.Add(new(EveryDay, new(22, 0), new(6, 0), evening[step] + nightExtra));
            rules.Add(new InconvenientHoursRule(Id("inconvenient_hours", steps[step]), StepPeriod(step), new(windowClause, windowQuote), windows));
        }

        // ---------------------------------------------------------------- pension

        var pension = Next();
        var months = hard == 1 ? qualifyingMonths.ToString(System.Globalization.CultureInfo.InvariantCulture) : Danish.Word(qualifyingMonths);
        var pensionQuote = variant == 0
            ? $"Pensionsbidraget udgør {Danish.Percent(employerPension + employeePension)} af lønnen, hvoraf arbejdsgiveren betaler {Danish.Percent(employerPension)} og medarbejderen {Danish.Percent(employeePension)}"
            : $"Arbejdsgiveren indbetaler {Danish.Percent(employerPension)} og medarbejderen {Danish.Percent(employeePension)} af lønnen til pensionsordningen.";
        Add("Pension",
            [Text(pensionQuote)],
            [Text($"Medarbejdere er omfattet af pensionsordningen efter {months} måneders anciennitet.")],
            [Text("Pensionsbidraget indbetales månedligt til det pensionsselskab, som overenskomstens parter har aftalt.")]);

        // A protocol amendment raises the employer's share from the second year, over the main text.
        decimal? raisedEmployerPension = hard >= 3 ? employerPension + (random.Next(1, 3) * 0.5m) : null;
        var pensionSource = new Source($"{pension}, stk. 1", pensionQuote);
        rules.Add(new PensionRule(
            Id("pension", start),
            raisedEmployerPension is null ? whole : new(start, steps[1]),
            pensionSource,
            employerPension,
            employeePension,
            qualifyingMonths));

        // ---------------------------------------------------------------- holiday allowance and savings

        var holiday = Next();
        var holidayQuote = "Feriegodtgørelsen udgør 12,5 pct. af lønnen.";
        Add("Ferie og feriegodtgørelse",
            [Text("Ferie holdes i overensstemmelse med ferielovens regler.")],
            [Text(holidayQuote)],
            [Text("Ud over ferie har medarbejderen ret til fem feriefridage pr. ferieår.")]);
        rules.Add(new HolidayAllowanceRule("holiday_allowance", whole, new($"{holiday}, stk. 2", holidayQuote), 12.5m));

        var savingsSection = Next();
        string savingsQuote;
        Block[] savingsBlocks;
        if (hard == 1)
        {
            savingsQuote = $"Til medarbejderens fritvalgskonto indbetaler arbejdsgiveren {Danish.Percent(savings[0])} af lønnen.";
            savingsBlocks = [Text(savingsQuote)];
        }
        else if (variant == 0)
        {
            savingsQuote = "Arbejdsgiverens bidrag til medarbejderens fritvalgskonto udgør følgende andel af lønnen:";
            savingsBlocks = [Text(savingsQuote), new TableBlock(stepHeader, [[.. savings.Select(Danish.Percent)]])];
        }
        else
        {
            var parts = steps.Select((step, index) => $"{Danish.Percent(savings[index])} fra {Danish.Date(step)}").ToList();
            savingsQuote = $"Til medarbejderens fritvalgskonto indbetaler arbejdsgiveren {parts[0]}, {parts[1]} og {parts[2]}.";
            savingsBlocks = [Text(hard >= 3 ? $"{savingsQuote} Den hidtidige sats på {Danish.Percent(savings[0] - 1)} bortfalder." : savingsQuote)];
        }

        Add("Fritvalgskonto",
            savingsBlocks,
            [Text("Medarbejderen kan vælge at få saldoen udbetalt i forbindelse med ferie og fridage eller indbetalt som ekstra pensionsbidrag.")]);
        for (var step = 0; step < steps.Length; step++)
        {
            rules.Add(new SpecialSavingsRule(Id("special_savings", steps[step]), StepPeriod(step), new($"{savingsSection}, stk. 1", savingsQuote), savings[step]));
        }

        // ---------------------------------------------------------------- closing sections

        Add("Sygdom",
            [Text("Medarbejdere med mindst seks måneders anciennitet har ret til løn under sygdom i indtil ni uger.")],
            [Text("Sygdom skal anmeldes til virksomheden hurtigst muligt og senest ved arbejdstids begyndelse.")]);

        Block[] notice = hard == 1
            ? [Text("Efter prøvetiden er opsigelsesvarslet fra virksomhedens side 21 dage og fra medarbejderens side 7 dage.")]
            :
            [
                Text("Efter prøvetiden gælder følgende opsigelsesvarsler:"),
                new TableBlock(
                    ["Anciennitet", "Fra virksomheden", "Fra medarbejderen"],
                    [["Under 1 år", "21 dage", "7 dage"], ["1 til 3 år", "28 dage", "14 dage"], ["Over 3 år", "56 dage", "28 dage"]]),
            ];
        Add("Opsigelse", notice, [Text("Opsigelse skal ske skriftligt.")]);

        Add("Tillidsrepræsentanter",
            [Text("I virksomheder med mindst seks medarbejdere kan medarbejderne vælge en tillidsrepræsentant af deres midte.")],
            [Text("Tillidsrepræsentanten skal have den nødvendige tid til at udføre sit hverv.")]);

        if (hard >= 2)
        {
            Add("Uddannelsesfond",
                [Text($"Virksomheden indbetaler {random.Next(40, 71)} øre pr. præsteret arbejdstime til parternes uddannelsesfond.")],
                [Text("Medarbejdere med mindst ni måneders anciennitet har ret til to ugers selvvalgt uddannelse om året.")]);
        }

        Add("Fagretlig behandling",
            [Text("Uenighed om forståelsen af overenskomsten søges løst ved forhandling mellem virksomheden og tillidsrepræsentanten.")],
            [Text("Opnås der ikke enighed, kan sagen videreføres af organisationerne efter reglerne for behandling af faglig strid.")]);

        Add("Varighed",
            [Text($"Overenskomsten træder i kraft den {Danish.Date(start)} og kan opsiges med tre måneders varsel til en 1. marts, dog tidligst den {Danish.Date(start.AddYears(3))}.")],
            [Text("Selv om overenskomsten er opsagt eller udløbet, gælder dens bestemmelser, indtil en ny overenskomst træder i stedet.")]);

        // ---------------------------------------------------------------- protocol amendment

        var protocols = new List<Section>();
        if (raisedEmployerPension is { } raised)
        {
            var raiseQuote = $"Med virkning fra den {Danish.Date(steps[1])} forhøjes arbejdsgiverens pensionsbidrag til {Danish.Percent(raised)} af lønnen.";
            protocols.Add(new("Protokollat 1", "Forhøjelse af pensionsbidrag",
            [
                new("Protokollat 1, stk. 1", [Text($"Parterne er enige om at ændre {pension}, stk. 1. {raiseQuote}")]),
                new("Protokollat 1, stk. 2", [Text("Medarbejderens bidrag og anciennitetskravet er uændrede.")]),
            ]));
            rules.Add(new PensionRule(
                Id("pension", steps[1]),
                new(steps[1]),
                new("Protokollat 1, stk. 1", raiseQuote),
                raised,
                employeePension,
                qualifyingMonths));
        }

        var document = new AgreementDocument(
            spec.Id,
            $"{title} {start.Year}-{start.Year + 3}",
            "mellem Erhvervsforeningen Tværhavn og Fagforeningen Elmekær",
            sections,
            protocols);
        return new(document, new(spec.Id, 1, rules));
    }

    /// <summary>Where the agreement being renewed left off.</summary>
    private sealed record Baseline(
        int Trade,
        decimal Starter,
        decimal AfterOneYear,
        decimal? AfterThreeYears,
        int FirstTierHours,
        TimeOnly EveningFrom,
        decimal Evening,
        decimal NightExtra,
        decimal? SaturdayExtra,
        decimal EmployerPension,
        decimal EmployeePension,
        int QualifyingMonths,
        decimal Savings);

    /// <summary>A value for each step, rising by a fresh increment each time.</summary>
    private static decimal[] Climb(decimal first, int steps, Func<decimal> increment)
    {
        var values = new decimal[steps];
        values[0] = first;
        for (var step = 1; step < steps; step++)
        {
            values[step] = values[step - 1] + increment();
        }

        return values;
    }

    private static string Id(string kind, DateOnly from) => $"{kind}-{from.Year}";

    private static Paragraph Text(string text) => new(text);

    /// <summary>A table row: a label, then an amount for each step.</summary>
    private static string[] Row(string label, decimal[] values, decimal added) =>
        [label, .. values.Select(value => Danish.Kroner(value + added))];
}
