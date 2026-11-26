using JamesTools.Core.Mcp;

namespace JamesTools.Mcp.Tests;

internal static class InvestigationWindowsSmoke
{
    internal static async Task RunAsync()
    {
        using var directory = new TestDirectory();
        using var backend = new FixtureBackend();
        var storage = Path.Combine(directory.Path, "sessions");
        Guid id;
        await using (var service = new DiagnosticSessionService(backend, storage))
        {
            var tools = new DiagnosticTools(new DiagnosticAccess(backend, service));
            var accepted = await tools.StartStutterInvestigation(3, default); id = accepted.Id;
            await Task.Delay(4000);
            var stopped = await tools.StopStutterInvestigation(id, default);
            Check.That(stopped.Status is "completed" or "stopped", "Real investigator did not complete through the MCP session lifecycle.");
            Check.That(stopped.Investigation is { Devices.Count: > 0 }, "Real device inventory was unavailable through MCP.");
            var devices = await tools.ListInvestigationDevices(id, default);
            Check.That(devices.Items.Count > 0, "Real inventory paging returned no device evidence.");
            Console.WriteLine($"WINDOWS MCP: {stopped.Status}; retained devices={stopped.Investigation!.Devices.Count}; first page={devices.Items.Count}; budget={System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(stopped).Length} bytes.");
        }
        await using (var reopened = new DiagnosticSessionService(backend, storage))
        {
            var persisted = await reopened.GetAsync(id, default);
            Check.That(persisted.Investigation is { Devices.Count: > 0 }, "Real investigation report did not survive store reopen.");
        }
    }
}
