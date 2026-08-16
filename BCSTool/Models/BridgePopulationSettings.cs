using BCSTool.Infrastructure;

namespace BCSTool.Models;

/// <summary>
/// Server-only population controls consumed by the generated Coop bridge.
/// Null party limits preserve Bannerlord's native behavior.
/// </summary>
public sealed class BridgePopulationSettings : BindableBase
{
    public const int DefaultAutomaticNpcCaravansPerTown = 2;
    public const double DefaultCaravanCapacityMultiplier = 1.0;
    public const double DefaultCaravanTradeBudgetMultiplier = 1.0;
    public const double DefaultCaravanDestinationAgeMaxBonus = 0.0;
    public const int DefaultCaravanDestinationAgeHorizonDays = 30;
    public const double DefaultVillagerPartyCapacityMultiplier = 1.0;
    public const bool DefaultVirtualVillagerShipmentsEnabled = false;
    public const double DefaultVirtualVillagerCargoMultiplier = 1.0;
    public const int DefaultVirtualVillagerCooldownDays = 7;
    public const double DefaultVirtualVillagerTravelTimeMultiplier = 1.0;
    public const double DefaultPlayerActiveSpawnRadiusBanditTravelDays = 0.5;
    public const bool DefaultRegionalAmbientOutlawSpawnsEnabled = false;
    public const bool DefaultRegionalVillagerTradeEnabled = false;
    public const bool DefaultRegionalSettlementPatrolSpawnsEnabled = false;
    public const bool DefaultRegionalBattleDeserterSpawnsEnabled = false;

    private int? _maximumAutomaticCaravans;
    private int? _maximumActiveVillagerParties;
    private int _automaticCaravanLimit;
    private int _activeVillagerPartyLimit;
    private int _automaticNpcCaravansPerTown = DefaultAutomaticNpcCaravansPerTown;
    private double _caravanCapacityMultiplier = DefaultCaravanCapacityMultiplier;
    private double _caravanTradeBudgetMultiplier = DefaultCaravanTradeBudgetMultiplier;
    private double _caravanDestinationAgeMaxBonus =
        DefaultCaravanDestinationAgeMaxBonus;
    private int _caravanDestinationAgeHorizonDays =
        DefaultCaravanDestinationAgeHorizonDays;
    private double _villagerPartyCapacityMultiplier =
        DefaultVillagerPartyCapacityMultiplier;
    private bool _virtualVillagerShipmentsEnabled =
        DefaultVirtualVillagerShipmentsEnabled;
    private double _virtualVillagerCargoMultiplier =
        DefaultVirtualVillagerCargoMultiplier;
    private int _virtualVillagerCooldownDays = DefaultVirtualVillagerCooldownDays;
    private double _virtualVillagerTravelTimeMultiplier =
        DefaultVirtualVillagerTravelTimeMultiplier;
    private double _banditPartiesAroundHideoutMultiplier = 1.0;
    private double _playerActiveSpawnRadiusBanditTravelDays =
        DefaultPlayerActiveSpawnRadiusBanditTravelDays;
    private bool _regionalAmbientOutlawSpawnsEnabled =
        DefaultRegionalAmbientOutlawSpawnsEnabled;
    private bool _regionalVillagerTradeEnabled =
        DefaultRegionalVillagerTradeEnabled;
    private bool _regionalSettlementPatrolSpawnsEnabled =
        DefaultRegionalSettlementPatrolSpawnsEnabled;
    private bool _regionalBattleDeserterSpawnsEnabled =
        DefaultRegionalBattleDeserterSpawnsEnabled;

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

    public double CaravanCapacityMultiplier
    {
        get => _caravanCapacityMultiplier;
        set => SetProperty(ref _caravanCapacityMultiplier, value);
    }

    public double CaravanTradeBudgetMultiplier
    {
        get => _caravanTradeBudgetMultiplier;
        set => SetProperty(ref _caravanTradeBudgetMultiplier, value);
    }

    public double CaravanDestinationAgeMaxBonus
    {
        get => _caravanDestinationAgeMaxBonus;
        set => SetProperty(ref _caravanDestinationAgeMaxBonus, value);
    }

