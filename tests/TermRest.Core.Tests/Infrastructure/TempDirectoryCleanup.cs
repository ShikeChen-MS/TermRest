using Xunit;

[assembly: AssemblyFixture(typeof(TermRest.Core.Tests.Infrastructure.TempDirectoryCleanup))]

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>Assembly fixture: disposed by xunit after the last test, so scratch directories don't pile up in %TEMP%.</summary>
public sealed class TempDirectoryCleanup : IDisposable
{
    public void Dispose() => TestFiles.DeleteCreatedDirectories();
}
