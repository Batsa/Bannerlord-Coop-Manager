using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

/// <summary>
/// UI-only lifetime behavior for the Server Mods window.
/// </summary>
public partial class ModManagerWindow : Window
{
    private readonly ModManagerViewModel _viewModel;
    private readonly BridgePopulationSettingsService _bridgePopulationSettingsService;
    private bool _allowClose;
    private Point _dragStartPoint;
    private BannerlordModule? _draggedModule;
    private ListBoxItem? _dropIndicatorItem;
    private bool _dropIndicatorAfter;
    private bool _folderDropIndicatorVisible;

    public ModManagerWindow(ModManagerViewModel viewModel)
        : this(
            viewModel,
            new BridgePopulationSettingsService(viewModel.ServerRoot))
    {
    }

    public ModManagerWindow(
        ModManagerViewModel viewModel,
        BridgePopulationSettingsService bridgePopulationSettingsService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _bridgePopulationSettingsService = bridgePopulationSettingsService;
        DataContext = viewModel;
        _viewModel.CompatibilityReportReady += ShowCompatibilityReport;
        _viewModel.BridgeInstallationCompleted += CompleteBridgeInstallation;
        _viewModel.BridgeDllSelectionRequested += ShowBridgeDllSelection;
        _viewModel.BridgePopulationSettingsRequested += ShowBridgePopulationSettings;
    }

    private void CompleteBridgeInstallation(BridgeInstallationResult result)
    {
        _allowClose = true;
        DialogResult = true;
    }

