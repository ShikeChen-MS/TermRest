using TermRest.Core.Scripting;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Which scripts run around a request: one language, the first of the order with all of them, or the one it chooses.</summary>
public class ScriptPlanTests
{
    private const string Collection = "collection 'API'";
    private const string Folder = "folder 'Users'";
    private const string Request = "request 'Users/Create'";

    private static ScriptLevel Level(string name, params LanguageScripts[] scripts) => new(name, new ScriptSet(scripts));

    private static LanguageScripts Python(string pre = "", string test = "") => new(ScriptLanguage.Python, pre, test);

    private static LanguageScripts JavaScript(string pre = "", string test = "") => new(ScriptLanguage.JavaScript, pre, test);

    private static LanguageScripts PowerShell(string pre = "", string test = "") => new(ScriptLanguage.PowerShell, pre, test);

    private static IEnumerable<(string Name, string Code)> Scripts(IEnumerable<ScriptCode> scripts) => scripts.Select(s => (s.Name, s.Code));

    [Fact]
    public void Without_scripts_nothing_runs()
    {
        var plan = ScriptPlan.For([Level(Collection), Level(Request)], ScriptSettings.Auto, ScriptLanguage.All);

        Assert.Null(plan.Language);
        Assert.True(plan.IsEmpty);
        Assert.Null(plan.Error);
        Assert.Empty(plan.Notes);
        Assert.False(plan.Runs(ScriptPhase.PreRequest) || plan.Runs(ScriptPhase.PostResponse));
        Assert.True(ScriptPlan.None.IsEmpty);
    }

    [Fact]
    public void The_first_language_of_the_order_with_every_script_runs_them_all_from_the_collection_down()
    {
        ScriptLevel[] levels =
        [
            Level(Collection, JavaScript(pre: "c-js"), PowerShell(pre: "c-ps")),
            Level(Folder, PowerShell(test: "f-ps")),
            Level(Request, JavaScript(test: "r-js"), PowerShell(pre: "r-ps", test: "r-ps-test")),
        ];

        var plan = ScriptPlan.For(levels, ScriptSettings.Auto, ScriptLanguage.All);

        Assert.Equal(ScriptLanguage.PowerShell, plan.Language);
        Assert.Equal([(Collection, "c-ps"), (Request, "r-ps")], Scripts(plan.PreRequest));
        Assert.Equal([(Folder, "f-ps"), (Request, "r-ps-test")], Scripts(plan.PostResponse));
        Assert.All(plan.PreRequest.Concat(plan.PostResponse), s => Assert.Equal(ScriptLanguage.PowerShell, s.Language));
        Assert.Null(plan.Error);
        Assert.Equal(
            ["Language: PowerShell, the first of your language order with all the scripts of this request (JavaScript lacks the pre-request script of " +
             "request 'Users/Create', the post-response script of folder 'Users')."],
            plan.Notes);

        // In another order, the first complete language is still the one: JavaScript lacks scripts whatever its place.
        Assert.Equal(ScriptLanguage.PowerShell, ScriptPlan.For(levels, ScriptSettings.Auto, [ScriptLanguage.JavaScript, ScriptLanguage.PowerShell]).Language);

        // A single language needs no note.
        var single = ScriptPlan.For([Level(Collection, Python(pre: "p")), Level(Request, Python(test: "t"))], ScriptSettings.Auto, ScriptLanguage.All);
        Assert.Equal((ScriptLanguage.Python, 0), (single.Language, single.Notes.Count));
    }

    [Fact]
    public void A_request_can_choose_its_language_and_then_needs_all_its_scripts_in_it()
    {
        ScriptLevel[] levels = [Level(Collection, JavaScript(pre: "c-js"), Python(pre: "c-py")), Level(Request, JavaScript(test: "r-js"), Python(test: "r-py"))];

        var python = ScriptPlan.For(levels, ScriptSettings.Auto, ScriptLanguage.All);
        var javaScript = ScriptPlan.For(levels, new ScriptSettings(ScriptLanguage.JavaScript), ScriptLanguage.All);

        Assert.Equal(ScriptLanguage.Python, python.Language);
        Assert.Equal(ScriptLanguage.JavaScript, javaScript.Language);
        Assert.Equal([(Collection, "c-js")], Scripts(javaScript.PreRequest));
        Assert.Equal(["Language: JavaScript, as the request's Scripts tab chooses."], javaScript.Notes);

        var powerShell = ScriptPlan.For(levels, new ScriptSettings(ScriptLanguage.PowerShell), ScriptLanguage.All);
        Assert.Null(powerShell.Language);
        Assert.True(powerShell.IsEmpty);
        Assert.Equal("2 scripts lack PowerShell", powerShell.Problem);
        Assert.Equal(
            "This request runs its scripts in PowerShell (its Scripts tab), but there is no PowerShell version of the pre-request script of collection " +
            "'API', the post-response script of request 'Users/Create'. Write them in PowerShell, choose another language, or turn pre-request and " +
            "post-response scripts off in its Scripts tab.",
            powerShell.Error);
    }

