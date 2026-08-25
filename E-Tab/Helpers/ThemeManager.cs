using System;
using System.Windows;
using Microsoft.Win32;

namespace ETab.Helpers;

/// <summary>
/// Tracks the Windows app theme (light/dark) and swaps the UI color resources
/// so both E-Tab windows follow the system immediately.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static bool _applied;

    /// <summary>Raised after the active theme changes; windows re-apply acrylic tint on this event.</summary>
    public static event Action? ThemeChanged;

    public static bool IsDark { get; private set; } = true;

    public static void Initialize(Application app)
    {
        ApplyTheme(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => ApplyTheme(app));
        };
    }

    public static void ApplyTheme(Application app)
    {
        var dark = IsSystemDarkMode();
        if (_applied && dark == IsDark)
            return;

        _applied = true;
        IsDark = dark;

        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var dictionary = new ResourceDictionary { Source = uri };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(dictionary);

        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Acrylic tint color (ABGR) for the legacy acrylic API, matching the theme.
    /// </summary>
    public static int GetAcrylicTint(bool tray)
    {
        return IsDark
            ? tray ? unchecked((int)0x402F2926) : unchecked((int)0x4C2A2420)
            : tray ? unchecked((int)0x40F8F6F4) : unchecked((int)0x4CF8F6F4);
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0;
        }
        catch
        {
            // Fall through to the dark default.
        }

        return true;
    }
}
