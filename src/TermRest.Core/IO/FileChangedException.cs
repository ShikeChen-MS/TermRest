namespace TermRest.Core.IO;

/// <summary>
/// Files that changed on disk since TermRest read them (a <c>git pull</c>, Postman, an editor): saving would undo those
/// changes, so they were not written. Saving again with overwriting allowed writes them anyway.
/// </summary>
public sealed class FileChangedException(IReadOnlyList<string> paths)
    : IOException($"Changed on disk since TermRest read {(paths.Count == 1 ? "it" : "them")}: {string.Join(", ", paths)}. Saving would undo those changes.")
{
    /// <summary>The files that changed (full paths).</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}
