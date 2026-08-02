using System.Windows;
using System.Windows.Input;
using ETab.Helpers;

namespace ETab;

public partial class SettingsWindow : Window
{
    private bool _suppressToggle;

    public SettingsWindow()
    {
        InitializeComponent();
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = version == null ? "E-Tab" : $"E-Tab v{version.ToString(3)}";
        Loaded += (_, _) =>
        {
            _suppressToggle = true;
            AutoStartToggle.IsChecked = AutoStartManager.IsEnabled();
            _suppressToggle = false;
        };
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AutoStartManager.SetEnabled(AutoStartToggle.IsChecked == true);
    }
}
