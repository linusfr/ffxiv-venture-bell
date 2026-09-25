using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace VentureBell;

/// <summary>What a request is trying to get a retainer onto.</summary>
public enum VentureTarget
{
    BestExploration,
    QuickExploration,
}

/// <summary>
/// Walks the menus the game puts up when a venture is assigned, deciding what
/// to click at each step. It currently only says what it would do: the chain
/// is four windows deep and worth watching before it touches anything.
/// </summary>
internal sealed class VentureAssigner : IDisposable
{
    // Menu entries, in whatever language the client is in. The game appends
    // state in brackets — "(In progress)", "(Pending completion)" — so both are
    // matched on the part before it.
    private const uint AssignVentureAddonRow = 2386;
    private const uint VentureReportAddonRow = 2403;

    // SelectString lays its entries out after a fixed header. The retainer menu
    // also puts a count at [3]; the category menu leaves that slot undefined,
    // so the entries are read by walking the strings instead.
    private const int FirstEntry = 7;

    private static readonly string[] Windows =
        { "RetainerList", "SelectString", "RetainerTaskList", "RetainerTaskAsk", "RetainerTaskResult" };

    // The overview lays its retainers out in blocks of ten, names first.
    private const int FirstRetainer = 3;
    private const int RetainerStride = 10;

    // "Field Exploration VIII" -> "Field Exploration", which is what the
    // category menu offers.
    private static readonly Regex Tier = new(@"\s+[IVXLC]+$", RegexOptions.Compiled);

    private readonly Configuration   _config;
    private readonly IAddonLifecycle _addons;
    private readonly IDataManager    _data;
    private readonly IGameGui        _gui;
    private readonly IFramework      _framework;
    private readonly IPluginLog      _log;
    private readonly Action<string>  _report;

    // The same file the probe writes, so the decisions and the windows they
    // were made about read as one timeline. Dalamud's own log buffers for
    // minutes, which is no use while chasing a stall.
    private readonly string _file;

    // Queued by retainer, because the game's list does not say which row is
    // which — they are opened one at a time, but decided all at once.
    private readonly Dictionary<string, Queued> _queue = new();

    /// <summary>A retainer's venture, and whether one has to be collected first.</summary>
    private sealed record Queued(VentureOption Venture, bool Collect)
    {
        internal bool Collect { get; set; } = Collect;
    }

    // Set while a retainer's menu still has to be closed behind us.
    private bool     _leaving;
    private DateTime _confirmedAt = DateTime.MinValue;

    // Retainers say goodbye after the menu closes, and that line still has to
    // be clicked through — so the run stays "in flight" for a moment after the
    // last thing it did.
    private DateTime _busyUntil = DateTime.MinValue;

    private VentureOption? _wanted;
    private string         _retainer = "";
    private bool           _live;

    internal VentureAssigner(Configuration config, IAddonLifecycle addons, IDataManager data,
                             IGameGui gui, IFramework framework, IPluginLog log, Action<string> report,
                             string directory)
    {
        _framework = framework;
        _file      = Path.Combine(directory, "probe.log");
        _config = config;
        _addons = addons;
        _data   = data;
        _gui    = gui;
        _log    = log;
        _report = report;

        _addons.RegisterListener(AddonEvent.PostSetup, Windows, OnWindow);
        // Dialogue is clicked through rather than waited on: the window is
        // reused between retainers, so it never fires a setup event to hook.
        _framework.Update += OnUpdate;
        // The overview is refreshed rather than rebuilt when a retainer is done
        // with, so that is where the next one is picked up.
        _addons.RegisterListener(AddonEvent.PostRefresh, new[] { "RetainerList" }, OnWindow);
    }

    /// <summary>Whether a dry run is waiting for the menus to be walked.</summary>
    internal bool Pending => _queue.Count > 0 || _wanted is not null;

    internal string Wanted => _wanted?.Name ?? (_queue.Count == 1 ? FirstQueued() : $"{_queue.Count} retainers");

    private string FirstQueued()
    {
        foreach (var entry in _queue)
            return $"{entry.Key}: {entry.Value.Venture.Name}";
        return "";
    }

