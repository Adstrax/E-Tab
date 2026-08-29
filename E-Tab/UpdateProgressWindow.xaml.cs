using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using ETab.Helpers;
using ETab.WinAPI;

namespace ETab;

public partial class UpdateProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    public UpdateProgressWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyBackdrop();
        ContentRendered += (_, _) => ApplyBackdrop();
        Closed += (_, _) => _cts.Cancel();
    }

    public CancellationToken CancellationToken => _cts.Token;

    public void SetStatus(string phase)
    {
        if (phase == "install")
        {
            StatusText.Text = "Installing... E-Tab will restart automatically.";
            Progress.IsIndeterminate = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = "Downloading update...";
        }
    }

    public void SetProgress(long received, long total)
    {
        if (total <= 0)
        {
            Progress.IsIndeterminate = true;
            return;
        }

        Progress.IsIndeterminate = false;
        Progress.Value = Math.Min(100.0, received * 100.0 / total);
        var mb = received / (1024.0 * 1024.0);
        var totalMb = total / (1024.0 * 1024.0);
        StatusText.Text = $"Downloading update... {mb:0.0} / {totalMb:0.0} MB";
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
    }
}
