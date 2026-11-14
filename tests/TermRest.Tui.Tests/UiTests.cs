using TermRest.Tui.Dialogs;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>Helpers shared by the dialogs.</summary>
public sealed class UiTests
{
    [Fact]
    public void A_line_wrapped_where_it_has_no_space_is_not_cut_inside_a_character()
    {
        var line = new string('a', 39) + "😀" + new string('b', 30);

        Assert.Equal([new string('a', 39), "  😀" + new string('b', 30)], Ui.Wrap(line, 40).ToArray());
    }
}
