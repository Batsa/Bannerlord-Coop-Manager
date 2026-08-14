using BCSTool.Infrastructure;

namespace BCSTool.Models;

/// <summary>
/// Server-only population controls consumed by the generated Coop bridge.
/// Null party limits preserve Bannerlord's native behavior.
/// </summary>
public sealed class BridgePopulationSettings : BindableBase
{
    public const int DefaultAutomaticNpcCaravansPerTown = 2;

    private int? _maximumAutomaticCaravans;
    private int? _maximumActiveVillagerParties;
    private int _automaticCaravanLimit;
    private int _activeVillagerPartyLimit;
    private int _automaticNpcCaravansPerTown = DefaultAutomaticNpcCaravansPerTown;
    private double _banditPartiesAroundHideoutMultiplier = 1.0;

    public int? MaximumAutomaticCaravans
    {
        get => _maximumAutomaticCaravans;
        set
        {
            if (!SetProperty(ref _maximumAutomaticCaravans, value))
                return;

            if (value.HasValue && _automaticCaravanLimit != value.Value)
            {
                _automaticCaravanLimit = value.Value;
                OnPropertyChanged(nameof(AutomaticCaravanLimit));
            }

            OnPropertyChanged(nameof(UseNativeAutomaticCaravanLimit));
        }
    }

    public bool UseNativeAutomaticCaravanLimit
    {
        get => MaximumAutomaticCaravans is null;
        set
        {
            if (value == UseNativeAutomaticCaravanLimit)
                return;

            MaximumAutomaticCaravans = value ? null : AutomaticCaravanLimit;
        }
    }

    public int AutomaticCaravanLimit
    {
        get => _automaticCaravanLimit;
        set
        {
            if (!SetProperty(ref _automaticCaravanLimit, value))
                return;

            if (!UseNativeAutomaticCaravanLimit)
                MaximumAutomaticCaravans = value;
        }
    }

    /// <summary>
    /// Soft ceiling for future automatic NPC caravans associated with one
    /// town. Player-clan caravans remain outside this bridge limit.
    /// </summary>
    public int AutomaticNpcCaravansPerTown
    {
        get => _automaticNpcCaravansPerTown;
        set => SetProperty(ref _automaticNpcCaravansPerTown, value);
    }

    public int? MaximumActiveVillagerParties
    {
        get => _maximumActiveVillagerParties;
        set
        {
            if (!SetProperty(ref _maximumActiveVillagerParties, value))
                return;

            if (value.HasValue && _activeVillagerPartyLimit != value.Value)
            {
                _activeVillagerPartyLimit = value.Value;
                OnPropertyChanged(nameof(ActiveVillagerPartyLimit));
            }

            OnPropertyChanged(nameof(UseNativeActiveVillagerPartyLimit));
        }
    }

    public bool UseNativeActiveVillagerPartyLimit
    {
        get => MaximumActiveVillagerParties is null;
        set
        {
            if (value == UseNativeActiveVillagerPartyLimit)
                return;

            MaximumActiveVillagerParties = value ? null : ActiveVillagerPartyLimit;
        }
    }

    public int ActiveVillagerPartyLimit
    {
        get => _activeVillagerPartyLimit;
        set
        {
            if (!SetProperty(ref _activeVillagerPartyLimit, value))
                return;

            if (!UseNativeActiveVillagerPartyLimit)
                MaximumActiveVillagerParties = value;
        }
    }

    public double BanditPartiesAroundHideoutMultiplier
    {
        get => _banditPartiesAroundHideoutMultiplier;
        set => SetProperty(ref _banditPartiesAroundHideoutMultiplier, value);
    }

    public void ResetToDefaults()
    {
        MaximumAutomaticCaravans = null;
        AutomaticNpcCaravansPerTown = DefaultAutomaticNpcCaravansPerTown;
        MaximumActiveVillagerParties = null;
        BanditPartiesAroundHideoutMultiplier = 1.0;
    }
}
