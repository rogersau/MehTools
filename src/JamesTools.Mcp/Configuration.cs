using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JamesTools.Mcp;

public sealed class McpConfigurationException(string message) : ArgumentException(message);

public sealed record McpConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public HttpConfiguration Http { get; init; } = new();
}

public sealed record HttpConfiguration
{
    public string Address { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 17841;
    public string TokenEnvironmentVariable { get; init; } = "MEHTOOLS_MCP_TOKEN";
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironmentVariable { get; init; }
    public string[] AllowedHosts { get; init; } = [];
    public string[] AllowedOrigins { get; init; } = [];

    [JsonIgnore]
    public IPAddress BindAddress => IPAddress.Parse(Address);
    [JsonIgnore]
    public string Endpoint => $"{(CertificatePath is null ? "http" : "https")}://{(BindAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{BindAddress}]" : BindAddress.ToString())}:{Port}/mcp";

    public IReadOnlySet<string> GetAllowedHosts() => new HashSet<string>(
        AllowedHosts.Length > 0 ? AllowedHosts.Select(host => IPAddress.TryParse(host, out var ip) ? ip.ToString() : host) : IPAddress.IsLoopback(BindAddress) ? [BindAddress.ToString(), "localhost"] : [BindAddress.ToString()],
        StringComparer.OrdinalIgnoreCase);
}

public static class ConfigurationStore
{
    public static string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamesTools");
    public static string DefaultPath => Path.Combine(AppDataDirectory, "Mcp", "config.json");
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true,
        MaxDepth = 32
    };

    public static McpConfiguration Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 64 * 1024) throw new McpConfigurationException("Configuration exceeds 64 KiB.");
        var config = JsonSerializer.Deserialize<McpConfiguration>(file, Json) ?? throw new McpConfigurationException("Configuration must be an object.");
        Validate(config);
        return config;
    }

    public static void Validate(McpConfiguration config)
    {
        if (config.SchemaVersion != 1 || config.Http is null) throw new McpConfigurationException("Unsupported configuration schema.");
        var http = config.Http;
        if (!IPAddress.TryParse(http.Address, out var address) || address.IsIPv6Multicast || address.Equals(IPAddress.Broadcast) || http.Address.Contains('%'))
            throw new McpConfigurationException("HTTP address must be an unscoped IP literal.");
        if (http.Port is < 1024 or > 65535) throw new McpConfigurationException("HTTP port must be between 1024 and 65535.");
        ValidateEnvironmentName(http.TokenEnvironmentVariable);
        if (http.CertificatePasswordEnvironmentVariable is not null) ValidateEnvironmentName(http.CertificatePasswordEnvironmentVariable);
        if (http.CertificatePath is not null && (string.IsNullOrWhiteSpace(http.CertificatePath) || !Path.IsPathFullyQualified(http.CertificatePath)))
            throw new McpConfigurationException("Certificate path must be absolute.");
        if (!IPAddress.IsLoopback(address) && http.CertificatePath is null)
            throw new McpConfigurationException("Nonloopback HTTP requires an HTTPS certificate.");
        if (http.AllowedHosts is null || http.AllowedOrigins is null || http.AllowedHosts.Length > 16 || http.AllowedOrigins.Length > 16)
            throw new McpConfigurationException("HTTP allowlists are limited to 16 entries.");
        foreach (var host in http.AllowedHosts)
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Contains('*') ||
                (!IPAddress.TryParse(host, out _) && (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains(':') || host.EndsWith('.'))))
                throw new McpConfigurationException("Allowed hosts must be exact DNS names or IP literals.");
        foreach (var origin in http.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || origin.Length > 512 ||
                uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.UserInfo.Length != 0 || origin != uri.GetLeftPart(UriPartial.Authority) ||
                (!IPAddress.IsLoopback(address) && uri.Scheme != "https"))
                throw new McpConfigurationException("Allowed origins must be exact HTTP(S) origins.");
        }
    }

    private static void ValidateEnvironmentName(string value)
    {
        if (value is null || !Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant))
            throw new McpConfigurationException("Invalid environment variable name.");
    }

    public static void Save(string path, McpConfiguration config)
    {
        Validate(config);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".mcp-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                PrivateFiles.RestrictFile(temporary);
                JsonSerializer.Serialize(file, config, Json);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
            PrivateFiles.RestrictFile(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class PrivateFiles
{
    public static void RestrictDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    public static void RestrictFile(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