    /// <summary>
    /// Arms a dry run. Nothing happens until the menus are opened by hand —
    /// each window then reports what would have been clicked.
    /// </summary>
    internal void DryRun(string retainer, VentureOption venture, bool collect)
        => Arm(retainer, venture, collect, live: false);

    /// <summary>Arms the real thing: the menus are clicked as they open.</summary>
    internal void Assign(string retainer, VentureOption venture, bool collect)
        => Arm(retainer, venture, collect, live: true);

    private void Arm(string retainer, VentureOption venture, bool collect, bool live)
    {
        _live            = live;
        _queue[retainer] = new Queued(venture, collect);
        _report(live
            ? $"{retainer}: {venture.Name} queued{(collect ? ", report to collect first" : "")}."
            : $"Dry run armed for {retainer}: {venture.Name}. Walk the menus and watch the log.");

        if (live)
            _framework.RunOnTick(OpenNext, TimeSpan.FromMilliseconds(100));
    }

    internal void Cancel()
    {
        _queue.Clear();
        _wanted = null;
        _report("Queue cleared.");
    }

    /// <summary>One step per window, as each one opens.</summary>
    private unsafe void OnWindow(AddonEvent type, AddonArgs args)
    {
        // A retainer is adopted when its menu opens and the title names it, so
        // the queue says there is work to do — and so does _leaving, which is
        // the state after the last confirm, with a menu still to close.
        if (_wanted is null && _queue.Count == 0 && !_leaving)
            return;

        switch (args.AddonName)
        {
            case "RetainerList":
                // A frame later: on setup the list is still being filled in.
                _framework.RunOnTick(OpenNext, TimeSpan.FromMilliseconds(250));
                break;

            case "SelectString" when args is AddonSetupArgs menu:
                Decide(menu);
                break;

            case "RetainerTaskResult":
                // Collect and close, rather than the reassign button beside it:
                // the point is to choose what comes next.
                Click(args.AddonName, 1, $"{_retainer}: closing the venture report");
                break;

            case "RetainerTaskList":
                // No callback reaches this list — its rows are chosen through
                // the component's own selection state, so the click is
                // synthesised. Row 0: the list is already filtered to what this
                // retainer can take, and the agent check at the confirm catches
                // it if that ever stops being true.
                _framework.RunOnTick(() => SelectRow(0), TimeSpan.FromMilliseconds(250));
                Say($"{_retainer}: taking the top of the list for {_wanted!.Value.Name}");
                break;

            case "RetainerTaskAsk":
                Confirm(args.AddonName);
                break;
        }
    }

    /// <summary>
    /// The agent says what is actually on offer, so the venture is checked
    /// before it is taken rather than after.
    /// </summary>
    private unsafe void Confirm(string addon)
    {
        if (_wanted is null)
            return;

        var agent   = AgentRetainerTask.Instance();
        var onOffer = agent is null ? 0 : agent->RetainerTaskId;

        // Button 1 assigns, 0 backs out: the report window's own labels run
        // "Reassign" then "Confirm", and this dialog is the same way round.
        if (onOffer == _wanted.Value.TaskId)
        {
            Say($"{_retainer}: {_wanted.Value.Name} confirmed");
            _framework.RunOnTick(() => SendEvent(addon, AtkEventType.ButtonClick, 1),
                                 TimeSpan.FromMilliseconds(200));
        }
        else if (!_config.TakeHighestQualified)
        {
            // The game only lists what a retainer qualifies for, so something
            // lesser on offer means the gear is behind the level.
            var needs = _wanted.Value.RequiredItemLevel > 0 ? $" (needs i{_wanted.Value.RequiredItemLevel})" : "";
            Click(addon, 0, $"{_retainer} cannot take {_wanted.Value.Name}{needs} — the list offered " +
                            $"{Name(onOffer)}. Stopping; check the gear.");
        }
        else
        {
            Click(addon, 0, $"{_retainer}: {_wanted.Value.Name} unavailable, taking {Name(onOffer)} instead");
        }

        Drop();
    }

    private DateTime _advancedAt = DateTime.MinValue;
    private DateTime _openedAt   = DateTime.MinValue;

