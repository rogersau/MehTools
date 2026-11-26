using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace JamesTools.Mcp;

internal static class ToolRegistration
{
    public static void AddTools(IServiceCollection services)
    {
        foreach (var method in typeof(DiagnosticTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            services.AddSingleton(provider => McpServerTool.Create(method,
                new DiagnosticTools(provider.GetRequiredService<DiagnosticAccess>()),
                new McpServerToolCreateOptions
                {
                    Services = provider,
                    SerializerOptions = DiagnosticAccess.Json,
                    SchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        TransformSchemaNode = (context, schema) =>
                        {
                            // Inventory properties explicitly allow null string values. The SDK cannot
                            // recover generic-value nullability here (string and string? share a runtime
                            // type), so preserve that contract for string-valued dictionaries. Investigation
                            // detail dictionaries use the same schema; persisted evidence validates nonnull values.
                            if (context.TypeInfo.Type == typeof(IReadOnlyDictionary<string, string?>) && schema is JsonObject dictionarySchema &&
                                dictionarySchema["additionalProperties"] is JsonObject valueSchema)
                                valueSchema["type"] = new JsonArray("string", "null");
                            return schema;
                        }
                    }
                }));
        }
    }
}
