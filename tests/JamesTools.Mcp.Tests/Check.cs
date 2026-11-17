using System.Text.Json;

namespace JamesTools.Mcp.Tests;

internal static class Check
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static T Read<T>(JsonElement value) =>
        value.Deserialize<T>(Json) ?? throw new InvalidOperationException($"Missing {typeof(T).Name} result.");

    internal static void Utc(DateTimeOffset timestamp) =>
        That(timestamp.Offset == TimeSpan.Zero, $"Timestamp must be UTC: {timestamp:O}");

    internal static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
