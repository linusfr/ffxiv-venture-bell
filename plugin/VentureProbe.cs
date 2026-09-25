using System;
using System.IO;
using System.Text;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace VentureBell;

/// <summary>
/// Temporary: logs what the venture windows are handed and what they send back,
/// so assigning one can be written against what the game actually does rather
/// than a guess. Debug mode only, and it never touches anything.
/// </summary>
internal sealed class VentureProbe : IDisposable
{
    // SelectString is where the venture is actually chosen, so its entries
    // matter as much as the retainer windows.
    private static readonly string[] Addons =
    {
        "RetainerTaskList", "RetainerTaskAsk", "RetainerTaskResult", "RetainerList",
        "SelectString", "SelectIconString", "SelectYesno",
    };

    private readonly Configuration   _config;
    private readonly IAddonLifecycle _addons;
    private readonly IPluginLog      _log;

    // Its own file: Dalamud's log buffers, and waiting for a flush to find out
    // what a click did makes for a slow conversation.
    private readonly string _file;

    internal VentureProbe(Configuration config, IAddonLifecycle addons, IPluginLog log, string directory)
    {
        _config = config;
        _addons = addons;
        _log    = log;
        _file   = Path.Combine(directory, "probe.log");

        _addons.RegisterListener(AddonEvent.PostSetup, Addons, OnValues);
        _addons.RegisterListener(AddonEvent.PostRefresh, Addons, OnValues);
        _addons.RegisterListener(AddonEvent.PostReceiveEvent, Addons, OnEvent);

        // Every window that opens, by name only. Guessing which ones are
        // involved is how the last capture ended up with holes in it.
        _addons.RegisterListener(AddonEvent.PostSetup, OnAnyOpened);
    }

    /// <summary>What the game put into the window: the venture rows, and what the
    /// confirmation dialog was told to confirm.</summary>
    private unsafe void OnValues(AddonEvent type, AddonArgs args)
    {
        if (!_config.DebugMode)
            return;

        AtkValue* values;
        uint      count;
        switch (args)
        {
            case AddonSetupArgs setup:
                values = (AtkValue*)setup.AtkValues;
                count  = setup.AtkValueCount;
                break;
            case AddonRefreshArgs refresh:
                values = (AtkValue*)refresh.AtkValues;
                count  = refresh.AtkValueCount;
                break;
            default:
                return;
        }

        if (values is null)
            return;

        // Empty slots are most of a venture list's array and tell us nothing;
        // the cap is high enough not to cut a long one short.
        var sb = new StringBuilder($"{args.AddonName} {type} values={count}{Agent()}");
        for (var i = 0; i < (int)Math.Min(count, 512u); i++)
        {
            if (values[i].Type is AtkValueType.Undefined or AtkValueType.Null)
                continue;
            sb.Append($"\n  [{i}] {Describe(values[i])}");
        }

        Write(sb.ToString());
    }

    /// <summary>What a click sent back, which is what we would have to send.</summary>
    private void OnEvent(AddonEvent type, AddonArgs args)
    {
        if (!_config.DebugMode || args is not AddonReceiveEventArgs e)
            return;

        // Rollovers are most of the file and say nothing.
        if (e.AtkEventType is Dalamud.Game.Addon.Events.AddonEventType.ListItemRollOver
                            or Dalamud.Game.Addon.Events.AddonEventType.ListItemRollOut)
            return;

        Write($"{args.AddonName} event {e.AtkEventType} param={e.EventParam}{Agent()}");
    }

    /// <summary>
    /// The agent's own state, which is where the venture identity lives — the
    /// addon values carry none of it.
    /// </summary>
    private static unsafe string Agent()
    {
        var agent = AgentRetainerTask.Instance();
        if (agent is null)
            return "";

        return $" | agent taskId={agent->RetainerTaskId} lvRange={agent->RetainerTaskLvRange}" +
               $" displayType={agent->DisplayType} loading={agent->IsLoading}";
    }

    private void Write(string line)
    {
        try
        {
            File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch (Exception ex)
        {
            _log.Warning($"VentureBell: could not write the probe log — {ex.Message}");
        }
    }

    private static unsafe string Describe(AtkValue value) => value.Type switch
    {
        AtkValueType.Int   => $"int {value.Int}",
        AtkValueType.UInt  => $"uint {value.UInt}",
        AtkValueType.Bool  => $"bool {value.Bool}",
        AtkValueType.String
            or AtkValueType.ManagedString
            or AtkValueType.String8 => $"string \"{(value.String.Value is null ? "" : value.String.ToString())}\"",
        _ => value.Type.ToString(),
    };

    /// <summary>One line per window that opens, whatever it is.</summary>
    private void OnAnyOpened(AddonEvent type, AddonArgs args)
    {
        if (_config.DebugMode)
            Write($"opened {args.AddonName}{Agent()}");
    }

    public void Dispose()
    {
        _addons.UnregisterListener(AddonEvent.PostSetup, OnAnyOpened);
        _addons.UnregisterListener(AddonEvent.PostSetup, Addons, OnValues);
        _addons.UnregisterListener(AddonEvent.PostRefresh, Addons, OnValues);
        _addons.UnregisterListener(AddonEvent.PostReceiveEvent, Addons, OnEvent);
    }
}
