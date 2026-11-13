namespace JamesTools.Core.Diagnostics;

public static class PresentMonLocator
{
    public static string? Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = configuredPath.Trim();
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        foreach (var candidate in BundledCandidates())
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
        }
        return null;
    }

    public static string? FindBundled()
    {
        foreach (var candidate in BundledCandidates())
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
        }
        return null;
    }

    public static IEnumerable<string> BundledCandidates()
    {
        var bases = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? "" }
            .Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bases)
        {
            yield return Path.Combine(b, "PresentMon.exe");
            yield return Path.Combine(b, "tools", "PresentMon", "PresentMon.exe");
            yield return Path.Combine(b, "PresentMon", "PresentMon.exe");
            yield return Path.Combine(b, "third_party", "PresentMon", "PresentMon.exe");
        }
    }
}
