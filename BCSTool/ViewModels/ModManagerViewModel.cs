using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BCSTool.Infrastructure;
using BCSTool.Models;
using BCSTool.Services;

namespace BCSTool.ViewModels;

/// <summary>
/// Presentation logic for dedicated-server module selection and ordering.
/// </summary>
public sealed class ModManagerViewModel : BindableBase
{
    private readonly ModuleManager _moduleManager;
    private readonly ModuleImporter _moduleImporter;
    private readonly ModuleRemovalService _moduleRemovalService;
    private readonly DependencyValidator _validator;
    private readonly CoopCompatibilityAnalyzer _compatibilityAnalyzer;
    private readonly BridgeInstallationService _bridgeInstallationService;
    private BannerlordModule? _selectedModule;
    private string _statusMessage = "Ready to scan dedicated-server modules.";
    private string _validationSummary = string.Empty;
    private bool _isBusy;
    private bool _isDirty;
    private bool _hasValidationErrors;
    private bool _hasRevertableBridgeInstallation;

    public ModManagerViewModel(
        ModuleManager moduleManager,
        ModuleImporter moduleImporter,
        ModuleRemovalService moduleRemovalService,
        DependencyValidator validator,
        CoopCompatibilityAnalyzer compatibilityAnalyzer,
        BridgeInstallationService bridgeInstallationService)
    {
        _moduleManager = moduleManager;
        _moduleImporter = moduleImporter;
        _moduleRemovalService = moduleRemovalService;
        _validator = validator;
        _compatibilityAnalyzer = compatibilityAnalyzer;
        _bridgeInstallationService = bridgeInstallationService;

        RescanCommand = new AsyncRelayCommand(RescanAsync, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            () => !IsBusy && IsDirty && !HasValidationErrors && Modules.Count > 0);
        MoveUpCommand = new RelayCommand(MoveUp, CanMoveUp);
        MoveDownCommand = new RelayCommand(MoveDown, CanMoveDown);
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync, CanDeleteSelected);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeSelectedAsync, CanAnalyzeSelected);
        InstallOrUpdateBridgeCommand = new AsyncRelayCommand(
            InstallOrUpdateBridgeAsync,
            CanInstallOrUpdateBridge);
        RevertBridgeInstallationCommand = new AsyncRelayCommand(
            RevertLatestBridgeInstallationAsync,
            () => !IsBusy && !IsDirty && HasRevertableBridgeInstallation);
    }

    public ObservableCollection<BannerlordModule> Modules { get; } = new();

    public BannerlordModule? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (!SetProperty(ref _selectedModule, value))
                return;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int InstalledCount => Modules.Count(module => module.IsInstalled);
    public int ActiveCount => Modules.Count(module => module.IsInstalled && module.Enabled);
    public string ServerRoot => _moduleManager.ServerRoot;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasValidationErrors
    {
        get => _hasValidationErrors;
        private set
        {
            if (SetProperty(ref _hasValidationErrors, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasRevertableBridgeInstallation
    {
        get => _hasRevertableBridgeInstallation;
        private set
        {
            if (SetProperty(ref _hasRevertableBridgeInstallation, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand RescanCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand InstallOrUpdateBridgeCommand { get; }
    public ICommand RevertBridgeInstallationCommand { get; }

    /// <summary>
    /// Window lifetime remains in the view. The view model only publishes the
    /// completed read-only report after background analysis finishes.
    /// </summary>
    public event Action<CoopCompatibilityReport>? CompatibilityReportReady;

    public Task InitializeAsync() => RescanAsync();

    public async Task ImportFoldersAsync(IReadOnlyList<string> droppedPaths)
    {
        if (IsBusy)
            return;
        if (IsDirty)
        {
            MessageBox.Show(
                "Save the current module selections and load order before importing folders.",
                "Unsaved Server Mod Changes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var candidates = _moduleImporter.Discover(droppedPaths);
            var names = string.Join(Environment.NewLine, candidates.Select(candidate =>
                $"• {candidate.FolderName}"));
            var answer = MessageBox.Show(
                $"Copy these module folders into the dedicated server?{Environment.NewLine}{Environment.NewLine}" +
                names + Environment.NewLine + Environment.NewLine +
                "Existing folders will not be overwritten. Imported modules start OFF.",
                "Import Server Mods",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            StatusMessage = "Validating and copying module folders...";
            var imported = await Task.Run(() => _moduleImporter.Import(candidates));
            var scanned = await Task.Run(_moduleManager.Load);
            ReplaceModules(scanned);
            var importedEoe = imported.FirstOrDefault(
                _bridgeInstallationService.IsKnownRecipe);
            if (importedEoe is not null)
            {
                SelectedModule = Modules.FirstOrDefault(module =>
                    module.Id.Equals(importedEoe.Id, StringComparison.OrdinalIgnoreCase));
            }
            StatusMessage = importedEoe is null
                ? $"Imported {imported.Count} module folder(s). Enable the wanted modules, then save."
                : "Imported Empires of Europe 1700 and selected it. Choose Install/Update Bridge.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Import Server Mods",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshCounts();
        }
    }

    private async Task RescanAsync()
    {
        if (IsDirty)
        {
            var answer = MessageBox.Show(
                "Rescanning discards unsaved module selections and ordering. Continue?",
                "Rescan Server Mods",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        IsBusy = true;
        StatusMessage = "Scanning dedicated-server modules...";
        try
        {
            var scanned = await Task.Run(_moduleManager.Load);

            ReplaceModules(scanned);
            StatusMessage =
                $"Scanned {InstalledCount} installed dedicated-server module(s).";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            ValidationSummary = exception.Message;
            HasValidationErrors = true;
        }
        finally
        {
            IsBusy = false;
            RefreshCounts();
        }
    }

    private async Task SaveAsync()
    {
        Revalidate();
        if (HasValidationErrors)
            return;

        IsBusy = true;
        StatusMessage = "Saving BCS module selection and load order...";
        try
        {
            var snapshot = Modules.ToArray();
            await Task.Run(() => _moduleManager.Save(snapshot));
            IsDirty = false;
            StatusMessage =
                "Saved load order and enabled state. Backups use the .bak suffix.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Save Server Mods",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var module = SelectedModule;
        if (module is null || !CanDeleteSelected())
            return;

        var enabledDependents = Modules
            .Where(candidate =>
                candidate.Enabled &&
                candidate.Dependencies.Contains(module.Id, StringComparer.OrdinalIgnoreCase))
            .Select(candidate => candidate.Id)
            .ToArray();
        var dependencyWarning = enabledDependents.Length == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine +
              "Enabled modules that depend on it:" + Environment.NewLine +
              string.Join(Environment.NewLine, enabledDependents.Select(id => $"• {id}"));
        var answer = MessageBox.Show(
            $"Delete '{module.Name}' from the dedicated server?{Environment.NewLine}{Environment.NewLine}" +
            module.Path + dependencyWarning + Environment.NewLine + Environment.NewLine +
            "The folder will be sent to the Windows Recycle Bin.",
            "Delete Server Module",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        StatusMessage = $"Deleting {module.Name}...";
        try
        {
            var snapshot = Modules.ToArray();
            await Task.Run(() => _moduleRemovalService.Remove(module, snapshot));

            var oldIndex = Modules.IndexOf(module);
            module.PropertyChanged -= Module_PropertyChanged;
            Modules.Remove(module);
            SelectedModule = Modules.Count == 0
                ? null
                : Modules[Math.Min(oldIndex, Modules.Count - 1)];
            IsDirty = false;
            Revalidate();
            RefreshCounts();
            StatusMessage =
                $"Deleted {module.Name}. Its folder is available in the Windows Recycle Bin.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Delete Server Module",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AnalyzeSelectedAsync()
    {
        var module = SelectedModule;
        if (module is null || !CanAnalyzeSelected())
            return;

        CoopCompatibilityReport? report = null;
        IsBusy = true;
        StatusMessage = $"Analyzing {module.Name} without executing or modifying it...";
        try
        {
            var snapshot = Modules.ToArray();
            report = await Task.Run(() =>
                _compatibilityAnalyzer.Analyze(module, snapshot, ServerRoot));
            StatusMessage =
                $"Compatibility analysis completed: {report.StatusText}. Static results are not runtime proof.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Analyze Module",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }

        if (report is not null)
            CompatibilityReportReady?.Invoke(report);
    }

    private async Task InstallOrUpdateBridgeAsync()
    {
        var module = SelectedModule;
        if (module is null || !CanInstallOrUpdateBridge())
            return;

        IsBusy = true;
        StatusMessage = $"Building the bridge installation for {module.Name}...";
        try
        {
            var snapshot = Modules.ToArray();
            var answer = MessageBox.Show(
                "Install or update the generated EOE Coop bridge, required EOE server projections, " +
                "headless XML overlays, load order, and matching client ZIP?" +
                Environment.NewLine + Environment.NewLine +
                "Every changed file is backed up and the server will repair missing bridge-owned files before start.",
                "Install/Update Bridge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Bridge installation was cancelled; no files were changed.";
                return;
            }

            var result = await Task.Run(() =>
                _bridgeInstallationService.InstallOrUpdate(
                    module,
                    snapshot,
                    ServerRoot));
            StatusMessage = result.ChangesApplied
                ? "Bridge installed/updated. " +
                  (result.ClientPackagePath is null
                      ? string.Empty
                      : $"Client package: {result.ClientPackagePath}. ") +
                  $"Backup: {result.BackupDirectory}"
                : "Bridge installation is already current; no repair was needed.";
            MessageBox.Show(
                (result.ChangesApplied
                    ? "Bridge installed/updated and backed up."
                    : "Bridge installation is already current.") +
                " This is not runtime compatibility proof." +
                (result.ClientPackagePath is null
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine +
                      "Install this exact bridge package on every client:" +
                      Environment.NewLine + result.ClientPackagePath) +
                (result.BackupDirectory is null
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine +
                      "Reversible backup:" + Environment.NewLine + result.BackupDirectory),
                "Bridge Installation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ReplaceModules(await Task.Run(_moduleManager.Load));
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Install/Update Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshBridgeInstallationState();
        }
    }

    private async Task RevertLatestBridgeInstallationAsync()
    {
        var manifest = _bridgeInstallationService.FindLatestInstallationBackup(ServerRoot);
        if (manifest is null)
        {
            RefreshBridgeInstallationState();
            return;
        }

        var answer = MessageBox.Show(
            "Restore every file from the latest BCS compatibility backup?" +
            Environment.NewLine + Environment.NewLine + manifest,
            "Revert Bridge Installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        StatusMessage = "Verifying and reverting the latest bridge installation...";
        try
        {
            var result = await Task.Run(() =>
                _bridgeInstallationService.RevertInstallation(manifest));
            ReplaceModules(await Task.Run(_moduleManager.Load));
            StatusMessage =
                $"Reverted bridge installation {result.PlanId}; exact original files were restored.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Revert Bridge Installation",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshBridgeInstallationState();
        }
    }

    private void MoveUp()
    {
        if (SelectedModule is null)
            return;

        var index = Modules.IndexOf(SelectedModule);
        if (index <= 0)
            return;

        Modules.Move(index, index - 1);
        MarkChanged();
    }

    private void MoveDown()
    {
        if (SelectedModule is null)
            return;

        var index = Modules.IndexOf(SelectedModule);
        if (index < 0 || index >= Modules.Count - 1)
            return;

        Modules.Move(index, index + 1);
        MarkChanged();
    }

    /// <summary>
    /// Reorders one module from a drag-and-drop gesture. A null target means
    /// the pointer was released in the empty area below the list.
    /// </summary>
    public void MoveModule(
        BannerlordModule module,
        BannerlordModule? target,
        bool insertAfter)
    {
        if (IsBusy)
            return;

        var sourceIndex = Modules.IndexOf(module);
        if (sourceIndex < 0)
            return;

        var destinationIndex = Modules.Count;
        if (target is not null)
        {
            var targetIndex = Modules.IndexOf(target);
            if (targetIndex < 0)
                return;

            destinationIndex = targetIndex + (insertAfter ? 1 : 0);
        }

        if (sourceIndex < destinationIndex)
            destinationIndex--;
        if (sourceIndex == destinationIndex)
            return;

        Modules.Move(sourceIndex, destinationIndex);
        SelectedModule = module;
        MarkChanged();
    }

    private bool CanMoveUp() =>
        !IsBusy && SelectedModule is not null && Modules.IndexOf(SelectedModule) > 0;

    private bool CanMoveDown() =>
        !IsBusy && SelectedModule is not null &&
        Modules.IndexOf(SelectedModule) is var index &&
        index >= 0 && index < Modules.Count - 1;

    private bool CanDeleteSelected() =>
        !IsBusy &&
        !IsDirty &&
        SelectedModule is { IsInstalled: true, IsRequired: false };

    private bool CanAnalyzeSelected() =>
        !IsBusy &&
        SelectedModule is { IsInstalled: true } module &&
        !string.IsNullOrWhiteSpace(module.Path);

    private bool CanInstallOrUpdateBridge() =>
        !IsBusy &&
        !IsDirty &&
        _bridgeInstallationService.IsKnownRecipe(SelectedModule) &&
        SelectedModule is { IsServerCompatible: true } module &&
        !string.IsNullOrWhiteSpace(module.Path);

    private void Module_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BannerlordModule.Enabled))
            return;

        MarkChanged();
    }

    private void MarkChanged()
    {
        IsDirty = true;
        Revalidate();
        RefreshCounts();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Revalidate()
    {
        var messages = _validator.Validate(Modules.ToArray());
        HasValidationErrors = messages.Count > 0;
        ValidationSummary = messages.Count == 0
            ? "No dependency or load-order problems detected."
            : string.Join(Environment.NewLine, messages);
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(ActiveCount));
    }

    private void ReplaceModules(IReadOnlyList<BannerlordModule> modules)
    {
        foreach (var module in Modules)
            module.PropertyChanged -= Module_PropertyChanged;
        Modules.Clear();

        foreach (var module in modules)
        {
            module.PropertyChanged += Module_PropertyChanged;
            Modules.Add(module);
        }

        SelectedModule = Modules.FirstOrDefault();
        IsDirty = false;
        Revalidate();
        RefreshBridgeInstallationState();
    }

    private void RefreshBridgeInstallationState()
    {
        HasRevertableBridgeInstallation =
            _bridgeInstallationService.FindLatestInstallationBackup(ServerRoot) is not null;
    }
}
