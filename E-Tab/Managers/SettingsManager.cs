using ETab.Models;
using Microsoft.Win32;

namespace ETab.Managers;

/// <summary>
/// The few settings the conversion needs. Everything is kept in the same
/// registry key the rest of this app uses, and every read falls back to a
/// sensible default: a setting must never be able to stop the app starting.
/// </summary>
public static class SettingsManager
{
    private const string KeyPath = @"Software\E-Tab";

    /// <summary>
    /// Whether a folder that is opened in a window of its own is moved into a
    /// tab of the window folders gather in. On by default: it is the whole point
    /// of the app.
    /// </summary>
    public static bool AutoMerge
    {
        get => ReadBool("AutoMerge", true);
        set => WriteBool("AutoMerge", value);
    }

    /// <summary>
    /// True when Windows itself is drawing window frames in a way that a hidden
    /// window cannot be made invisible without a visible flicker, in which case
    /// the window is moved out of the way instead.
    /// </summary>
    public static bool HaveThemeIssue
    {
        get => ReadBool("HaveThemeIssue", false);
        set => WriteBool("HaveThemeIssue", value);
    }

    /// <summary>Whether the folders that were open last time are opened again.</summary>
    public static bool RestorePreviousWindows
    {
        get => ReadBool("RestorePreviousWindows", false);
        set => WriteBool("RestorePreviousWindows", value);
    }

    /// <summary>Whether the windows that were closed are remembered.</summary>
    public static bool SaveClosedHistory
    {
        get => ReadBool("SaveClosedHistory", false);
        set => WriteBool("SaveClosedHistory", value);
    }

    /// <summary>
    /// The windows that were open when the app last went away, so a restart can
    /// bring them back. Only ever read when <see cref="RestorePreviousWindows"/>
    /// is on.
    /// </summary>
    public static WindowRecord[]? ClosedWindows { get; set; }

    private static bool ReadBool(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var value = key?.GetValue(name);
            return value is int i ? i != 0 : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteBool(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Settings persistence is best effort.
        }
    }
}
