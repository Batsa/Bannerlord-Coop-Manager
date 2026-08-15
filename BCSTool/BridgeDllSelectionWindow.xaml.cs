using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace BCSTool;

/// <summary>
/// Collects an in-memory DLL selection for a target mod bridge.
/// The caller remains responsible for persistence and bridge generation.
/// </summary>
public partial class BridgeDllSelectionWindow : Window
{
    private readonly ReadOnlyCollection<BridgeDllSelectionItem> _dllOptions;
    private bool _updatingAllOptions;

    public BridgeDllSelectionWindow(
        string modDisplayName,
        IEnumerable<string> declaredDllNames,
        IEnumerable<string>? initiallySelectedDllNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDisplayName);
        ArgumentNullException.ThrowIfNull(declaredDllNames);

        var declaredNames = NormalizeNames(declaredDllNames, nameof(declaredDllNames));
        if (declaredNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one declared DLL name is required.",
                nameof(declaredDllNames));
        }

        var selectedNames = initiallySelectedDllNames is null
            ? declaredNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : NormalizeNames(
                    initiallySelectedDllNames,
                    nameof(initiallySelectedDllNames))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknownSelectedNames = selectedNames
            .Except(declaredNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownSelectedNames.Length > 0)
        {
            throw new ArgumentException(
                "Initial selection contains DLLs that are not declared: " +
                string.Join(", ", unknownSelectedNames),
                nameof(initiallySelectedDllNames));
        }

        _dllOptions = declaredNames
            .Select(name => new BridgeDllSelectionItem(
                name,
                selectedNames.Contains(name)))
            .ToList()
            .AsReadOnly();

        foreach (var option in _dllOptions)
            option.PropertyChanged += DllOption_PropertyChanged;

        SelectedDllNames = CreateSelectionSnapshot();

        InitializeComponent();
        TargetModText.Text = modDisplayName;
        DllList.ItemsSource = _dllOptions;
        UpdateSelectionSummary();
    }

    /// <summary>
    /// Immutable selection snapshot. It changes only after Apply succeeds.
    /// </summary>
    public ReadOnlyCollection<string> SelectedDllNames { get; private set; }

    private static ReadOnlyCollection<string> NormalizeNames(
        IEnumerable<string> names,
        string parameterName)
    {
        var normalizedNames = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "DLL names cannot be null, empty, or whitespace.",
                    parameterName);
            }

            var normalizedName = name.Trim();
            if (seenNames.Add(normalizedName))
                normalizedNames.Add(normalizedName);
        }

        return normalizedNames.AsReadOnly();
    }

    private ReadOnlyCollection<string> CreateSelectionSnapshot()
    {
        return _dllOptions
            .Where(option => option.IsSelected)
            .Select(option => option.DllName)
            .ToList()
            .AsReadOnly();
    }

    private void DllOption_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!_updatingAllOptions &&
            e.PropertyName == nameof(BridgeDllSelectionItem.IsSelected))
        {
            UpdateSelectionSummary();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        SetAllOptions(isSelected: true);

    private void ClearAll_Click(object sender, RoutedEventArgs e) =>
        SetAllOptions(isSelected: false);

    private void SetAllOptions(bool isSelected)
    {
        _updatingAllOptions = true;
        try
        {
            foreach (var option in _dllOptions)
                option.IsSelected = isSelected;
        }
        finally
        {
            _updatingAllOptions = false;
        }

        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var selectedCount = _dllOptions.Count(option => option.IsSelected);
        SelectionSummaryText.Text =
            $"{selectedCount:N0} of {_dllOptions.Count:N0} DLLs selected";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedDllNames = CreateSelectionSnapshot();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private sealed class BridgeDllSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public BridgeDllSelectionItem(string dllName, bool isSelected)
        {
            DllName = dllName;
            _isSelected = isSelected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DllName { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
