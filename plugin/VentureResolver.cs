using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Dalamud.Plugin.Services;

using Lumina.Excel.Sheets;

namespace VentureBell;

/// <summary>One venture a retainer could be sent on.</summary>
/// <param name="TaskId">The RetainerTask row, which is what the game calls a venture.</param>
/// <param name="RequiredLevel">Retainer level it asks for.</param>
/// <param name="RequiredItemLevel">Item level it asks for; 0 when it asks none.</param>
public readonly record struct VentureOption(uint TaskId, string Name, int RequiredLevel, int RequiredItemLevel);

public static class VentureName
{
    /// <summary>
    /// "Field Exploration VIII" becomes "field": the short word a button can
    /// wear. A gatherer's reads "highland" or "waterside", so it is taken from
    /// the venture rather than written down.
    /// </summary>
    public static string Word(string venture)
    {
        var space = venture.IndexOf(' ');
        return (space > 0 ? venture[..space] : venture).ToLowerInvariant();
    }
}

/// <summary>
/// Works out what a retainer should be sent on, from the sheets rather than
/// from what it happens to be doing — which is the gap in repeating a fixed
/// venture: a retainer that levels up keeps running the tier it outgrew.
/// </summary>
internal sealed class VentureResolver
{
    private readonly IDataManager _data;
    private readonly IPluginLog   _log;

    // ClassJobCategory carries one boolean column per job, named after the
    // abbreviation, so the lookup is a reflected property per job.
    private readonly Dictionary<byte, PropertyInfo?>     _jobColumns = new();
    private readonly Dictionary<(byte, byte), VentureOption?> _best   = new();

    private VentureOption? _quick;
    private bool           _quickResolved;

    internal VentureResolver(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    /// <summary>
    /// Quick Exploration: the one random venture with no level requirement, so
    /// it needs no name matching and holds in any client language.
    /// </summary>
    internal VentureOption? QuickExploration()
    {
        if (_quickResolved)
            return _quick;

        _quickResolved = true;
        var sheet = _data.GetExcelSheet<RetainerTask>();
        if (sheet is null)
            return _quick;

        foreach (var task in sheet)
        {
            if (task.IsRandom && task.RetainerLevel == 0)
            {
                _quick = Describe(task);
                break;
            }
        }

        return _quick;
    }

    /// <summary>
    /// The highest exploration this retainer's job and level allow. Item level
    /// is reported rather than enforced: the client does not tell us a
    /// retainer's gear, so the game has the last word on whether it is offered.
    /// </summary>
    internal VentureOption? BestExploration(byte classJob, byte level)
    {
        if (_best.TryGetValue((classJob, level), out var cached))
            return cached;

        var best = Resolve(classJob, level);
        _best[(classJob, level)] = best;
        return best;
    }

    private VentureOption? Resolve(byte classJob, byte level)
    {
        var tasks = _data.GetExcelSheet<RetainerTask>();
        var column = JobColumn(classJob);
        if (tasks is null || column is null)
            return null;

        var categories = _data.GetExcelSheet<ClassJobCategory>();
        if (categories is null)
            return null;

        VentureOption? best = null;
        foreach (var task in tasks)
        {
            // RetainerLevel 0 is Quick Exploration, which is always available
            // and never the answer to "what is the best one".
            if (!task.IsRandom || task.RetainerLevel == 0 || task.RetainerLevel > level)
                continue;

            var category = categories.GetRowOrDefault(task.ClassJobCategory.RowId);
            if (category is null || column.GetValue(category.Value) is not true)
                continue;

            if (best is null || task.RetainerLevel > best.Value.RequiredLevel)
                best = Describe(task);
        }

        return best;
    }

    private PropertyInfo? JobColumn(byte classJob)
    {
        if (_jobColumns.TryGetValue(classJob, out var cached))
            return cached;

        PropertyInfo? column = null;
        var job = _data.GetExcelSheet<ClassJob>()?.GetRowOrDefault(classJob);
        if (job is not null)
        {
            var abbreviation = job.Value.Abbreviation.ExtractText();
            column = typeof(ClassJobCategory).GetProperty(abbreviation, BindingFlags.Public | BindingFlags.Instance);
            if (column is null)
                _log.Warning($"VentureBell: ClassJobCategory has no column for {abbreviation}.");
        }

        _jobColumns[classJob] = column;
        return column;
    }

    private VentureOption Describe(RetainerTask task)
    {
        var name = _data.GetExcelSheet<RetainerTaskRandom>()?.GetRowOrDefault(task.Task.RowId)?.Name.ExtractText() ?? "";
        return new VentureOption(task.RowId, name, task.RetainerLevel, task.RequiredItemLevel);
    }
}
