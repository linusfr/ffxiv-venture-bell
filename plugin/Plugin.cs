using System;

using System.Collections.Generic;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;

    internal Configuration   Config   { get; }
    internal VentureResolver Resolver { get; private set; } = null!;

    private readonly RetainerReader      _reader;
    private readonly BellClient          _client;
    private readonly VentureSync         _sync;
    private readonly ConfigurationWindow _configWindow;
    private readonly VentureWindow       _ventureWindow;
    private readonly VentureProbe        _probe;
    private readonly VentureAssigner     _assigner;
    private readonly RetainerBar         _bar;

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
        _bar           = new RetainerBar(this, reader);
        _probe         = new VentureProbe(Config, AddonLifecycle, Log, PluginInterface.GetPluginConfigDirectory());
        _assigner      = new VentureAssigner(Config, AddonLifecycle, DataManager, GameGui, Framework, Log,
                                             line => ChatGui.Print("[Venture Bell] " + line),
                                             PluginInterface.GetPluginConfigDirectory());

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Venture Bell settings. \"/venturebell sync\" sends now, \"window\" toggles the on-screen list.",
        });

        PluginInterface.UiBuilder.Draw         += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenConfig;

        Log.Info("VentureBell: Plugin loaded.");
    }

    /// <summary>Arms a dry run: the next walk through the menus is narrated, not driven.</summary>
    internal void DryRunAssign(string retainer, VentureOption venture, bool collect)
        => _assigner.DryRun(retainer, venture, collect);

    /// <summary>The real thing: clicks the menus as they open.</summary>
    /// <param name="collect">The retainer has a finished venture to collect first.</param>
    internal void Assign(string retainer, VentureOption venture, bool collect)
        => _assigner.Assign(retainer, venture, collect);

    internal bool   DryRunPending => _assigner.Pending;
    internal string DryRunWanted  => _assigner.Wanted;
    internal void   CancelDryRun() => _assigner.Cancel();

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
        // Case is kept for the diagnostic below: addon names are matched
        // exactly, and lowercasing them made every lookup miss.
        var trimmed = args.Trim();
        if (trimmed.StartsWith("fire ", StringComparison.OrdinalIgnoreCase))
        {
            Fire(trimmed[5..]);
            return;
        }

        // "/venturebell click RetainerTaskAsk 1" sends a ButtonClick rather than
        // a callback, for the windows that only answer to events.
        if (trimmed.StartsWith("click ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var param))
            {
                _assigner.SendEvent(parts[0], FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType.ButtonClick, param);
                ChatGui.Print($"[Venture Bell] sent ButtonClick {param} to {parts[0]}.");
            }
            return;
        }

        switch (trimmed.ToLowerInvariant())
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

    private unsafe void Fire(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            ChatGui.Print("[Venture Bell] /venturebell fire <addon> <int> [int ...]");
            return;
        }

        var unit = (AtkUnitBase*)GameGui.GetAddonByName(parts[0]).Address;
        if (unit is null)
        {
            ChatGui.Print($"[Venture Bell] {parts[0]} is not open.");
            return;
        }

        var numbers = new List<int>();
        foreach (var part in parts[1..])
            if (int.TryParse(part, out var value))
                numbers.Add(value);

        if (numbers.Count == 1)
        {
            unit->FireCallbackInt(numbers[0]);
        }
        else
        {
            var values = stackalloc AtkValue[numbers.Count];
            for (var i = 0; i < numbers.Count; i++)
                values[i].SetInt(numbers[i]);
            unit->FireCallback((uint)numbers.Count, values);
        }

        ChatGui.Print($"[Venture Bell] fired {string.Join(",", numbers)} at {parts[0]}.");
    }

    private void OnOpenConfig() => _configWindow.IsVisible = true;

    private void OnDraw()
    {
        _configWindow.Draw();
        _ventureWindow.Draw();
        _bar.Draw();
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
        _probe.Dispose();
        _assigner.Dispose();
        _bar.Dispose();
    }
}