    [Fact]
    public void Turning_a_phase_off_skips_its_scripts_everywhere_and_the_language_needs_only_the_others()
    {
        ScriptLevel[] levels = [Level(Collection, PowerShell(pre: "c-ps")), Level(Request, JavaScript(test: "r-js"))];
        var both = ScriptPlan.For(levels, ScriptSettings.Auto, ScriptLanguage.All);
        Assert.Equal("no language has them all", both.Problem);
        Assert.Equal(
            "No language has all the scripts this request runs: JavaScript lacks the pre-request script of collection 'API'; PowerShell lacks the " +
            "post-response script of request 'Users/Create'. Write the missing ones in one language, choose a language in the request's Scripts tab, " +
            "or turn pre-request or post-response scripts off there.",
            both.Error);

        var testsOnly = ScriptPlan.For(levels, new ScriptSettings(PreRequest: false), ScriptLanguage.All);
        Assert.Equal(ScriptLanguage.JavaScript, testsOnly.Language);
        Assert.Empty(testsOnly.PreRequest);
        Assert.Equal([(Request, "r-js")], Scripts(testsOnly.PostResponse));
        Assert.False(testsOnly.Runs(ScriptPhase.PreRequest));
        Assert.True(testsOnly.Has(ScriptPhase.PreRequest));
        Assert.Equal(["Pre-request scripts are turned off for this request (its Scripts tab): those of collection 'API' did not run."], testsOnly.Notes);

        var none = ScriptPlan.For(levels, new ScriptSettings(PreRequest: false, PostResponse: false), ScriptLanguage.All);
        Assert.Equal((null, true, 2), (none.Language, none.IsEmpty, none.Notes.Count));
        Assert.Null(none.Error);
    }

    [Fact]
    public void A_phase_turned_on_needs_a_script()
    {
        var plan = ScriptPlan.For([Level(Collection, Python(pre: "p")), Level(Request)], new ScriptSettings(PostResponse: true), ScriptLanguage.All);

        Assert.Null(plan.Language);
        Assert.Equal("no post-response script", plan.Problem);
        Assert.Equal(
            "This request runs post-response scripts (its Scripts tab), but there is none in it, its folders or its collection, in any language. " +
            "Write one, or turn post-response scripts off in its Scripts tab.",
            plan.Error);
        Assert.True(plan.Runs(ScriptPhase.PostResponse));
        Assert.False(plan.Has(ScriptPhase.PostResponse));
    }

    [Fact]
    public void A_script_set_keeps_one_pre_request_and_one_post_response_script_per_language()
    {
        var set = new ScriptSet([PowerShell(test: "t"), Python(pre: "p"), JavaScript(), Python(pre: "p2")]);

        Assert.Equal([ScriptLanguage.Python, ScriptLanguage.PowerShell], set.Languages.Select(s => s.Language));
        Assert.Equal("p2", set.For(ScriptLanguage.Python).PreRequest);
        Assert.Equal(new LanguageScripts(ScriptLanguage.CSharp, "", ""), set.For(ScriptLanguage.CSharp));
        Assert.True(set.Has(ScriptPhase.PostResponse));
        Assert.False(set.Has(ScriptPhase.PostResponse, ScriptLanguage.Python));
        Assert.Equal(new ScriptSet([Python(pre: "p2"), PowerShell(test: "t")]), set);
        Assert.Equal(new ScriptSet([PowerShell(test: "t")]), set.With(Python()));
        Assert.True(new ScriptSet("  ", "\n", ScriptLanguage.Python).IsEmpty);
        Assert.NotEqual(ScriptSet.Empty, set);
    }
}
