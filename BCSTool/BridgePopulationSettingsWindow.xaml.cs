using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BCSTool.Models;
using BCSTool.Services;

namespace BCSTool;

/// <summary>
/// Edits server-only population controls for the selected generated bridge.
/// </summary>
public partial class BridgePopulationSettingsWindow : Window
{
    private readonly BridgePopulationSettingsService _settingsService;
    private readonly BridgePopulationSettings _settings;
    private readonly BridgePopulationGuide? _populationGuide;
    private string _savedSnapshot;

    public BridgePopulationSettingsWindow(
        BridgePopulationSettingsService settingsService,
        BridgePopulationSettingsTarget target)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = settingsService.Load();
        _populationGuide = target.PopulationGuide;
        _savedSnapshot = settingsService.CreateSnapshot(_settings);
        DataContext = _settings;
        _settings.PropertyChanged += Settings_PropertyChanged;

        BridgeNameText.Text =
            $"{target.BridgeDisplayName} for {target.OverhaulDisplayName}";
        SettingsPathText.Text = settingsService.SettingsPath;
        ShowPopulationGuide(target);
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BridgePopulationSettings.AutomaticNpcCaravansPerTown))
            UpdateCaravanPerTownGuide();
    }

    private bool SaveConfiguration()
    {
        if (HasValidationErrors(this))
        {
            MessageBox.Show(
                this,
                "One or more population values are not valid. Correct the highlighted fields before saving.",
                "Bridge Population Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        try
        {
            _settingsService.Save(_settings);
            _savedSnapshot = _settingsService.CreateSnapshot(_settings);
            SaveStatusText.Text = "Saved. Changes apply after the server restarts.";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Bridge Population Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private bool HasUnsavedChanges()
    {
        try
        {
            return !_savedSnapshot.Equals(
                _settingsService.CreateSnapshot(_settings),
                StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveConfiguration();

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveConfiguration())
            return;

        DialogResult = true;
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        _settings.ResetToDefaults();
        ApplyGuideValuesToNativeFields();
        SaveStatusText.Text = "Defaults restored in the editor. Click Save to apply them.";
    }

    private void ShowPopulationGuide(BridgePopulationSettingsTarget target)
    {
        if (_populationGuide is null)
        {
            PopulationGuideSummaryText.Text = target.PopulationGuideMessage;
            PopulationGuideSourceText.Text =
                "Native behavior remains available. No custom value was inferred.";
            CaravanGuideText.Text = "Calculated native caravan baseline: unavailable.";
            UpdateCaravanPerTownGuide();
            VillagerGuideText.Text = "Calculated native villager ceiling: unavailable.";
            return;
        }

        ApplyGuideValuesToNativeFields();
        PopulationGuideSummaryText.Text =
            $"Detected {_populationGuide.TotalSettlements:N0} settlements: " +
            $"{_populationGuide.TownCount:N0} towns, " +
            $"{_populationGuide.CastleCount:N0} castles, and " +
            $"{_populationGuide.VillageCount:N0} villages.";
        PopulationGuideSourceText.Text =
            target.PopulationGuideMessage +
            " These are advisory native-scale values; imported or custom campaign state can differ.";
        CaravanGuideText.Text =
            $"Calculated native baseline: {_populationGuide.CalculatedAutomaticCaravanBaseline:N0} " +
            $"({_populationGuide.TownCount:N0} towns × 2 merchant caravans).";
        UpdateCaravanPerTownGuide();
        VillagerGuideText.Text =
            $"Calculated native ceiling: {_populationGuide.CalculatedActiveVillagerPartyCeiling:N0} " +
            $"({_populationGuide.VillageCount:N0} villages × 1 active party).";
    }

    private void UpdateCaravanPerTownGuide()
    {
        if (_populationGuide is null)
        {
            CaravanPerTownGuideText.Text =
                $"Current per-town value: {_settings.AutomaticNpcCaravansPerTown:N0}. " +
                "A total target cannot be calculated without the overhaul town count.";
            return;
        }

        var calculatedTarget = checked(
            _populationGuide.TownCount * _settings.AutomaticNpcCaravansPerTown);
        CaravanPerTownGuideText.Text =
            $"Current bridge target: {_populationGuide.TownCount:N0} towns × " +
            $"{_settings.AutomaticNpcCaravansPerTown:N0} = up to {calculatedTarget:N0} " +
            "automatic NPC caravans. Player-clan caravans are not counted.";
    }

    private void ApplyGuideValuesToNativeFields()
    {
        if (_populationGuide is null)
            return;

        if (_settings.UseNativeAutomaticCaravanLimit)
        {
            _settings.AutomaticCaravanLimit =
                _populationGuide.CalculatedAutomaticCaravanBaseline;
        }
        if (_settings.UseNativeActiveVillagerPartyLimit)
        {
            _settings.ActiveVillagerPartyLimit =
                _populationGuide.CalculatedActiveVillagerPartyCeiling;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!HasValidationErrors(this) && !HasUnsavedChanges())
            return;

        var answer = MessageBox.Show(
            this,
            "Close without saving the bridge population changes?",
            "Unsaved Bridge Population Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            e.Cancel = true;

        if (!e.Cancel)
            _settings.PropertyChanged -= Settings_PropertyChanged;
    }

    private static bool HasValidationErrors(DependencyObject root)
    {
        if (Validation.GetHasError(root))
            return true;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (HasValidationErrors(VisualTreeHelper.GetChild(root, index)))
                return true;
        }

        return false;
    }
}
