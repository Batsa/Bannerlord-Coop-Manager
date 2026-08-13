using BCSTool.Infrastructure;

namespace BCSTool.Models;

/// <summary>
/// One module known to the dedicated-server launcher.
/// </summary>
public sealed class BannerlordModule : BindableBase
{
    private bool _enabled;
    private IReadOnlyList<string> _validationMessages = Array.Empty<string>();

    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Path { get; init; }
    public required bool IsInstalled { get; init; }
    public required bool IsRequired { get; init; }
    public required bool IsServerCompatible { get; init; }
    public required IReadOnlyList<string> Dependencies { get; init; }
    public required IReadOnlyList<string> MustLoadAfter { get; init; }
    public required IReadOnlyList<string> MustLoadBefore { get; init; }
    public required IReadOnlyList<string> IncompatibleModules { get; init; }

    public bool CanToggle => IsServerCompatible && !IsRequired;
    public string ToggleHelpText =>
        IsRequired
            ? "Required by the Bannerlord Coop dedicated server and cannot be disabled."
            : !IsServerCompatible
                ? "This official module is not compatible with the dedicated server."
                : "Enable or disable this dedicated-server module.";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!CanToggle || !SetProperty(ref _enabled, value))
                return;

            OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText =>
        !IsInstalled
            ? "Missing"
            : IsRequired
                ? "REQUIRED"
            : Enabled
                ? "ON"
                : "OFF";

    public IReadOnlyList<string> ValidationMessages
    {
        get => _validationMessages;
        private set
        {
            if (!SetProperty(ref _validationMessages, value))
                return;

            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(WarningText));
        }
    }

    public bool HasWarnings => ValidationMessages.Count > 0;
    public string WarningText => string.Join(Environment.NewLine, ValidationMessages);

    internal void SetInitialEnabled(bool enabled)
    {
        _enabled = IsRequired || (IsServerCompatible && enabled);
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(StateText));
    }

    internal void SetValidationMessages(IEnumerable<string> messages)
    {
        ValidationMessages = messages.Distinct(StringComparer.Ordinal).ToArray();
    }
}
