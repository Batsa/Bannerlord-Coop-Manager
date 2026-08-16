using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly BridgePopulationSettingsService _bridgePopulationSettingsService;
    private BannerlordModule? _selectedModule;
    private BridgePopulationSettingsTarget? _selectedBridgeSettingsTarget;
    private BridgeDllSelection? _pendingBridgeDllSelection;
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
        BridgeInstallationService bridgeInstallationService,
        BridgePopulationSettingsService? bridgePopulationSettingsService = null)
    {
        _moduleManager = moduleManager;
        _moduleImporter = moduleImporter;
        _moduleRemovalService = moduleRemovalService;
        _validator = validator;
        _compatibilityAnalyzer = compatibilityAnalyzer;
        _bridgeInstallationService = bridgeInstallationService;
        _bridgePopulationSettingsService = bridgePopulationSettingsService ??
                                           new BridgePopulationSettingsService(
                                               moduleManager.ServerRoot);

        OpenServerModulesFolderCommand = new RelayCommand(OpenServerModulesFolder);
        OpenGameModulesFolderCommand = new RelayCommand(OpenGameModulesFolder);
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
        OpenBridgeDllOptionsCommand = new RelayCommand(
            OpenBridgeDllOptions,
            CanOpenBridgeDllOptions);
        OpenBridgePopulationSettingsCommand = new RelayCommand(
            OpenBridgePopulationSettings,
            CanOpenBridgePopulationSettings);
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

            RefreshSelectedBridgeSettingsTarget();
            OnPropertyChanged(nameof(DeleteModuleHelpText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int InstalledCount => Modules.Count(module => module.IsInstalled);
    public int ActiveCount => Modules.Count(module => module.IsInstalled && module.Enabled);
    public string ServerRoot => _moduleManager.ServerRoot;
    public string DeleteModuleHelpText
    {
        get
        {
            if (IsBusy)
                return "Wait for the current server-mod operation to finish.";
            if (IsDirty)
                return "Save or rescan the pending module changes before deleting a module.";
            if (SelectedModule is null)
                return "Select an installed non-core module to delete.";

            return _moduleRemovalService.GetRemovalBlocker(SelectedModule, Modules) ??
                   (SelectedModule.Id.StartsWith(
                       CoopBridgePackageBuilder.BridgeIdPrefix,
                       StringComparison.OrdinalIgnoreCase)
                       ? "Send this inactive historical bridge folder to the Windows Recycle Bin."
                       : "Send the selected non-core module folder to the Windows Recycle Bin.");
        }
    }

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
            {
                OnPropertyChanged(nameof(DeleteModuleHelpText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DeleteModuleHelpText));
                CommandManager.InvalidateRequerySuggested();
            }
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

    public ICommand OpenServerModulesFolderCommand { get; }
    public ICommand OpenGameModulesFolderCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand InstallOrUpdateBridgeCommand { get; }
    public ICommand OpenBridgeDllOptionsCommand { get; }
    public ICommand OpenBridgePopulationSettingsCommand { get; }
    public ICommand RevertBridgeInstallationCommand { get; }

    /// <summary>
    /// Window lifetime remains in the view. The view model only publishes the
    /// completed read-only report after background analysis finishes.
    /// </summary>
    public event Action<CoopCompatibilityReport>? CompatibilityReportReady;
    public event Action<BridgeInstallationResult>? BridgeInstallationCompleted;
    public event Action<BridgeDllSelection>? BridgeDllSelectionRequested;
    public event Action<BridgePopulationSettingsTarget>? BridgePopulationSettingsRequested;

    public BridgeInstallationResult? LastBridgeInstallationResult { get; private set; }

    internal bool CanPrepareSelectedBridge => CanInstallOrUpdateBridge();
    internal bool CanOpenSelectedBridgeDllOptions => CanOpenBridgeDllOptions();
    internal bool CanOpenSelectedBridgePopulationSettings =>
        CanOpenBridgePopulationSettings();

    public Task InitializeAsync() => RescanAsync();

    private void OpenServerModulesFolder()
    {
        try
        {
            var modulesDirectory = _moduleManager.ModulesDirectory;
            if (!Directory.Exists(modulesDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Dedicated-server Modules directory was not found: {modulesDirectory}");
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = modulesDirectory,
                    UseShellExecute = true
                });
            StatusMessage = $"Opened server Modules folder: {modulesDirectory}";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Open Server Modules",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenGameModulesFolder()
    {
        try
        {
            var gameRoot = ServerExecutableLocator.FindBannerlordInstallRoot();
            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                throw new DirectoryNotFoundException(
                    "The installed Bannerlord game directory could not be found in the configured Steam libraries.");
            }

            var modulesDirectory = Path.Combine(gameRoot, "Modules");
            if (!Directory.Exists(modulesDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Bannerlord game Modules directory was not found: {modulesDirectory}");
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = modulesDirectory,
                    UseShellExecute = true
                });
            StatusMessage = $"Opened game Modules folder: {modulesDirectory}";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Open Game Modules",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public async Task ImportFoldersAsync(IReadOnlyList<string> droppedPaths)
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            if (IsDirty)
            {
                Revalidate();
                if (HasValidationErrors)
                {
                    throw new InvalidDataException(
                        "The current advanced load-order changes are invalid. Fix or rescan them before importing a mod.");
                }

                StatusMessage = "Saving advanced module changes before import...";
                await Task.Run(() => _moduleManager.Save(Modules.ToArray()));
                IsDirty = false;
            }

            var candidates = _moduleImporter.Discover(droppedPaths);
            StatusMessage = "Validating and copying module folders...";
            var imported = await Task.Run(() => _moduleImporter.Import(candidates));
            var scanned = await Task.Run(_moduleManager.Load);
            ReplaceModules(scanned);
            var importedRecipe = imported.FirstOrDefault(
                _bridgeInstallationService.IsKnownRecipe);
            if (importedRecipe is not null)
            {
                SelectedModule = Modules.FirstOrDefault(module =>
                    module.Id.Equals(importedRecipe.Id, StringComparison.OrdinalIgnoreCase));
                if (SelectedModule is not null)
                {
                    SelectedModule.PropertyChanged -= Module_PropertyChanged;
                    SelectedModule.SetInitialEnabled(false);
                    SelectedModule.PropertyChanged += Module_PropertyChanged;
                    Revalidate();
                }
            }
            var recipeName = _bridgeInstallationService.GetRecipeDisplayName(importedRecipe);
            StatusMessage = importedRecipe is null
                ? $"Imported {imported.Count} module folder(s). Enable the wanted modules, then save."
                : $"Imported {recipeName ?? importedRecipe.Name}. Choose Prepare / Install Bridge; enabling and load order are automatic.";
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
            RefreshSelectedBridgeSettingsTarget();
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

        BridgeInstallationResult? completed = null;
        IsBusy = true;
        StatusMessage = $"Preparing and installing the bridge for {module.Name}...";
        try
        {
            var snapshot = Modules.ToArray();
            var selection = _pendingBridgeDllSelection is { } pending &&
                            pending.ModuleId.Equals(module.Id, StringComparison.OrdinalIgnoreCase) &&
                            pending.ModuleVersion.Equals(module.Version, StringComparison.OrdinalIgnoreCase)
                ? pending
                : null;
            var result = await Task.Run(() => selection is null
                ? _bridgeInstallationService.InstallOrUpdate(
                    module,
                    snapshot,
                    ServerRoot)
                : _bridgeInstallationService.InstallOrUpdate(
                    module,
                    snapshot,
                    ServerRoot,
                    selection));
            StatusMessage = result.ChangesApplied
                ? "Bridge installed/updated. " +
                  (result.ClientPackagePath is null
                      ? string.Empty
                      : $"Client package: {result.ClientPackagePath}. ") +
                  $"Backup: {result.BackupDirectory}"
                : "Bridge installation is already current; no repair was needed.";
            ReplaceModules(await Task.Run(_moduleManager.Load));
            LastBridgeInstallationResult = result;
            completed = result;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Prepare / Install Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshBridgeInstallationState();
        }

        if (completed is not null)
            BridgeInstallationCompleted?.Invoke(completed);
    }

    private void OpenBridgeDllOptions()
    {
        var module = SelectedModule;
        if (module is null || !CanOpenBridgeDllOptions())
            return;

        try
        {
            var selection = _pendingBridgeDllSelection;
            if (selection is null ||
                !selection.ModuleId.Equals(module.Id, StringComparison.OrdinalIgnoreCase) ||
                !selection.ModuleVersion.Equals(module.Version, StringComparison.OrdinalIgnoreCase))
            {
                selection = _bridgeInstallationService.ResolveDllSelection(
                    module,
                    Modules.ToArray(),
                    ServerRoot);
            }
            else
            {
                selection = _bridgeInstallationService.ValidateCurrentDllSelection(
                    module,
                    selection);
            }

            _pendingBridgeDllSelection = selection;
            BridgeDllSelectionRequested?.Invoke(selection);
        }
        catch (Exception exception)
        {
            _pendingBridgeDllSelection = null;
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Open Bridge DLL Options",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public void ApplyBridgeDllSelection(BridgeDllSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var module = SelectedModule ?? throw new InvalidOperationException(
            "Select a bridge-managed module before applying DLL options.");
        if (!selection.ModuleId.Equals(module.Id, StringComparison.OrdinalIgnoreCase) ||
            !selection.ModuleVersion.Equals(module.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Bridge DLL options belong to a different selected module or version.");
        }

        _pendingBridgeDllSelection = selection;
        StatusMessage =
            $"Bridge DLL options ready: {selection.SelectedDllNames.Count} of " +
            $"{selection.AvailableDllNames.Count} included for {module.Name}. " +
            "Click Prepare / Install Bridge to apply them.";
    }

    private void OpenBridgePopulationSettings()
    {
        var module = SelectedModule;
        if (module is null || !CanOpenBridgePopulationSettings())
            return;

        try
        {
            var target = _bridgePopulationSettingsService.ValidateSelectedBridge(
                module,
                Modules.ToArray());
            _selectedBridgeSettingsTarget = target;
            BridgePopulationSettingsRequested?.Invoke(target);
        }
        catch (Exception exception)
        {
            _selectedBridgeSettingsTarget = null;
            CommandManager.InvalidateRequerySuggested();
            StatusMessage = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Could Not Open Population & Trade Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        SelectedModule is { } module &&
        _moduleRemovalService.GetRemovalBlocker(module, Modules) is null;

    private bool CanAnalyzeSelected() =>
        !IsBusy &&
        SelectedModule is { IsInstalled: true } module &&
        !string.IsNullOrWhiteSpace(module.Path);

    private bool CanInstallOrUpdateBridge() =>
        !IsBusy &&
        _bridgeInstallationService.IsKnownRecipe(SelectedModule) &&
        SelectedModule is { IsServerCompatible: true } module &&
        !string.IsNullOrWhiteSpace(module.Path);

    private bool CanOpenBridgeDllOptions() =>
        !IsBusy &&
        !IsDirty &&
        _bridgeInstallationService.IsKnownRecipe(SelectedModule) &&
        SelectedModule is { IsInstalled: true } module &&
        !string.IsNullOrWhiteSpace(module.Path);

    private bool CanOpenBridgePopulationSettings() =>
        !IsBusy &&
        !IsDirty &&
        _selectedBridgeSettingsTarget is not null;

    private void RefreshSelectedBridgeSettingsTarget()
    {
        _bridgePopulationSettingsService.TryValidateSelectedBridge(
            SelectedModule,
            Modules.ToArray(),
            out _selectedBridgeSettingsTarget);
    }

    private void Module_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BannerlordModule.Enabled))
            return;

        OnPropertyChanged(nameof(DeleteModuleHelpText));
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
        _pendingBridgeDllSelection = null;
        foreach (var module in Modules)
            module.PropertyChanged -= Module_PropertyChanged;
        Modules.Clear();

        foreach (var module in modules)
        {
            var generatedBridge = module.Id.StartsWith(
                CoopBridgePackageBuilder.BridgeIdPrefix,
                StringComparison.OrdinalIgnoreCase);
            module.SetBridgeManaged(
                generatedBridge || _bridgeInstallationService.IsKnownRecipe(module));
            module.PropertyChanged += Module_PropertyChanged;
            Modules.Add(module);
        }

        var knownRecipes = Modules
            .Where(_bridgeInstallationService.IsKnownRecipe)
            .ToArray();
        SelectedModule = knownRecipes.Length == 1
            ? knownRecipes[0]
            : Modules.FirstOrDefault();
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
