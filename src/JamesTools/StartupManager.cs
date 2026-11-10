using System.Reflection;
using Microsoft.Win32;

namespace JamesTools;

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "JamesTools";

    public static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(EntryName) as string;
    }

    public static string CurrentCommand()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the application executable.");
        var entry = Assembly.GetEntryAssembly()?.Location;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{process}\" \"{entry}\" --background"
            : $"\"{process}\" --background";
    }

    public static void WriteCommand(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open Windows startup settings.");
        if (string.IsNullOrEmpty(command)) key.DeleteValue(EntryName, throwOnMissingValue: false);
        else key.SetValue(EntryName, command, RegistryValueKind.String);
    }
}