    /// <summary>
    /// Clicks through the retainer's greeting while a run is in flight. Only
    /// then: advancing dialogue the rest of the time would be someone else's
    /// plugin.
    /// </summary>
    private unsafe void OnUpdate(IFramework framework)
    {
        // _leaving counts as work: after the last retainer the queue is empty,
        // and that is precisely when its menu still has to be closed.
        if (!_live || (_queue.Count == 0 && _wanted is null && !_leaving && DateTime.UtcNow > _busyUntil))
            return;

        // One a frame would spam the game; a dialogue needs a beat anyway.
        if (DateTime.UtcNow - _advancedAt < TimeSpan.FromMilliseconds(200))
            return;

        var talk = (AtkUnitBase*)_gui.GetAddonByName("Talk").Address;
        if (talk is not null && talk->IsVisible)
        {
            _advancedAt = DateTime.UtcNow;
            talk->FireCallbackInt(0);
            return;
        }

        // The windows that close a run are often reused rather than rebuilt, so
        // they announce nothing and there is no event to answer. Polling is the
        // only way to see them.
        // A second's grace: the game rebuilds the retainer's menu right after a
        // confirm, and leaving the instance that is about to be replaced only
        // brings the menu straight back.
        if (!_leaving || DateTime.UtcNow - _confirmedAt < TimeSpan.FromSeconds(1))
            return;

        var menu = (AtkUnitBase*)_gui.GetAddonByName("SelectString").Address;
        if (menu is null || !menu->IsVisible)
            return;

        var title = TitleOf(menu);
        if (_retainer.Length == 0 || !title.Contains(_retainer, StringComparison.Ordinal))
            return;

        var entries = EntriesOf(menu);
        if (entries.Count == 0)
            return;

        _advancedAt = DateTime.UtcNow;
        _leaving    = false;
        Click("SelectString", entries.Count - 1, $"{_retainer}: done, leaving their menu");
    }

    /// <summary>The heading of a window that is already open.</summary>
    private static unsafe string TitleOf(AtkUnitBase* unit)
    {
        if (unit->AtkValues is null || unit->AtkValuesCount <= 2)
            return "";

        var value = unit->AtkValues[2];
        return value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8
               && value.String.Value is not null
            ? value.String.ToString()
            : "";
    }

