using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

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

    internal Configuration Config { get; }

    private readonly BellClient          _client;
    private readonly VentureSync         _sync;
    private readonly ConfigurationWindow _configWindow;

    private const string CmdMain = "/venturebell";

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _client       = new BellClient();
        _sync         = new VentureSync(Config, new RetainerReader(DataManager, Log), _client,
                                        Framework, AddonLifecycle, PlayerState, Log);
        _configWindow = new ConfigurationWindow(this);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Venture Bell settings. \"/venturebell sync\" sends the current timers now.",
        });

        PluginInterface.UiBuilder.Draw         += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenConfig;

        Log.Info("VentureBell: Plugin loaded.");
    }

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
            case "status":
                ChatGui.Print("[Venture Bell] " + Status);
                break;
            default:
                _configWindow.IsVisible = !_configWindow.IsVisible;
                break;
        }
    }

    private void OnOpenConfig() => _configWindow.IsVisible = true;
    private void OnDraw()       => _configWindow.Draw();

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
    }
}