    public int CaravanDestinationAgeHorizonDays
    {
        get => _caravanDestinationAgeHorizonDays;
        set => SetProperty(ref _caravanDestinationAgeHorizonDays, value);
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

    public double VillagerPartyCapacityMultiplier
    {
        get => _villagerPartyCapacityMultiplier;
        set => SetProperty(ref _villagerPartyCapacityMultiplier, value);
    }

    public bool VirtualVillagerShipmentsEnabled
    {
        get => _virtualVillagerShipmentsEnabled;
        set => SetProperty(ref _virtualVillagerShipmentsEnabled, value);
    }

    public double VirtualVillagerCargoMultiplier
    {
        get => _virtualVillagerCargoMultiplier;
        set => SetProperty(ref _virtualVillagerCargoMultiplier, value);
    }

    public int VirtualVillagerCooldownDays
    {
        get => _virtualVillagerCooldownDays;
        set => SetProperty(ref _virtualVillagerCooldownDays, value);
    }

    public double VirtualVillagerTravelTimeMultiplier
    {
        get => _virtualVillagerTravelTimeMultiplier;
        set => SetProperty(ref _virtualVillagerTravelTimeMultiplier, value);
    }

    public double BanditPartiesAroundHideoutMultiplier
    {
        get => _banditPartiesAroundHideoutMultiplier;
        set => SetProperty(ref _banditPartiesAroundHideoutMultiplier, value);
    }

    /// <summary>
    /// Shared straight-line radius around connected authoritative player parties,
    /// expressed in average bandit travel-days.
    /// </summary>
    public double PlayerActiveSpawnRadiusBanditTravelDays
    {
        get => _playerActiveSpawnRadiusBanditTravelDays;
        set => SetProperty(ref _playerActiveSpawnRadiusBanditTravelDays, value);
    }

    public bool RegionalAmbientOutlawSpawnsEnabled
    {
        get => _regionalAmbientOutlawSpawnsEnabled;
        set => SetProperty(ref _regionalAmbientOutlawSpawnsEnabled, value);
    }

    public bool RegionalVillagerTradeEnabled
    {
        get => _regionalVillagerTradeEnabled;
        set => SetProperty(ref _regionalVillagerTradeEnabled, value);
    }

    public bool RegionalSettlementPatrolSpawnsEnabled
    {
        get => _regionalSettlementPatrolSpawnsEnabled;
        set => SetProperty(ref _regionalSettlementPatrolSpawnsEnabled, value);
    }

    public bool RegionalBattleDeserterSpawnsEnabled
    {
        get => _regionalBattleDeserterSpawnsEnabled;
        set => SetProperty(ref _regionalBattleDeserterSpawnsEnabled, value);
    }

    public void ResetToDefaults()
    {
        MaximumAutomaticCaravans = null;
        AutomaticNpcCaravansPerTown = DefaultAutomaticNpcCaravansPerTown;
        CaravanCapacityMultiplier = DefaultCaravanCapacityMultiplier;
        CaravanTradeBudgetMultiplier = DefaultCaravanTradeBudgetMultiplier;
        CaravanDestinationAgeMaxBonus = DefaultCaravanDestinationAgeMaxBonus;
        CaravanDestinationAgeHorizonDays = DefaultCaravanDestinationAgeHorizonDays;
        MaximumActiveVillagerParties = null;
        VillagerPartyCapacityMultiplier = DefaultVillagerPartyCapacityMultiplier;
        VirtualVillagerShipmentsEnabled = DefaultVirtualVillagerShipmentsEnabled;
        VirtualVillagerCargoMultiplier = DefaultVirtualVillagerCargoMultiplier;
        VirtualVillagerCooldownDays = DefaultVirtualVillagerCooldownDays;
        VirtualVillagerTravelTimeMultiplier =
            DefaultVirtualVillagerTravelTimeMultiplier;
        BanditPartiesAroundHideoutMultiplier = 1.0;
        PlayerActiveSpawnRadiusBanditTravelDays =
            DefaultPlayerActiveSpawnRadiusBanditTravelDays;
        RegionalAmbientOutlawSpawnsEnabled =
            DefaultRegionalAmbientOutlawSpawnsEnabled;
        RegionalVillagerTradeEnabled = DefaultRegionalVillagerTradeEnabled;
        RegionalSettlementPatrolSpawnsEnabled =
            DefaultRegionalSettlementPatrolSpawnsEnabled;
        RegionalBattleDeserterSpawnsEnabled =
            DefaultRegionalBattleDeserterSpawnsEnabled;
    }
}