    /// <summary>The entries of a menu that is already open.</summary>
    private static unsafe List<string> EntriesOf(AtkUnitBase* unit)
    {
        var found = new List<string>();
        if (unit->AtkValues is null)
            return found;

        for (var i = FirstEntry; i < unit->AtkValuesCount; i++)
        {
            var value = unit->AtkValues[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                break;

            found.Add(value.String.Value is null ? "" : value.String.ToString());
        }

        return found;
    }

    /// <summary>
    /// Picks the next queued retainer out of the overview and opens it. The
    /// list's click event reports the same parameter whichever row is used, so
    /// the row comes from the names the list was built with — and the menu that
    /// opens names the retainer, which is what confirms the right one.
    /// </summary>
    private unsafe void OpenNext()
    {
        if (_wanted is not null || _queue.Count == 0)
            return;

        // Both the button and the list's own events ask for this; once is
        // enough, and three clicks at a closing window is where the "vanished"
        // noise came from.
        if (DateTime.UtcNow - _openedAt < TimeSpan.FromSeconds(1))
            return;

        _openedAt = DateTime.UtcNow;

        var list = (AtkUnitBase*)_gui.GetAddonByName("RetainerList").Address;
        if (list is null || !list->IsVisible)
            return;   // Nothing to open from; the queue waits for the bell.

        var names = Retainers(list->AtkValues, list->AtkValuesCount);
        for (var row = 0; row < names.Count; row++)
        {
            if (!_queue.ContainsKey(names[row]))
                continue;

            var name = names[row];
            var at   = row;
            // A frame later: the list is still settling when it is first built,
            // and a click into it now does nothing.
            Click("RetainerList", at, $"{name}: opening from the list [{at}]");
            return;
        }
    }

    /// <summary>The retainers the overview was built with, in its own order.</summary>
    private static unsafe List<string> Retainers(AtkValue* values, uint count)
    {
        var found = new List<string>();
        if (values is null)
            return found;

        for (var i = FirstRetainer; i < count; i += RetainerStride)
        {
            var value = values[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                break;

            var name = value.String.Value is null ? "" : value.String.ToString();
            if (name.Length == 0)
                break;

            found.Add(name);
        }

        return found;
    }

    /// <summary>Reads a menu and picks the entry this step needs.</summary>
    private unsafe void Decide(AddonSetupArgs args)
    {
        // The retainer's own menu names it in the title, which is how a queued
        // retainer is recognised whichever one is opened, and in any language.
        var title = Title(args);
        foreach (var queued in _queue)
        {
            if (!title.Contains(queued.Key, StringComparison.Ordinal))
                continue;

            _retainer = queued.Key;
            _wanted   = queued.Value.Venture;
                break;
        }

        if (_wanted is null)
        {
            // The menu that comes back after a confirm: leave it, so the list
            // returns for whoever is next — or for you, if nobody is.
            if (_leaving && _retainer.Length > 0 && title.Contains(_retainer, StringComparison.Ordinal))
            {
                var menu = Entries(args);
                if (menu.Count > 0)
                {
                    _leaving = false;
                    Click("SelectString", menu.Count - 1, $"{_retainer}: done, leaving their menu");
                    return;
                }
            }

            if (_queue.Count > 0 && !_leaving)
                Say($"a menu opened but no queued retainer matched its title: \"{title.Replace("\n", " / ")}\"");
            return;
        }

        var entries = Entries(args);
        if (entries.Count == 0)
        {
            // Never silently: a menu that reads as empty is a menu whose layout
            // has changed, and that is exactly what this is here to catch.
            Say($"a menu opened with no readable entries ({args.AtkValueCount} values) — layout changed?");
            return;
        }

        // The game appends state to an entry — "Assign venture. (In progress)"
        // — and moves it about as entries come and go, so it is matched by its
        // opening text and never by position.
        // Whether a venture is finished comes from RetainerManager, not from the
        // state the game writes into the label: "(Complete)" and "(Complete on
        // 26/9 14:22)" mean opposite things and neither survives translation.
        if (_queue.TryGetValue(_retainer, out var current) && current.Collect)
        {
            var report  = Stem(VentureReportAddonRow);
            var collect = report.Length > 0
                ? entries.FindIndex(e => e.StartsWith(report, StringComparison.Ordinal))
                : -1;
            if (collect < 0)
            {
                Say($"{_retainer}: no venture report entry — stopping. Entries: {string.Join(" | ", entries)}");
                Drop();
                return;
            }

            current.Collect = false;
            Click("SelectString", collect, $"{_retainer}: collecting the venture report [{collect}]");
            return;
        }

        var assign = Stem(AssignVentureAddonRow);
        var index = assign.Length > 0 ? entries.FindIndex(e => e.StartsWith(assign, StringComparison.Ordinal)) : -1;
        if (index >= 0)
        {
            Click("SelectString", index, $"retainer menu: [{index}] \"{entries[index]}\"");
            return;
        }

        // Otherwise the category menu, whose entries are the venture family
        // names with a full stop: "Field Exploration." for the VIII of it.
        var family = Tier.Replace(_wanted!.Value.Name, "");
        index = entries.FindIndex(e => e.TrimEnd('.') == family);
        if (index < 0)
        {
            Say($"category menu: no entry matches \"{family}\" — entries were {string.Join(" | ", entries)}");
            Drop();
            return;
        }

        Click("SelectString", index, $"category menu: [{index}] \"{entries[index]}\" for {_wanted.Value.Name}");
    }

    /// <summary>A venture's name, for saying what turned up instead.</summary>
    private string Name(uint taskId)
    {
        var task = _data.GetExcelSheet<Lumina.Excel.Sheets.RetainerTask>()?.GetRowOrDefault(taskId);
        if (task is null)
            return "nothing";

        return _data.GetExcelSheet<Lumina.Excel.Sheets.RetainerTaskRandom>()
                    ?.GetRowOrDefault(task.Value.Task.RowId)?.Name.ExtractText() ?? taskId.ToString();
    }

    /// <summary>Takes the retainer off the queue, however it ended.</summary>
    /// <summary>
    /// Takes the retainer off the queue, however it ended. _retainer is kept:
    /// the menu that opens next is still theirs, and leaving it is what brings
    /// the list back for whoever is next.
    /// </summary>
    private void Drop()
    {
        _queue.Remove(_retainer);
        _wanted      = null;
        _leaving     = true;
        _confirmedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Clicks, or says what it would have clicked. Always a couple of frames
    /// later: these windows are still being built when they announce
    /// themselves, and a callback fired into one lands on nothing.
    /// </summary>
    private void Click(string addon, int index, string what)
    {
        if (!_live)
        {
            Say("would click — " + what);
            return;
        }

        Say(what);
        _framework.RunOnTick(() => Fire(addon, index), TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Sends a window the event a mouse would, for the ones that ignore
    /// callbacks entirely — the venture list and the confirmation both do.
    /// </summary>
    internal unsafe void SendEvent(string addon, AtkEventType type, int param)
    {
        var unit = (AtkUnitBase*)_gui.GetAddonByName(addon).Address;
        if (unit is null)
        {
            Say($"{addon} is not open");
            return;
        }

        var data = stackalloc AtkEventData[1];
        var evt  = stackalloc AtkEvent[1];
        unit->ReceiveEvent(type, param, evt, data);
    }

    /// <summary>
    /// Clicks a row of the venture list the way the mouse does: the list reads
    /// its selection out of the event data rather than from a callback.
    /// </summary>
    private unsafe void SelectRow(int row)
    {
        var unit = (AtkUnitBase*)_gui.GetAddonByName("RetainerTaskList").Address;
        if (unit is null)
        {
            Say("the venture list went away before a row could be taken");
            Drop();
            return;
        }

        var data = stackalloc AtkEventData[1];
        data->ListItemData.SelectedIndex = row;

        var evt = stackalloc AtkEvent[1];
        unit->ReceiveEvent(AtkEventType.ListItemClick, row, evt, data);
    }

    private unsafe void Fire(string addon, int index)
    {
        var unit = (AtkUnitBase*)_gui.GetAddonByName(addon).Address;
        if (unit is null || !unit->IsVisible)
        {
            // Ordinary: the same step can be queued twice while a window is on
            // its way out. Worth a log line, not a chat one.
            _log.Information($"VentureBell/assign {addon} went away before it could be clicked");
            return;
        }

        if (addon == "RetainerList")
        {
            // The overview wants a case and a row rather than a bare index:
            // FireCallbackInt does nothing at all to it.
            var values = stackalloc AtkValue[2];
            values[0].SetInt(2);
            values[1].SetInt(index);
            unit->FireCallback(2, values);
            return;
        }

        unit->FireCallbackInt(index);
    }

    /// <summary>
    /// A menu entry's text without the state the game appends in brackets, and
    /// without the leading space some rows carry.
    /// </summary>
    private string Stem(uint addonRow)
    {
        var text = _data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(addonRow)
                        ?.Text.ExtractText() ?? "";
        var bracket = text.IndexOf(" (", StringComparison.Ordinal);
        return (bracket > 0 ? text[..bracket] : text).Trim();
    }

    /// <summary>The menu's heading, which for a retainer menu carries its name.</summary>
    private static unsafe string Title(AddonSetupArgs args)
    {
        var values = (AtkValue*)args.AtkValues;
        if (values is null || args.AtkValueCount <= 2)
            return "";

        var value = values[2];
        return value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8
               && value.String.Value is not null
            ? value.String.ToString()
            : "";
    }

    private static unsafe List<string> Entries(AddonSetupArgs args)
    {
        var values = (AtkValue*)args.AtkValues;
        var found  = new List<string>();
        if (values is null)
            return found;

        for (var i = FirstEntry; i < args.AtkValueCount; i++)
        {
            var value = values[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                break;   // The entries run until the trailing ints begin.

            found.Add(value.String.Value is null ? "" : value.String.ToString());
        }

        return found;
    }

    private void Say(string line)
    {
        _busyUntil = DateTime.UtcNow.AddSeconds(5);
        _log.Information("VentureBell/assign " + line);
        _report(line);

        try
        {
            File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff} ASSIGN {line}\n");
        }
        catch (Exception ex)
        {
            _log.Warning($"VentureBell: could not write the assign log — {ex.Message}");
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _addons.UnregisterListener(AddonEvent.PostSetup, Windows, OnWindow);
        _addons.UnregisterListener(AddonEvent.PostRefresh, new[] { "RetainerList" }, OnWindow);
    }
}
