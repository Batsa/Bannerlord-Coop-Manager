using System.Windows;
using BCSTool.ViewModels;

namespace BCSTool;

public partial class CheatConsoleWindow : Window
{
    private readonly CheatConsoleViewModel _viewModel;

    public CheatConsoleWindow(CheatConsoleViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Server Cheats",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RunCommand_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_viewModel.TryBuildCommand(out var command, out var error))
        {
            MessageBox.Show(
                this,
                error,
                "Server Cheats",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        var answer =
            MessageBox.Show(
                this,
                "Run this command against the live campaign?\n\n" +
                command +
                "\n\nThis action may not be reversible.",
                "Confirm Server Cheat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        await _viewModel.RunSelectedCommandAsync();
    }

    private void Window_Closed(
        object? sender,
        EventArgs e)
    {
        _viewModel.Dispose();
    }
}
