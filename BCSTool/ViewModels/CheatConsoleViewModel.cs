using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using BCSTool.Infrastructure;
using BCSTool.Models;
using BCSTool.Services;

namespace BCSTool.ViewModels;

/// <summary>
/// Presentation and command-building logic for verified Coop server cheats.
/// </summary>
public sealed class CheatConsoleViewModel : BindableBase, IDisposable
{
    private readonly MainViewModel _mainViewModel;
    private readonly CoopConfigService _configService;
    private readonly CoopPlayerListParser _playerParser;
    private CheatCommandDefinition? _selectedCommand;
    private CoopPlayerIdentity? _selectedPlayer;
    private string _additionalArguments = "";
    private string _clientCheatsSelection = "Disabled";
    private string _statusMessage = "Refresh players after the server is ready.";
    private bool _isBusy;

    public CheatConsoleViewModel(
        MainViewModel mainViewModel,
        CoopConfigService configService,
        CoopPlayerListParser playerParser)
    {
        _mainViewModel = mainViewModel;
        _configService = configService;
        _playerParser = playerParser;

        Commands = CreateCommands();
        SelectedCommand = Commands.FirstOrDefault();

        RefreshPlayersCommand =
            new AsyncRelayCommand(
                RefreshPlayersAsync,
                () => !IsBusy && CanRunServerCommands);

        SaveClientCheatsCommand =
            new AsyncRelayCommand(
                SaveClientCheatsAsync,
                () => !IsBusy && CanEditClientCheats);

        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
    }

    public IReadOnlyList<CheatCommandDefinition> Commands { get; }
    public ObservableCollection<CoopPlayerIdentity> Players { get; } = new();
    public IReadOnlyList<string> ClientCheatOptions { get; } = ["Disabled", "Enabled"];

    public CheatCommandDefinition? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (!SetProperty(ref _selectedCommand, value))
                return;

            OnPropertyChanged(nameof(IsPlayerRequired));
            OnPropertyChanged(nameof(AdditionalArgumentsHint));
            OnPropertyChanged(nameof(CommandDescription));
            OnPropertyChanged(nameof(CommandPreview));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public CoopPlayerIdentity? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (!SetProperty(ref _selectedPlayer, value))
                return;

