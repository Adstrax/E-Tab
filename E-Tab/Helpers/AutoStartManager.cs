using System;
using Microsoft.Win32;

namespace ETab.Helpers;

public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "E-Tab";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(ValueName) as string;
        return string.Equals(value, GetLaunchCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (key == null) return;

        if (enabled)
            key.SetValue(ValueName, GetLaunchCommand(), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, false);
    }

    private static string GetLaunchCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            path = typeof(AutoStartManager).Assembly.Location;

        return $"\"{path}\"";
    }
}
