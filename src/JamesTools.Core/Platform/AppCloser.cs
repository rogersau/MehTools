using System.Diagnostics;

namespace JamesTools.Core.Platform;

internal static class AppCloser
{
    public static int Close(IEnumerable<string> targets)
    {
        var names = targets
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(NormalizeName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0) return 0;
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var signaled = 0;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return 0; }
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                var name = process.ProcessName;
                if (!wanted.Contains(name)) continue;
                try
                {
                    if (process.CloseMainWindow()) signaled++;
                }
                catch { }
            }
            catch { }
            finally { process.Dispose(); }
        }
        return signaled;
    }

    private static string NormalizeName(string target)
    {
        var value = target.Trim().Trim('"');
        if (value.Length == 0) return "";
        try
        {
            if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar) || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                value = Path.GetFileNameWithoutExtension(value);
        }
        catch { }
        return value.Trim();
    }
}
