namespace TermRest.Core.Scripting;

/// <summary>A collection, folder or request with its scripts, named as errors and output name it ("collection 'API'").</summary>
public sealed record ScriptLevel(string Name, ScriptSet Scripts);

/// <summary>
/// Which scripts run around one request. A request runs its scripts in one language, in one runtime: the language its
/// Scripts tab chooses, or else the first language of the user's order in which none of them is missing. A phase
/// (pre-request, post-response) runs when the request says so, or else when there are scripts for it; each of its
/// scripts, the collection's, the folders' and the request's own, must then be there in that language. They run one
/// after the other: the collection's, the folders' (outermost first), the request's. When no language has them all,
/// the request is not sent (<see cref="Error"/>).
/// </summary>
public sealed class ScriptPlan
{
    private readonly bool[] _runs;
    private readonly bool[] _has;

    private ScriptPlan(bool[] runs, bool[] has, List<string> notes)
    {
        _runs = runs;
        _has = has;
        Notes = notes;
    }

    /// <summary>No scripts at all.</summary>
    public static ScriptPlan None { get; } = new([false, false], [false, false], []);

    /// <summary>The language the scripts run in; null when none runs.</summary>
    public string? Language { get; private init; }

    public IReadOnlyList<ScriptCode> PreRequest { get; private init; } = [];

    public IReadOnlyList<ScriptCode> PostResponse { get; private init; } = [];

    public IReadOnlyList<string> Notes { get; }

    /// <summary>Why the request cannot run its scripts (it is then not sent); null when it can.</summary>
    public string? Error { get; private init; }

    /// <summary>The same, in a few words (for the request's Scripts tab).</summary>
    public string? Problem { get; private init; }

    public bool IsEmpty => PreRequest.Count == 0 && PostResponse.Count == 0;

    /// <summary>Whether the scripts of <paramref name="phase"/> run: as the request says, or else when there are some.</summary>
    public bool Runs(ScriptPhase phase) => _runs[(int)phase];

    /// <summary>Whether there is a script for <paramref name="phase"/> in any language: what <see cref="Runs"/> is unless the request says otherwise.</summary>
    public bool Has(ScriptPhase phase) => _has[(int)phase];

    /// <param name="levels">The collection, its folders (outermost first) and the request.</param>
    /// <param name="settings">The request's settings (its Scripts tab).</param>
    /// <param name="order">The languages to try, first to last, when the request does not choose one.</param>
    public static ScriptPlan For(IReadOnlyList<ScriptLevel> levels, ScriptSettings settings, IReadOnlyList<string> order)
    {
        var phases = ScriptPhases.All;
        var has = phases.Select(p => levels.Any(l => l.Scripts.Has(p))).ToArray();
        var runs = phases.Select(p => settings.Runs(p) ?? has[(int)p]).ToArray();
        var running = phases.Where(p => runs[(int)p]).ToList();
        var notes = new List<string>();
        foreach (var phase in phases.Where(p => settings.Runs(p) == false && has[(int)p]))
        {
            var owners = levels.Where(l => l.Scripts.Has(phase)).Select(l => l.Name);
            notes.Add($"{Capitalized(phase.Label())} scripts are turned off for this request (its Scripts tab): those of {string.Join(", ", owners)} did not run.");
        }

        if (running.Count == 0)
        {
            return new ScriptPlan(runs, has, notes);
        }

        var absent = running.Where(p => !has[(int)p]).ToList();
        if (absent.Count > 0)
        {
            return new ScriptPlan(runs, has, notes)
            {
                Error = $"This request runs {Phases(absent, " and ")} scripts (its Scripts tab), but there is none in it, its folders or its collection, " +
                        $"in any language. Write one, or turn {Phases(absent, " and ")} scripts off in its Scripts tab.",
                Problem = $"no {Phases(absent, " or ")} script",
            };
        }

        // The scripts that run and have no version in a language: those a language lacks to run them all.
        List<(ScriptPhase Phase, ScriptLevel Level)> Lacks(string language) =>
            [.. running.SelectMany(p => levels.Where(l => l.Scripts.Has(p) && !l.Scripts.Has(p, language)).Select(l => (p, l)))];
        string Describe(IEnumerable<(ScriptPhase Phase, ScriptLevel Level)> scripts) =>
            string.Join(", ", scripts.Select(s => $"the {s.Phase.Label()} script of {s.Level.Name}"));

        string language;
        if (settings.Language is { } chosen)
        {
            var lacks = Lacks(chosen);
            if (lacks.Count > 0)
            {
                var label = ScriptLanguage.Label(chosen);
                return new ScriptPlan(runs, has, notes)
                {
                    Error = $"This request runs its scripts in {label} (its Scripts tab), but there is no {label} version of {Describe(lacks)}. " +
                            $"Write {(lacks.Count == 1 ? "it" : "them")} in {label}, choose another language, or turn " +
                            $"{Phases(lacks.Select(s => s.Phase).Distinct(), " and ")} scripts off in its Scripts tab.",
                    Problem = lacks.Count == 1 ? $"1 script lacks {label}" : $"{lacks.Count} scripts lack {label}",
                };
            }

            language = chosen;
        }
        else if (order.FirstOrDefault(l => Lacks(l).Count == 0) is { } first)
        {
            language = first;
        }
        else
        {
            var languages = order.Where(l => running.Any(p => levels.Any(level => level.Scripts.Has(p, l))))
                .Select(l => $"{ScriptLanguage.Label(l)} lacks {Describe(Lacks(l))}");
            return new ScriptPlan(runs, has, notes)
            {
                Error = $"No language has all the scripts this request runs: {string.Join("; ", languages)}. Write the missing ones in one language, " +
                        "choose a language in the request's Scripts tab, or turn pre-request or post-response scripts off there.",
                Problem = "no language has them all",
            };
        }

        // Scripts in other languages are alternatives: say which language ran, and why not one before it.
        var others = order.Where(l => l != language && running.Any(p => levels.Any(level => level.Scripts.Has(p, l)))).ToList();
        if (others.Count > 0)
        {
            var passedOver = order.TakeWhile(l => l != language).Where(others.Contains)
                .Select(l => $"{ScriptLanguage.Label(l)} lacks {Describe(Lacks(l))}").ToList();
            notes.Insert(0, settings.Language is not null
                ? $"Language: {ScriptLanguage.Label(language)}, as the request's Scripts tab chooses."
                : $"Language: {ScriptLanguage.Label(language)}, the first of your language order with all the scripts of this request" +
                  (passedOver.Count == 0 ? "." : $" ({string.Join("; ", passedOver)})."));
        }

        return new ScriptPlan(runs, has, notes)
        {
            Language = language,
            PreRequest = Scripts(ScriptPhase.PreRequest, language),
            PostResponse = Scripts(ScriptPhase.PostResponse, language),
        };

        List<ScriptCode> Scripts(ScriptPhase phase, string chosen) => runs[(int)phase]
            ? [.. levels.Where(l => l.Scripts.Has(phase, chosen)).Select(l => new ScriptCode(l.Name, l.Scripts.For(chosen).Code(phase), chosen))]
            : [];
    }

    private static string Phases(IEnumerable<ScriptPhase> phases, string conjunction) => string.Join(conjunction, phases.Select(p => p.Label()));

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
