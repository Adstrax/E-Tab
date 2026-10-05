using System;
using System.IO;
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

    /// <summary>
    /// Keeps the "start with Windows" entry usable.
    ///
    /// The entry records the copy that was running when it was switched on, and
    /// that copy can be moved, deleted or replaced by a newer one. When it is
    /// gone, Windows starts nothing at login and the app simply looks like it
    /// never started - with nothing in the log to say why, because there is no
    /// process to write one. The entry is then pointed at this copy instead.
    /// </summary>
    public static void RepairIfStale()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return;

            var recorded = value.Trim().Trim('"');
            if (File.Exists(recorded)) return;

            var current = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) return;

            key!.SetValue(ValueName, GetLaunchCommand(), RegistryValueKind.String);
            Log.Info($"Start with Windows pointed at '{recorded}', which is not there any more; it now points at '{current}'.");
        }
        catch (Exception ex)
        {
            Log.Warn($"The start with Windows entry could not be checked: {ex.Message}");
        }
    }

    private static string GetLaunchCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var exeName = $"{typeof(AutoStartManager).Assembly.GetName().Name ?? "E-Tab"}.exe";
            path = Path.Combine(AppContext.BaseDirectory, exeName);
        }

        return $"\"{path}\"";
    }
}