    private void ShowCompatibilityReport(CoopCompatibilityReport report)
    {
        var window = new CompatibilityReportWindow(report)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ShowBridgePopulationSettings(BridgePopulationSettingsTarget target)
    {
        try
        {
            var window = new BridgePopulationSettingsWindow(
                _bridgePopulationSettingsService,
                target)
            {
                Owner = this
            };
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could Not Open Bridge Population Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowBridgeDllSelection(BridgeDllSelection selection)
    {
        try
        {
            var displayName = _viewModel.SelectedModule?.Name ?? selection.ModuleId;
            var window = new BridgeDllSelectionWindow(
                displayName,
                selection.AvailableDllNames,
                selection.SelectedDllNames)
            {
                Owner = this
            };
            if (window.ShowDialog() == true)
            {
                _viewModel.ApplyBridgeDllSelection(
                    selection.WithSelectedDllNames(window.SelectedDllNames));
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could Not Apply Bridge DLL Options",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_viewModel.IsDirty)
        {
            _viewModel.CompatibilityReportReady -= ShowCompatibilityReport;
            _viewModel.BridgeInstallationCompleted -= CompleteBridgeInstallation;
            _viewModel.BridgeDllSelectionRequested -= ShowBridgeDllSelection;
            _viewModel.BridgePopulationSettingsRequested -= ShowBridgePopulationSettings;
            return;
        }

        var answer = MessageBox.Show(
            "Discard unsaved module selections and load-order changes?",
            "Close Server Mods",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _allowClose = true;
        _viewModel.CompatibilityReportReady -= ShowCompatibilityReport;
        _viewModel.BridgeInstallationCompleted -= CompleteBridgeInstallation;
        _viewModel.BridgeDllSelectionRequested -= ShowBridgeDllSelection;
        _viewModel.BridgePopulationSettingsRequested -= ShowBridgePopulationSettings;
    }

    private void ModuleList_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _draggedModule = null;
        _dragStartPoint = e.GetPosition(ModuleList);

        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(source);
        _draggedModule = item?.DataContext as BannerlordModule;
    }

    private void ModuleList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedModule is null)
            return;

        var position = e.GetPosition(ModuleList);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var module = _draggedModule;
        _draggedModule = null;
        var data = new DataObject(typeof(BannerlordModule), module);
        try
        {
            DragDrop.DoDragDrop(ModuleList, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
        }
    }

    private void ModuleList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ClearDropIndicator();
            ShowFolderDropIndicator();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        ClearFolderDropIndicator();
        if (!e.Data.GetDataPresent(typeof(BannerlordModule)))
        {
            ClearDropIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var (targetItem, _, insertAfter) = GetDropTarget(e);
        UpdateDropIndicator(targetItem, insertAfter);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ModuleList_DragLeave(object sender, DragEventArgs e)
    {
        if (!ModuleList.IsMouseOver)
        {
            ClearDropIndicator();
            ClearFolderDropIndicator();
        }
    }

    private async void ModuleList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
        {
            ClearDropIndicator();
            ClearFolderDropIndicator();
            e.Handled = true;
            await _viewModel.ImportFoldersAsync(droppedPaths);
            return;
        }

        if (e.Data.GetData(typeof(BannerlordModule)) is not BannerlordModule source)
            return;

        var (_, target, insertAfter) = GetDropTarget(e);

        _viewModel.MoveModule(source, target, insertAfter);
        ClearDropIndicator();
        e.Handled = true;
    }

    private void ShowFolderDropIndicator()
    {
        if (_folderDropIndicatorVisible)
            return;

        _folderDropIndicatorVisible = true;
        ModuleList.BorderBrush = SystemColors.HighlightBrush;
        ModuleList.BorderThickness = new Thickness(3);
        ModuleList.Background = SystemColors.ControlLightBrush;
    }

    private void ClearFolderDropIndicator()
    {
        if (!_folderDropIndicatorVisible)
            return;

        ModuleList.ClearValue(Control.BorderBrushProperty);
        ModuleList.ClearValue(Control.BorderThicknessProperty);
        ModuleList.ClearValue(Control.BackgroundProperty);
        _folderDropIndicatorVisible = false;
    }

    private (ListBoxItem? Item, BannerlordModule? Module, bool InsertAfter)
        GetDropTarget(DragEventArgs e)
    {
        var targetItem = e.OriginalSource is DependencyObject originalSource
            ? ItemsControl.ContainerFromElement(ModuleList, originalSource) as ListBoxItem
            : null;

        if (targetItem is null && ModuleList.Items.Count > 0)
        {
            targetItem = ModuleList.ItemContainerGenerator.ContainerFromIndex(
                ModuleList.Items.Count - 1) as ListBoxItem;
            return (
                targetItem,
                targetItem?.DataContext as BannerlordModule,
                InsertAfter: true);
        }

        var insertAfter = targetItem is not null &&
                          e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2;
        return (
            targetItem,
            targetItem?.DataContext as BannerlordModule,
            insertAfter);
    }

    private void UpdateDropIndicator(ListBoxItem? item, bool insertAfter)
    {
        if (ReferenceEquals(item, _dropIndicatorItem) &&
            insertAfter == _dropIndicatorAfter)
        {
            return;
        }

        ClearDropIndicator();
        if (item is null)
            return;

        _dropIndicatorItem = item;
        _dropIndicatorAfter = insertAfter;
        item.BorderBrush = SystemColors.HighlightBrush;
        item.BorderThickness = insertAfter
            ? new Thickness(0, 0, 0, 3)
            : new Thickness(0, 3, 0, 0);
        Panel.SetZIndex(item, 1);
    }

    private void ClearDropIndicator()
    {
        if (_dropIndicatorItem is null)
            return;

        _dropIndicatorItem.ClearValue(Control.BorderBrushProperty);
        _dropIndicatorItem.ClearValue(Control.BorderThicknessProperty);
        _dropIndicatorItem.ClearValue(Panel.ZIndexProperty);
        _dropIndicatorItem = null;
    }

    private static T? FindAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject source)
    {
        if (source is ContentElement content)
        {
            return ContentOperations.GetParent(content) ??
                   (content as FrameworkContentElement)?.Parent;
        }

        return source is Visual or Visual3D
            ? VisualTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);
    }
}
