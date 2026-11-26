using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Label = System.Windows.Controls.Label;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace JamesTools;

/// <summary>Edits the standalone host's configuration; never hosts MCP in the desktop process.</summary>
public sealed class McpSettingsWindow : Window
{
    private readonly string _dataDirectory;
    private readonly string _hostPath = Path.Combine(AppContext.BaseDirectory, "tools", "mcp", "MehTools.Mcp.exe");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CheckBox _enabled = new() { Content = "Enable MCP connections" };
    private readonly TextBox _address = new();
    private readonly TextBox _port = new();
    private readonly TextBox _tokenVariable = new();
    private readonly TextBox _certificate = new();
    private readonly TextBox _passwordVariable = new();
    private readonly TextBox _hosts = new();
    private readonly TextBox _origins = new();
    private readonly TextBlock _configured = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _running = new() { Text = "Status has not been checked.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _command = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _editor = new();
    private readonly WrapPanel _actions = new();
    private string ConfigPath => Path.Combine(_dataDirectory, "Mcp", "config.json");
    private string? _loadedText;
    private bool _loaded;
    private bool _busy;
    private bool _closed;

    public McpSettingsWindow(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        WindowTheme.Apply(this);
        Title = "MCP configuration & status";
        Width = 740; Height = 800; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = layout;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock { Text = "Connect an MCP client", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(Hint("Give a client access to this PC's diagnostics. Opening this window does not start a host or a network listener."));
        layout.Children.Add(heading);
        var body = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);

        var configuration = Card(body, "Connect via stdio");
        configuration.Children.Add(_editor);
        _editor.Children.Add(_enabled);
        _editor.IsEnabled = false;
        configuration.Children.Add(_actions);
        configuration.Children.Add(_command);
        _command.Text = Command(false);
        AutomationProperties.SetName(_command, "Stdio connection command");
        AddButton(configuration, "Copy connection command", () => Copy(Command(false)));
        configuration.Children.Add(Hint("Restart your MCP client after saving. For separate command and argument fields, use the executable above and pass --config and the file path as two arguments."));

        var httpSettings = new StackPanel();
        _editor.Children.Add(new Expander { Header = "Optional HTTP settings", Content = httpSettings, Padding = new Thickness(0, 7, 0, 0), Margin = new Thickness(0, 10, 0, 0) });
        httpSettings.Children.Add(Hint("These settings apply when a host is explicitly started with --http."));
        Field(httpSettings, "HTTP bind address", _address);
        Field(httpSettings, "HTTP port", _port);
        AddSecurityHelp(httpSettings);
        AddButton(httpSettings, "Copy HTTP command", () => Copy(Command(true)));

        var status = new StackPanel();
        body.Children.Add(new Expander { Header = "Saved configuration and running status", Content = status, Padding = new Thickness(0, 7, 0, 0) });
        status.Children.Add(_configured);
        status.Children.Add(_running);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
        footer.Children.Add(_notice);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        ConfigureActions(status);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        Loaded += async (_, _) => await LoadAsync();
    }

    private static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 10) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return hint;
    }

    private static StackPanel Card(Panel parent, string title)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var border = new Border { Child = content };
        border.SetResourceReference(StyleProperty, "Card");
        parent.Children.Add(border);
        return content;
    }

    private static void Field(Panel parent, string label, TextBox input)
    {
        parent.Children.Add(new Label { Content = label, Target = input, Padding = new Thickness(0), Margin = new Thickness(0, 10, 0, 5) });
        AutomationProperties.SetName(input, label);
        parent.Children.Add(input);
    }

    private void AddSecurityHelp(Panel panel)
    {
        panel.Children.Add(Hint("HTTP always requires a bearer token of at least 32 UTF-8 bytes in the host process's environment. Enter environment variable names below, never tokens or passwords. Stdio does not require an HTTP token."));
        Field(panel, "Bearer token environment variable name", _tokenVariable);
        Field(panel, "TLS certificate path (.pfx; optional on loopback)", _certificate);
        Field(panel, "Certificate password environment variable name (optional)", _passwordVariable);
        Field(panel, "Allowed HTTP hosts (one exact name/IP per line; optional)", _hosts);
        Field(panel, "Allowed browser origins (one exact origin per line; optional)", _origins);
        foreach (var input in new[] { _hosts, _origins })
        {
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 60; input.MaxHeight = 110;
            input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        panel.Children.Add(Hint("Loopback (127.0.0.1 or ::1) is the default. A nonloopback bind requires a PFX certificate and HTTPS. To publish loopback HTTP through a TLS proxy, retain bearer authentication and allow the proxy's exact Host. Never send bearer tokens over public plaintext HTTP."));
        panel.Children.Add(Hint("Blank allowed hosts uses the bind address (plus localhost for loopback). Blank allowed origins rejects requests containing Origin. Add a browser origin only when intentionally authorizing it. Changing environment variables also requires restarting the host with the new environment."));
    }

    private string Command(bool http) => $"\"{_hostPath}\" --config \"{ConfigPath}\"" + (http ? " --http" : "");

    private void Copy(string command)
    {
        try { System.Windows.Clipboard.SetText(command); _notice.Text = "Command copied. Nothing was started."; }
        catch (Exception ex) { _notice.Text = "Could not copy the command: " + ex.Message; }
    }

    private void ConfigureActions(Panel status)
    {
        var refresh = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        status.Children.Add(refresh);
        AddButton(refresh, "Refresh running status", async () => await RefreshStatusAsync());
        _actions.Margin = new Thickness(0, 12, 0, 0);
        var save = AddButton(_actions, "Save configuration", async () => await SaveAsync());
        save.SetResourceReference(StyleProperty, "AccentButton");
        AddButton(refresh, "Reload saved configuration", async () => await LoadAsync());
    }

    private static Button AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 6, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }

    private async Task LoadAsync()
    {
        if (_busy || _closed) return;
        _busy = true; _loaded = false; _editor.IsEnabled = false; _actions.IsEnabled = false;
        try
        {
            var text = await Task.Run(() => ReadConfiguration(ConfigPath), _lifetime.Token);
            if (_closed) return;
            var document = ParseConfiguration(text);
            var http = HttpSettings(document);
            _enabled.IsChecked = Read(document, "enabled", false);
            _address.Text = Read(http, "address", "127.0.0.1");
            _port.Text = Read(http, "port", 17841).ToString(CultureInfo.InvariantCulture);
            _tokenVariable.Text = Read(http, "tokenEnvironmentVariable", "MEHTOOLS_MCP_TOKEN");
            _certificate.Text = Read(http, "certificatePath", "");
            _passwordVariable.Text = Read(http, "certificatePasswordEnvironmentVariable", "");
            _hosts.Text = string.Join(Environment.NewLine, ReadList(http, "allowedHosts"));
            _origins.Text = string.Join(Environment.NewLine, ReadList(http, "allowedOrigins"));
            _loadedText = text; _loaded = true; UpdateConfigured(document);
            _notice.Text = text is null ? "No configuration file yet. Defaults are disabled; Save creates the file." : "Saved configuration loaded. Nothing was started.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { _configured.Text = "Configuration could not be read. Editing is disabled to protect the existing file."; _notice.Text = ex.Message; }
        finally { _busy = false; if (!_closed) { _editor.IsEnabled = _loaded; _actions.IsEnabled = true; } }
        if (!_closed) await RefreshStatusAsync();
    }

    private void UpdateConfigured(JsonObject document)
    {
        var http = HttpSettings(document);
        _configured.Text = $"Saved: {(Read(document, "enabled", false) ? "enabled" : "disabled")}. Default transport: stdio. Optional HTTP bind: {Read(http, "address", "127.0.0.1")}:{Read(http, "port", 17841)}. These are startup settings, not live status.";
    }

    private async Task SaveAsync()
    {
        if (_busy || _closed) return;
        if (!_loaded) { _notice.Text = "Reload a valid configuration before saving."; return; }
        _busy = true; _editor.IsEnabled = false; _actions.IsEnabled = false;
        try
        {
            var document = ParseConfiguration(_loadedText);
            document["schemaVersion"] = 1;
            document["enabled"] = _enabled.IsChecked == true;
            var http = HttpSettings(document);
            if (document["http"] is null) document["http"] = http;
            if (!IPAddress.TryParse(_address.Text.Trim(), out var address) || address.IsIPv6Multicast || address.Equals(IPAddress.Broadcast) || _address.Text.Contains('%'))
                throw new InvalidDataException("HTTP address must be an unscoped IP literal, such as 127.0.0.1 or ::1. Multicast and broadcast addresses are not allowed.");
            if (!int.TryParse(_port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
                throw new InvalidDataException("HTTP port must be between 1024 and 65535.");
            var tokenVariable = VariableName(_tokenVariable.Text, false);
            var passwordVariable = VariableName(_passwordVariable.Text, true);
            var certificate = _certificate.Text.Trim();
            if (!IPAddress.IsLoopback(address) && certificate.Length == 0)
                throw new InvalidDataException("Nonloopback HTTP requires a PFX certificate and HTTPS. Use a loopback address or provide the certificate path.");
            if (certificate.Length > 0 && !Path.IsPathFullyQualified(certificate))
                throw new InvalidDataException("Use the full path to the PFX certificate file. The certificate is verified only when the HTTP host starts.");
            var hosts = Lines(_hosts.Text);
            if (hosts.Length > 16) throw new InvalidDataException("Allowed hosts are limited to 16 entries.");
            foreach (var host in hosts)
                if (host.Length > 253 || host.Contains('*') || (!IPAddress.TryParse(host, out _) && (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains(':') || host.EndsWith('.'))))
                    throw new InvalidDataException("Allowed hosts must be exact DNS names or IP literals, without schemes, ports, or wildcards.");
            var origins = Lines(_origins.Text);
            if (origins.Length > 16) throw new InvalidDataException("Allowed browser origins are limited to 16 entries.");
            foreach (var origin in origins)
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                    uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                    origin.Length > 512 || origin != uri.GetLeftPart(UriPartial.Authority) || (!IPAddress.IsLoopback(address) && uri.Scheme != "https"))
                    throw new InvalidDataException("Allowed origins must be exact HTTP(S) origins, without credentials, paths, queries, fragments, or wildcards. Nonloopback origins require HTTPS.");
            }
            http["address"] = address.ToString(); http["port"] = port;
            http["tokenEnvironmentVariable"] = tokenVariable;
            http["certificatePath"] = certificate.Length == 0 ? null : certificate;
            http["certificatePasswordEnvironmentVariable"] = passwordVariable;
            http["allowedHosts"] = new JsonArray(hosts.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            http["allowedOrigins"] = new JsonArray(origins.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            var updatedText = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            if (Encoding.UTF8.GetByteCount(updatedText) > 65536) throw new InvalidDataException("MCP configuration exceeds the host's 64 KiB size limit.");
            var expectedText = _loadedText;
            await Task.Run(() => WriteConfiguration(ConfigPath, expectedText, updatedText), _lifetime.Token);
            if (_closed) return;
            _loadedText = updatedText; UpdateConfigured(document);
            _notice.Text = "Configuration saved. Restart every running host/client to apply changes. Nothing was started or stopped.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _notice.Text = "Could not save configuration: " + ex.Message; }
        finally { _busy = false; if (!_closed) { _editor.IsEnabled = _loaded; _actions.IsEnabled = true; } }
    }

    private static string? VariableName(string text, bool optional)
    {
        var name = text.Trim();
        if (optional && name.Length == 0) return null;
        if (name.Length is < 1 or > 128 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Enter an environment variable name using letters, numbers and underscores, starting with a letter or underscore. Never enter the secret itself.");
        return name;
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();

    private static string? ReadConfiguration(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 65536) throw new InvalidDataException("MCP configuration exceeds the host's 64 KiB size limit.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static JsonObject ParseConfiguration(string? text)
    {
        var document = text is null ? new JsonObject() : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException("MCP configuration must be a JSON object.");
        if (Read(document, "schemaVersion", 1) != 1) throw new InvalidDataException("This MCP configuration version is not supported by the editor.");
        return document;
    }

    private static JsonObject HttpSettings(JsonObject document) => document["http"] is null ? new JsonObject() : document["http"] as JsonObject ?? throw new InvalidDataException("HTTP configuration must be a JSON object.");
    private static T Read<T>(JsonObject document, string key, T fallback) => document[key] is { } value ? value.GetValue<T>() : fallback;
    private static string[] ReadList(JsonObject document, string key) => document[key] is null ? [] : document[key] is JsonArray list ? list.Select(value => value?.GetValue<string>() ?? throw new InvalidDataException("Allowed hosts and origins must contain strings.")).ToArray() : throw new InvalidDataException("Allowed hosts and origins must be JSON arrays.");

    private static void WriteConfiguration(string path, string? expectedText, string updatedText)
    {
        if (ReadConfiguration(path) != expectedText)
            throw new IOException("The configuration changed outside this window. Reload saved configuration and reapply your changes.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".config-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(updatedText);
                stream.Write(bytes); stream.Flush(true);
            }
            if (ReadConfiguration(path) != expectedText)
                throw new IOException("The configuration changed while saving. Reload saved configuration and reapply your changes.");
            if (expectedText is null) File.Move(temporary, path);
            else File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RefreshStatusAsync()
    {
        if (_busy || _closed) return;
        _busy = true;
        _actions.IsEnabled = false;
        _running.Text = "Checking the standalone host's status…";
        try
        {
            if (!File.Exists(_hostPath))
            {
                _running.Text = "Running status unavailable: the packaged MCP host was not found at " + _hostPath + ". Configuration remains editable. Install a package containing the host to check live status.";
                return;
            }
            var output = await QueryStatusAsync(_lifetime.Token);
            if (_closed) return;
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported host status version.");
            var hosts = root.GetProperty("hosts").EnumerateArray().ToArray();
            var status = new StringBuilder(hosts.Length == 0 ? "No running host was verified for this configuration." : $"{hosts.Length} running host(s) verified:");
            foreach (var host in hosts)
            {
                status.AppendLine().Append("• PID ").Append(host.GetProperty("processId").GetInt32())
                    .Append(" · ").Append(host.GetProperty("transport").GetString());
                if (host.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.String)
                    status.Append(" · ").Append(endpoint.GetString());
                status.Append(" · started ").Append(host.GetProperty("startedUtc").GetString());
            }
            var warnings = root.GetProperty("warnings");
            if (warnings.GetArrayLength() > 0)
            {
                status.AppendLine().Append("Some hosts could not be verified. Treat this status as incomplete.");
                foreach (var warning in warnings.EnumerateArray()) status.AppendLine().Append("• ").Append(warning.GetString());
            }
            status.AppendLine().Append("Checked ").Append(DateTime.Now.ToString("T", CultureInfo.CurrentCulture)).Append(". Saved configuration may differ from each running host.");
            _running.Text = status.ToString();
        }
        catch (OperationCanceledException) { if (!_closed) _running.Text = "Running status unavailable: the status check exceeded its time limit."; }
        catch (Exception ex) { if (!_closed) _running.Text = "Running status unavailable. No running state can be inferred. " + ex.Message; }
        finally { _busy = false; if (!_closed) _actions.IsEnabled = true; }
    }

    private async Task<string> QueryStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(_hostPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_hostPath)!
        } };
        process.StartInfo.ArgumentList.Add("--status");
        process.StartInfo.ArgumentList.Add("--config"); process.StartInfo.ArgumentList.Add(ConfigPath);
        if (!process.Start()) throw new IOException("The MCP status process could not be started.");
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout);
        var stderr = ReadBoundedAsync(process.StandardError, timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr; // Drain both streams concurrently; never display potentially sensitive stderr.
            if (process.ExitCode != 0) throw new InvalidDataException("The host rejected the status request. Check configuration against the installed host's CONFIGURATION.md (and reload after correcting it).");
            return output;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } catch (InvalidDataException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource timeout)
    {
        var result = new StringBuilder(); var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token);
            if (count == 0) return result.ToString();
            if (result.Length + count > 32768) { timeout.Cancel(); throw new InvalidDataException("The MCP status response exceeded the 32 KiB output limit."); }
            result.Append(buffer, 0, count);
        }
    }
}
