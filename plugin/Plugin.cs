using System;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using VentureBell.Windows;

namespace VentureBell;

public sealed class Plugin : IDalamudPlugin
{
    // ── Injected services ─────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle         AddonLifecycle  { get; private set; } = null!;
    [PluginService] internal static IPlayerState            PlayerState     { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;

    internal Configuration   Config   { get; }
    internal VentureResolver Resolver { get; private set; } = null!;

    private readonly RetainerReader      _reader;
    private readonly BellClient          _client;
    private readonly VentureSync         _sync;
    private readonly ConfigurationWindow _configWindow;
    private readonly VentureWindow       _ventureWindow;

    private const string CmdMain = "/venturebell";

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Config.Migrate())
            PluginInterface.SavePluginConfig(Config);

        var reader = new RetainerReader(DataManager, Log);
        Resolver   = new VentureResolver(DataManager, Log);

        _reader        = reader;
        _client        = new BellClient();
        _sync          = new VentureSync(Config, reader, _client,
                                         Framework, AddonLifecycle, PlayerState, Log);
        _configWindow  = new ConfigurationWindow(this);
        _ventureWindow = new VentureWindow(this, reader);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Venture Bell settings. \"/venturebell sync\" sends now, \"window\" toggles the on-screen list.",
        });

        PluginInterface.UiBuilder.Draw         += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenConfig;

        Log.Info("VentureBell: Plugin loaded.");
    }

    /// <summary>The retainers as the game has them, for the settings window.</summary>
    internal Snapshot? ReadRetainers() => _reader.Read(PlayerState);

    internal string    Status => _sync.Status;
    internal LinkState Link   => _sync.Link;

    /// <summary>Asks the server whether it is there, without sending anything.</summary>
    internal void CheckConnection() => _sync.CheckConnection();

    /// <summary>Asks the server to push one notification with your credentials.</summary>
    internal void SendTestNotification() => _sync.SendTestNotification();

    /// <summary>Sends the current timers whether or not they have changed.</summary>
    internal void SyncNow() => _sync.Check(force: true);

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "sync":
                SyncNow();
                // The send is asynchronous, so this reports what is known now;
                // the settings window carries the outcome.
                ChatGui.Print("[Venture Bell] Syncing. " + Status);
                break;
            case "window":
                Config.ShowWindow = !Config.ShowWindow;
                SaveConfig();
                ChatGui.Print("[Venture Bell] On-screen list " + (Config.ShowWindow ? "shown." : "hidden."));
                break;
            case "status":
                ChatGui.Print("[Venture Bell] " + Status);
                break;
            default:
                _configWindow.IsVisible = !_configWindow.IsVisible;
                break;
        }
    }

    /// <summary>Whether to keep the overlay out of the way right now.</summary>
    internal bool IsInDuty => Condition[ConditionFlag.BoundByDuty]
                           || Condition[ConditionFlag.BoundByDuty56]
                           || Condition[ConditionFlag.WatchingCutscene];

    /// <summary>
    /// Opens the Timers window and closes it again, which is what makes the
    /// client ask the server for venture timers. Saves walking the main menu
    /// after a restart just to populate a list.
    /// </summary>
    internal unsafe void LoadTimers()
    {
        var agent = AgentContentsTimer.Instance();
        if (agent is null || agent->IsAgentActive())
            return;   // Already open: it is doing the job itself.

        agent->Show();

        // The request is out as soon as the window opens; the answer arrives on
        // its own, so there is nothing to wait for but the frame.
        Framework.RunOnTick(() =>
        {
            var closing = AgentContentsTimer.Instance();
            if (closing is not null && closing->IsAgentActive())
                closing->Hide();
        }, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Drag mode for the overlay, driven from the settings window.</summary>
    internal bool Repositioning
    {
        get => _ventureWindow.Repositioning;
        set => _ventureWindow.Repositioning = value;
    }

    private void OnOpenConfig() => _configWindow.IsVisible = true;

    private void OnDraw()
    {
        _configWindow.Draw();
        _ventureWindow.Draw();
    }

    internal void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw         -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenConfig;

        CommandManager.RemoveHandler(CmdMain);

        _sync.Dispose();
        _client.Dispose();
        _configWindow.Dispose();
        _ventureWindow.Dispose();
    }
}
