using System.Collections.Generic;
using System.Text;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;

using Lumina.Excel.Sheets;

namespace VentureBell;

/// <summary>One retainer, as the server needs to hear about it.</summary>
/// <param name="Name">The retainer's name.</param>
/// <param name="Venture">What it was sent on, empty when nothing is running.</param>
/// <param name="DoneAt">Unix seconds the venture completes; 0 when nothing is running.</param>
public readonly record struct RetainerVenture(string Name, string Venture, long DoneAt);

/// <summary>Everything the client knows about one character's retainers.</summary>
public readonly record struct Snapshot(string Character, IReadOnlyList<RetainerVenture> Retainers)
{
    /// <summary>
    /// Cheap equality for "has anything changed": comparing the snapshot would
    /// compare list references, and re-sending costs a request a minute.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var sb = new StringBuilder(Character);
            foreach (var r in Retainers)
                sb.Append('|').Append(r.Name).Append('=').Append(r.DoneAt);
            return sb.ToString();
        }
    }
}

/// <summary>Reads the venture timers the game already has in memory.</summary>
internal sealed class RetainerReader
{
    private readonly IDataManager _data;
    private readonly IPluginLog   _log;

    // Two sheet reads deep, and never changes within a session.
    private readonly Dictionary<ushort, string> _ventureNames = new();

    internal RetainerReader(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    /// <summary>
    /// The current snapshot, or null when there is nothing trustworthy to read.
    /// The client has to ask the server for venture timers — opening Timers or a
    /// summoning bell is what asks; <c>IsReady</c> is how it says so.
    /// </summary>
    internal unsafe Snapshot? Read(IPlayerState player)
    {
        if (!player.IsLoaded)
            return null;

        var manager = RetainerManager.Instance();
        if (manager is null || !manager->IsReady)
            return null;

        var retainers = manager->Retainers;
        var found     = new List<RetainerVenture>(retainers.Length);

        for (var i = 0; i < retainers.Length; i++)
        {
            ref var retainer = ref retainers[i];
            if (retainer.RetainerId == 0)
                continue;

            var name = retainer.NameString;
            if (string.IsNullOrEmpty(name))
                continue;

            // An idle retainer still belongs in the payload: the server
            // replaces its whole picture, so omitting one reads as dismissed.
            var venture = retainer.VentureId == 0 ? "" : VentureName(retainer.VentureId);
            var doneAt  = retainer.VentureId == 0 ? 0L : retainer.VentureComplete;

            found.Add(new RetainerVenture(name, venture, doneAt));
        }

        return new Snapshot(Character(player), found);
    }

    private static string Character(IPlayerState player)
    {
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(world) ? player.CharacterName : $"{player.CharacterName}@{world}";
    }

    /// <summary>
    /// The name the Retainer Task window shows. Exploration ventures carry their
    /// own; the rest are named after the item they fetch.
    /// </summary>
    private string VentureName(ushort ventureId)
    {
        if (_ventureNames.TryGetValue(ventureId, out var cached))
            return cached;

        var name = LookUpVenture(ventureId);
        _ventureNames[ventureId] = name;
        return name;
    }

    private string LookUpVenture(ushort ventureId)
    {
        var task = _data.GetExcelSheet<RetainerTask>()?.GetRowOrDefault(ventureId);
        if (task is null)
        {
            _log.Warning($"VentureBell: no RetainerTask row for venture {ventureId}.");
            return "";
        }

        if (task.Value.IsRandom)
        {
            var random = _data.GetExcelSheet<RetainerTaskRandom>()?.GetRowOrDefault(task.Value.Task.RowId);
            return random?.Name.ExtractText() ?? "";
        }

        var normal = _data.GetExcelSheet<RetainerTaskNormal>()?.GetRowOrDefault(task.Value.Task.RowId);
        return normal?.Item.ValueNullable?.Name.ExtractText() ?? "";
    }
}
