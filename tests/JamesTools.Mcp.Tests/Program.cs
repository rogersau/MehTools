using JamesTools.Core.Mcp;
using JamesTools.Mcp;
using JamesTools.Mcp.Tests;
using Microsoft.Extensions.Hosting;

if (args is ["--fixture-stdio", var directory, var scenario])
{
    using var backend = new FixtureBackend { Oversized = scenario == "oversized", Fail = scenario == "failure" };
    if (scenario == "saturated") LargeSessionFixture.Write(Path.Combine(directory, "sessions"));
    await using var sessions = new DiagnosticSessionService(backend, Path.Combine(directory, "sessions"));
    var builder = Host.CreateApplicationBuilder([]);
    McpHost.AddDiagnostics(builder.Services, backend, sessions);
    using var host = McpHost.BuildStdioHost(builder, Console.OpenStandardInput(), Console.OpenStandardOutput());
    try { await host.RunAsync(); }
    finally
    {
        await sessions.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "disconnected"), "clean");
    }
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("contracts", ContractTests.RunAsync),
    ("sessions", SessionTests.RunAsync),
    ("stdio", TransportTests.StdioAsync),
    ("http", TransportTests.HttpAsync),
    ("https", TlsTests.RunAsync),
    ("configuration", SecurityTests.ConfigurationAsync)
};
if (args.Contains("--hardware")) tests.Add(("hardware", TransportTests.HardwareAsync));
if (args.Contains("--list")) { foreach (var test in tests) Console.WriteLine(test.Name); return 0; }
var only = Array.IndexOf(args, "--only");
if (only >= 0) tests = tests.Where(t => t.Name == args.ElementAtOrDefault(only + 1)).ToList();
if (tests.Count == 0) { Console.Error.WriteLine("No tests selected."); return 2; }
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromMinutes(3)); Console.WriteLine("PASS  " + test.Name); }
    catch (Exception ex)
    {
        failures++;
        var messages = new List<string>();
        void Describe(Exception error)
        {
            if (error is AggregateException aggregate) { foreach (var child in aggregate.InnerExceptions) Describe(child); return; }
            messages.Add(error.GetType().Name + ": " + error.Message);
            if (error.InnerException is { } inner) Describe(inner);
        }
        Describe(ex);
        var detail = string.Join(Environment.NewLine + "  ", messages);
        Console.Error.WriteLine("FAIL  " + test.Name + ": " + detail[..Math.Min(detail.Length, 10000)]);
    }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} MCP test groups passed.");
if (!args.Contains("--hardware")) Console.WriteLine("Hardware integration was not requested; use --hardware to observe real Windows sensor availability.");
return failures == 0 ? 0 : 1;
