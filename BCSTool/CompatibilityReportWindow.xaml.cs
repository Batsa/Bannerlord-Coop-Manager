using System.Windows;
using BCSTool.Models;

namespace BCSTool;

/// <summary>
/// Display-only shell for a completed static compatibility report.
/// </summary>
public partial class CompatibilityReportWindow : Window
{
    private readonly CoopCompatibilityReport _report;

    public CompatibilityReportWindow(CoopCompatibilityReport report)
    {
        InitializeComponent();
        _report = report;
        DataContext = report;
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report.ToPlainText());
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Could Not Copy Report",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
