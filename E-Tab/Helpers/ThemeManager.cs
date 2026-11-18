using Microsoft.Win32;

namespace ETab.Helpers;

/// <summary>
/// Tracks the Windows app theme (light/dark) for the custom tray menu. The
/// notification icon itself follows the system, so this only caches the value
/// needed by the menu palette and refreshes it when Windows changes themes.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; } = true;

    public static void Initialize()
    {
        IsDark = IsSystemDarkMode();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
                IsDark = IsSystemDarkMode();
        };
    }

    public static bool IsSystemDarkMode()
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
