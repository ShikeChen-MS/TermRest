using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>Terminal.Gui keeps some process-wide state (default key bindings, drivers), so UI tests run one at a time.</summary>
[CollectionDefinition("TUI", DisableParallelization = true)]
public sealed class TuiCollection;
