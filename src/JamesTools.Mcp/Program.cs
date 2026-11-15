using JamesTools.Core.Mcp;
using System.Text.Json;

namespace JamesTools.Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CommandLine.Parse(args);
            if (options.Help) { Console.Out.WriteLine(Help); return 0; }
            var configuration = ConfigurationStore.Load(options.ConfigPath);
            if (options.Enable is { } enabled)
            {
                ConfigurationStore.Save(options.ConfigPath, configuration with { Enabled = enabled });
                Console.Out.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, configPath = options.ConfigPath, enabled, restartRequiredForChanges = true }, ConfigurationStore.Json));
                return 0;
            }
            if (options.Status)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(HostStatusLease.Read(options.ConfigPath, configuration), ConfigurationStore.Json));
                return 0;
            }
            if (!configuration.Enabled) throw new McpConfigurationException("MCP is disabled. Enable it with --enable or the desktop settings.");
            // These constructors are supplied by Core; this executable owns their lifetimes.
            using var backend = new WindowsDiagnosticBackend(ConfigurationStore.AppDataDirectory);
            await using var sessions = new DiagnosticSessionService(backend, ConfigurationStore.AppDataDirectory);
            if (options.Http)
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = [], EnvironmentName = Environments.Production, ContentRootPath = AppContext.BaseDirectory
                });
                McpHost.AddDiagnostics(builder.Services, backend, sessions);
                await using var app = McpHost.BuildHttpHost(builder, configuration.Http);
                await app.StartAsync();
                using var status = new HostStatusLease(options.ConfigPath, "http", configuration.Http.Endpoint);
                await app.WaitForShutdownAsync();
            }
            else
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [], DisableDefaults = true, EnvironmentName = Environments.Production, ContentRootPath = AppContext.BaseDirectory
                });
                McpHost.AddDiagnostics(builder.Services, backend, sessions);
                using var app = McpHost.BuildStdioHost(builder, Console.OpenStandardInput(), Console.OpenStandardOutput());
                await app.StartAsync();
                using var status = new HostStatusLease(options.ConfigPath, "stdio", null);
                await app.WaitForShutdownAsync();
            }
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            // No exception detail: certificate providers and backend errors may include secrets/paths.
            var reason = ex is McpConfigurationException ? ex.Message : ex is JsonException ? "Invalid configuration JSON." :
                ex is UnauthorizedAccessException ? "Permission denied during MCP startup." : "MCP startup or shutdown failed. Check configuration and local diagnostic availability.";
            Console.Error.WriteLine(reason);
            return 1;
        }
    }

    private const string Help = """
        MehTools MCP host
        Usage: MehTools.Mcp.exe [--config <path>] [--http | --status | --enable | --disable | --help]
        Default transport: stdio (no listener, stdout reserved for MCP, logs on stderr).
        Default config: %LOCALAPPDATA%\JamesTools\Mcp\config.json (disabled if absent).
        --http     Opt in to authenticated Streamable HTTP at /mcp.
                   Requires MEHTOOLS_MCP_TOKEN (or the configured environment variable),
                   at least 32 visible ASCII bytes, including on loopback.
                   Nonloopback binding requires a configured HTTPS certificate.
        --status   Print JSON configuration and verified live-host status; do not start diagnostics.
        --enable   Persist enabled=true; does not start a host.
        --disable  Persist enabled=false; already-running hosts must be stopped separately.
        --config   Select a configuration file; does not change application data storage.
        --help     Print this help.
        See CONFIGURATION.md for fields, status JSON, limits and secure TLS proxy deployment.
        """;
}

internal sealed record CommandLine(string ConfigPath, bool Http, bool Status, bool Help, bool? Enable)
{
    public static CommandLine Parse(string[] args)
    {
        var path = ConfigurationStore.DefaultPath;
        string? command = null;
        var configSeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config")
            {
                if (configSeen || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new McpConfigurationException("--config requires exactly one path.");
                configSeen = true;
                path = Path.GetFullPath(args[i]);
            }
            else if (args[i] is "--http" or "--status" or "--help" or "--enable" or "--disable")
            {
                if (command is not null) throw new McpConfigurationException("Select only one startup or administrative option.");
                command = args[i];
            }
            else throw new McpConfigurationException("Unknown option. Use --help.");
        }
        if (path.Length > 2048) throw new McpConfigurationException("Configuration path exceeds 2048 characters.");
        return new(Path.GetFullPath(path), command == "--http", command == "--status", command == "--help", command == "--enable" ? true : command == "--disable" ? false : null);
    }
}
