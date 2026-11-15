namespace JamesTools.Mcp.Tests;

internal sealed class TestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JamesTools.Mcp.Tests", Guid.NewGuid().ToString("N"));
    internal TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        // Only the exact directory created by this instance is eligible for cleanup.
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JamesTools.Mcp.Tests")) + System.IO.Path.DirectorySeparatorChar;
        var target = System.IO.Path.GetFullPath(Path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test directory escaped its root.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
