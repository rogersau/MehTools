using System.Text.Json;

namespace JamesTools.Core.Diagnostics;

/// <summary>Loads the optional investigation section without changing legacy report readers.</summary>
public static class InvestigationExports
{
    public static InvestigationReport Read(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("Investigation reports must be at most 32 MiB.");
        return Parse(File.ReadAllText(path));
    }

    public static InvestigationReport Parse(string text)
    {
        if (text.Length > 32 * 1024 * 1024) throw new InvalidDataException("Investigation report is too large.");
        if (text.TrimStart().StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("stutterInvestigation", out var section) && section.ValueKind == JsonValueKind.Object)
                return InvestigationReportWriter.FromJson(section.GetRawText());
            return InvestigationReportWriter.FromJson(text);
        }
        var summary = DiagnosticComparison.MachineSummary(text);
        if (summary is { } root && root.TryGetProperty("stutterInvestigation", out var investigation) && investigation.ValueKind == JsonValueKind.Object)
            return InvestigationReportWriter.FromJson(investigation.GetRawText());
        throw new InvalidDataException("This report has no Stutter Investigator evidence. Existing performance reports remain available in the performance timeline.");
    }

    public static string InterruptedCaptureNotice(string directory)
    {
        var state = Path.Combine(directory, "investigation-state.json");
        if (!File.Exists(state)) return "No investigation capture state is available.";
        using var json = JsonDocument.Parse(File.ReadAllText(state));
        return json.RootElement.GetProperty("State").GetString() switch
        {
            "completed" => "Capture completed. Read investigation.json or report.md for the analysis.",
            "recording" => "Capture was active when state was last written. If its owner has exited, this is an interrupted capture; raw files may be incomplete. No final analysis or complete coverage is implied.",
            _ => "Capture ended with incomplete coverage. Inspect collector status and raw evidence before interpreting it."
        };
    }
}
