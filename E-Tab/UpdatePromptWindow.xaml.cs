using System;
using System.Windows;
using System.Windows.Interop;
using ETab.Helpers;
using ETab.WinAPI;

namespace ETab;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow(ReleaseInfo release)
    {
        InitializeComponent();

        MessageText.Text =
            $"E-Tab {release.Version.ToString(3)} is available" +
            $" (current: {UpdateManager.CurrentVersion.ToString(3)}).\n\n" +
            "Update now? The app will restart automatically after installing.";

        SourceInitialized += (_, _) => ApplyBackdrop();
        ContentRendered += (_, _) => ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;

        WinApi.MakeWindowBackdropVisible(this);
        WinApi.ExtendGlassFrame(handle);
        WinApi.ApplyRoundedCorners(handle);
        WinApi.ApplyLegacyAcrylic(handle, ThemeManager.GetAcrylicTint(tray: false));
    }

    private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