            OnPropertyChanged(nameof(CommandPreview));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string AdditionalArguments
    {
        get => _additionalArguments;
        set
        {
            if (!SetProperty(ref _additionalArguments, value))
                return;

            OnPropertyChanged(nameof(CommandPreview));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ClientCheatsSelection
    {
        get => _clientCheatsSelection;
        set => SetProperty(ref _clientCheatsSelection, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            OnPropertyChanged(nameof(CanRunServerCommands));
            OnPropertyChanged(nameof(CanEditClientCheats));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanRunServerCommands =>
        !IsBusy &&
        _mainViewModel.CanRunServerCheats;

    public bool CanEditClientCheats =>
        !IsBusy &&
        _mainViewModel.IsServerFullyStopped;

    public bool IsPlayerRequired =>
        SelectedCommand?.PlayerTarget != CheatPlayerTarget.None;

    public string CommandDescription =>
        SelectedCommand?.Description ?? "";

    public string AdditionalArgumentsHint =>
        SelectedCommand?.AdditionalArgumentsHint ?? "";

    public string CommandPreview =>
        TryBuildCommand(out var command, out _)
            ? command
            : "Select the required player and enter valid arguments.";

    public ICommand RefreshPlayersCommand { get; }
    public ICommand SaveClientCheatsCommand { get; }

    public Task InitializeAsync()
    {
        var config = _configService.LoadModConfig();

        ClientCheatsSelection =
            config.ClientsCanUseCheats
                ? "Enabled"
                : "Disabled";

        return Task.CompletedTask;
    }

    public bool TryBuildCommand(
        out string command,
        out string error)
    {
        command = "";
        error = "";

        if (SelectedCommand is null)
        {
            error = "Select a command.";
            return false;
        }

        var parts = new List<string>
        {
            SelectedCommand.Command
        };

        if (SelectedCommand.PlayerTarget != CheatPlayerTarget.None)
        {
            if (SelectedPlayer is null)
            {
                error = "Select a connected player.";
                return false;
            }

            var target = GetPlayerTarget(
                SelectedPlayer,
                SelectedCommand.PlayerTarget);

            if (string.IsNullOrWhiteSpace(target))
            {
                error =
                    $"Selected player has no {SelectedCommand.PlayerTarget} value.";

                return false;
            }

            parts.Add(target);
        }

        var arguments = AdditionalArguments.Trim();

        if (
            arguments.Contains('\r') ||
            arguments.Contains('\n'))
        {
            error = "Arguments cannot contain a new line.";
            return false;
        }

        if (
            SelectedCommand.RequiresAdditionalArguments &&
            arguments.Length == 0)
        {
            error = $"Enter {SelectedCommand.AdditionalArgumentsHint}.";
            return false;
        }

        if (arguments.Length > 0)
            parts.Add(arguments);

        command = string.Join(' ', parts);
        return true;
    }

    public async Task<bool> RunSelectedCommandAsync()
    {
        if (!TryBuildCommand(out var command, out var error))
        {
            StatusMessage = error;
            return false;
        }

        if (!CanRunServerCommands)
        {
            StatusMessage = "Server must be online and ready.";
            return false;
        }

        IsBusy = true;

        try
        {
            var sent =
                await _mainViewModel.ExecuteServerCommandAsync(
                    command);

            StatusMessage =
                sent
                    ? $"Command sent: {command}"
                    : "Command could not be sent.";

            return sent;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveClientCheatsAsync()
    {
        if (!CanEditClientCheats)
            return;

        IsBusy = true;

        try
        {
            var config = _configService.LoadModConfig();
            config.ClientsCanUseCheats =
                ClientCheatsSelection == "Enabled";

            _configService.SaveModConfig(config);

            StatusMessage =
                config.ClientsCanUseCheats
                    ? "Client cheats enabled. Restart server; each client must also run config.cheat_mode 1."
                    : "Client cheats disabled. Restart server for the change to apply.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshPlayersAsync()
    {
        if (!CanRunServerCommands)
            return;

        IsBusy = true;
        StatusMessage = "Requesting connected-player IDs...";

        try
        {
            var position = CaptureLatestLogPosition();

            if (!await _mainViewModel.ExecuteServerCommandAsync(
                    "coop.debug.players.list"))
            {
                StatusMessage = "Could not request the player list.";
                return;
            }

            var deadline = DateTime.UtcNow.AddSeconds(6);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);

                var output = ReadLogSince(position);

                if (!_playerParser.TryParse(output, out var players))
                    continue;

                Players.Clear();

                foreach (var player in players)
                    Players.Add(player);

                SelectedPlayer = Players.FirstOrDefault();

                StatusMessage =
                    Players.Count == 0
                        ? "Coop reported no connected players."
                        : $"Loaded {Players.Count} connected player(s).";

                return;
            }

            StatusMessage =
                "Player list response was not found in the server log. Try Refresh Players again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private LogPosition CaptureLatestLogPosition()
    {
        var file = GetLatestServerLog();

        return
            file is null
                ? new LogPosition(null, 0)
                : new LogPosition(file.FullName, file.Length);
    }

    private string ReadLogSince(LogPosition position)
    {
        var latest = GetLatestServerLog();

        if (latest is null)
            return "";

        var offset =
            string.Equals(
                latest.FullName,
                position.Path,
                StringComparison.OrdinalIgnoreCase)
                ? Math.Min(position.Length, latest.Length)
                : 0;

        using var stream = new FileStream(
            latest.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private FileInfo? GetLatestServerLog()
    {
        if (!Directory.Exists(_configService.ServerLogDirectory))
            return null;

        return Directory
            .EnumerateFiles(
                _configService.ServerLogDirectory,
                "*.log",
                SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void MainViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName != nameof(MainViewModel.CanRunServerCheats) &&
            e.PropertyName != nameof(MainViewModel.IsServerFullyStopped) &&
            e.PropertyName != nameof(MainViewModel.IsServerRunning))
        {
            return;
        }

        OnPropertyChanged(nameof(CanRunServerCommands));
        OnPropertyChanged(nameof(CanEditClientCheats));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string GetPlayerTarget(
        CoopPlayerIdentity player,
        CheatPlayerTarget target)
    {
        return target switch
        {
            CheatPlayerTarget.ControllerId => player.ControllerId,
            CheatPlayerTarget.HeroId => player.HeroId,
            CheatPlayerTarget.HeroName => player.PlayerName,
            CheatPlayerTarget.PartyId => player.PartyId,
            CheatPlayerTarget.ClanId => player.ClanId,
            _ => ""
        };
    }

    private static IReadOnlyList<CheatCommandDefinition> CreateCommands()
    {
        return
        [
            Command("Siege Buff", "coop.debug.mobileparty.siege_buff", "Adds troops, morale, party capacity, speed, and food.", CheatPlayerTarget.PartyId),
            Command("Add Clan Renown", "coop.debug.clan.add_renown", "Adds renown to the selected player's clan.", CheatPlayerTarget.ClanId, "renown amount", true),
            Command("Give Smithing Supplies", "coop.debug.crafting.givesupplies", "Gives smithing supplies to the selected hero.", CheatPlayerTarget.HeroName),
            Command("Set Hero Gold", "coop.debug.hero.SetGold", "Sets gold for heroes matching the selected player's name.", CheatPlayerTarget.HeroName, "gold amount", true),
            Command("Set Hero Relation", "coop.debug.hero.set_relation", "Sets relation between the selected hero and another hero.", CheatPlayerTarget.HeroId, "otherHeroId relationValue", true),
            Command("Create Kingdom", "coop.debug.kingdom.create", "Creates a kingdom led by the selected player.", CheatPlayerTarget.HeroName, "kingdom_name_with_underscores", true),
            Command("Add Skill XP", "coop.debug.herodeveloper.addskillxp", "Adds skill XP to the selected hero.", CheatPlayerTarget.HeroName, "skillName xpAmount", true),
            Command("Add Attribute Points", "coop.debug.herodeveloper.addattributepoints", "Adds attribute points to the selected hero.", CheatPlayerTarget.HeroName, "point amount", true),
            Command("Add Focus Points", "coop.debug.herodeveloper.addfocuspoints", "Adds focus points to the selected hero.", CheatPlayerTarget.HeroName, "point amount", true),
            Command("Reset Hero Skills", "coop.debug.herodeveloper.resetskills", "Resets skills for the selected hero.", CheatPlayerTarget.HeroName),
            Command("Leave Settlement", "coop.debug.mapevent.leave_settlement", "Moves the selected connected player out of a settlement.", CheatPlayerTarget.ControllerId),
            Command("Remove Companion", "coop.debug.clan.remove_companion", "Removes a companion by hero ID.", CheatPlayerTarget.None, "heroId", true),
            Command("Make Kingdoms Peace", "coop.debug.kingdom.make_peace", "Makes two factions peaceful.", CheatPlayerTarget.None, "faction1Id faction2Id", true),
            Command("Declare Kingdom War", "coop.debug.kingdom.declare_war", "Declares war between two factions.", CheatPlayerTarget.None, "faction1Id faction2Id", true),
            Command("List Kingdoms", "coop.debug.kingdom.list", "Prints kingdom IDs.", CheatPlayerTarget.None),
            Command("List Heroes", "coop.debug.hero.list", "Prints hero IDs and names.", CheatPlayerTarget.None),
            Command("List Clans", "coop.debug.clan.list", "Prints clan IDs.", CheatPlayerTarget.None),
            Command("List Towns", "coop.debug.town.list_towns", "Prints town IDs.", CheatPlayerTarget.None),
            Command("List Villages", "coop.debug.village.list", "Prints village IDs.", CheatPlayerTarget.None),
            Command("List Wanderers", "coop.debug.companions.list_wanderers", "Prints wanderer IDs and locations.", CheatPlayerTarget.None)
        ];
    }

    private static CheatCommandDefinition Command(
        string name,
        string command,
        string description,
        CheatPlayerTarget target,
        string argumentHint = "No additional arguments",
        bool requiresArguments = false)
    {
        return new CheatCommandDefinition
        {
            Name = name,
            Command = command,
            Description = description,
            PlayerTarget = target,
            AdditionalArgumentsHint = argumentHint,
            RequiresAdditionalArguments = requiresArguments
        };
    }

    public void Dispose()
    {
        _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
    }

    private sealed record LogPosition(string? Path, long Length);
}
