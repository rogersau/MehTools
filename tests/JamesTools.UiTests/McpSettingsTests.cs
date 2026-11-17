using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using JamesTools;

internal static class McpSettingsTests
{
    internal static void Run(string directory)
    {
        var type = typeof(McpSettingsWindow);
        object? Call(string method, params object?[] arguments)
        {
            try { return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            throw new Exception("Expected MCP settings write/read to be rejected.");
        }
        var path = Path.Combine(directory, "Mcp", "config.json");
        Check(Call("ReadConfiguration", path) is null, "Missing configuration must not enable MCP.");
        var initial = "{\"schemaVersion\":1,\"enabled\":false,\"futureField\":{\"keep\":true}}";
        Call("WriteConfiguration", path, null, initial);
        var parsed = (JsonObject)Call("ParseConfiguration", initial)!;
        parsed["enabled"] = true;
        var updated = parsed.ToJsonString();
        Call("WriteConfiguration", path, initial, updated);
        Check(File.ReadAllText(path) == updated && parsed["futureField"]!["keep"]!.GetValue<bool>(), "Configuration edits must preserve unrelated fields.");
        Reject(() => Call("WriteConfiguration", path, initial, "{}"));
        Check(File.ReadAllText(path) == updated, "Stale editor must not overwrite newer configuration.");
        Reject(() => Call("ParseConfiguration", "{\"schemaVersion\":99}"));
        File.WriteAllText(path, new string('x', 65537));
        Reject(() => Call("ReadConfiguration", path));
        File.WriteAllText(path, updated);
        Check(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Count() == 0, "Atomic writes must clean staging files.");
        Console.WriteLine("PASS MCP settings defaults, atomic writes, external edits, unknown-field preservation and limits");
    }
}
