using System;
using Microsoft.Win32;

namespace ETab.Helpers;

public static class Settings
{
    private const string KeyPath = @"Software\E-Tab";

    public static bool AutoMerge
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                var value = key?.GetValue("AutoMerge");
                return value is int i ? i != 0 : true;
            }
            catch
            {
                return true;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
                if (key != null)
                    key.SetValue("AutoMerge", value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch
            {
                // Settings persistence is best effort.
            }
        }
    }
}