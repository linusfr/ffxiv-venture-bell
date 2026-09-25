using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace VentureBell.Windows;

/// <summary>
/// A small always-on list of what the retainers are doing and when they are
/// back. No title bar, no buttons, no chrome — the same idea as the notification
/// itself, just on screen: the numbers, and nothing around them.
/// </summary>
internal sealed class VentureWindow : IDisposable
{
    private static readonly Vector4 Done    = new(0.45f, 0.85f, 0.5f, 1f);
    private static readonly Vector4 Pending = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 Muted   = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin         _plugin;
    private readonly RetainerReader _reader;
    private Configuration Config => _plugin.Config;

    // The list is minute-resolution, so there is nothing to gain from reading
    // the game more often than this. Five seconds is still prompt enough that a
    // venture assigned at the bell, or one that has just come back, shows up
    // before you have looked away.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private DateTime                  _lastRead = DateTime.MinValue;
    private IReadOnlyList<RetainerVenture> _cached = Array.Empty<RetainerVenture>();

    // Where the window actually is, as of last frame, and when its position
    // last changed. Saving on every frame of a drag would rewrite the config a
    // hundred times for one move.
    private Vector2  _lastPos;
    private bool     _seen;
    private DateTime _movedAt = DateTime.MinValue;

    /// <summary>Reposition mode: borders and a title bar, briefly, so it can be dragged.</summary>
    internal bool Repositioning { get; set; }

    internal VentureWindow(Plugin plugin, RetainerReader reader)
    {
        _plugin = plugin;
        _reader = reader;
    }

    internal void Draw()
    {
        if (!Config.ShowWindow)
            return;

        Refresh();
        if (!ShouldShow())
            return;

        var flags = ImGuiWindowFlags.NoDecoration
                  | ImGuiWindowFlags.AlwaysAutoResize
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoNav;
        if (Repositioning)
            // Borders and a drag target, and nothing else changes — what you
            // move is what you get.
            flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNav
                  | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings;
        else if (Config.WindowLocked)
            // Locked means the mouse goes through it to the game underneath.
            flags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        ImGui.SetNextWindowBgAlpha(Repositioning ? 0.85f : Config.WindowBackgroundAlpha);

        // Only correct the window when it has drifted from where it was left.
        // Setting the position unconditionally would swallow the drag, because
        // ImGui applies the mouse movement before Begin and this would overwrite
        // it on the same frame.
        if (Config.WindowPlaced)
        {
            var want = new Vector2(Config.WindowX, Config.WindowY);
            if (!_seen || Vector2.Distance(want, _lastPos) > 0.5f)
                ImGui.SetNextWindowPos(want, ImGuiCond.Always);
        }

        if (!ImGui.Begin("###VentureBellOverlay", flags))
        {
            ImGui.End();
            return;
        }

        // Where it ended up, which the next frame compares its anchor against.
        var previous   = _lastPos;
        var firstFrame = !_seen;
        _lastPos = ImGui.GetWindowPos();
        _seen    = true;

        // A drag is the only thing allowed to decide where the list lives;
        // anything else that can move a window is a side effect to be undone on
        // the next frame. The first frame seeds the anchor from wherever ImGui
        // put it, so an existing placement is kept rather than overwritten.
        var dragged = _lastPos != previous && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        if (!Config.WindowPlaced || (dragged && !firstFrame))
            Remember(_lastPos);

        if (Repositioning)
            ImGui.TextColored(Done, "Drag me. Click \"Anchor\" in the settings when done.");

        if (_cached.Count == 0)
        {
            ImGui.TextColored(Muted, "No venture timers yet — visit a summoning bell.");
            ImGui.End();
            return;
        }

        // A table rather than SameLine: the times line up under each other, which
        // is the whole reason to glance at this instead of opening the bell.
        var columns = Config.ShowVentureNames ? 3 : 2;
        if (ImGui.BeginTable("##ventures", columns, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("name");
            if (columns == 3)
                ImGui.TableSetupColumn("venture");
            ImGui.TableSetupColumn("time", ImGuiTableColumnFlags.WidthFixed);

            foreach (var r in _cached)
                Row(r, columns);

            ImGui.EndTable();
        }

        ImGui.End();
        Flush();
    }

    /// <summary>Records a new position, without writing the config yet.</summary>
    private void Remember(Vector2 pos)
    {
        if (Config.WindowPlaced
            && MathF.Abs(Config.WindowX - pos.X) < 0.5f
            && MathF.Abs(Config.WindowY - pos.Y) < 0.5f)
            return;

        Config.WindowPlaced = true;
        Config.WindowX      = pos.X;
        Config.WindowY      = pos.Y;
        _movedAt            = DateTime.UtcNow;
    }

    /// <summary>Writes it a second after the dragging stops.</summary>
    private void Flush()
    {
        if (_movedAt == DateTime.MinValue) return;
        if (DateTime.UtcNow - _movedAt < TimeSpan.FromSeconds(1)) return;

        _movedAt = DateTime.MinValue;
        _plugin.SaveConfig();
    }

    private void Row(RetainerVenture r, int columns)
    {
        var remaining = r.DoneAt == 0 ? TimeSpan.Zero : DateTimeOffset.FromUnixTimeSeconds(r.DoneAt) - DateTimeOffset.Now;
        var complete  = r.DoneAt != 0 && remaining <= TimeSpan.Zero;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(r.DoneAt == 0 ? Muted : Pending, r.Name);

        if (columns == 3)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, r.Venture);
        }

        ImGui.TableNextColumn();
        var (colour, text) = r.DoneAt == 0 ? (Muted, "idle")
                           : complete      ? (Done, "back")
                                           : (Pending, Format(remaining));

        // Right-aligned, so the numbers form a column rather than trailing the
        // venture names.
        var offset = ImGui.GetColumnWidth() - ImGui.CalcTextSize(text).X;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextColored(colour, text);
    }

    /// <summary>
    /// "2h07m", "47m", "<1m" — wide enough to plan around, short enough to read
    /// without stopping.
    /// </summary>
    internal static string Format(TimeSpan left)
    {
        if (left.TotalHours >= 1)
            return $"{(int)left.TotalHours}h{left.Minutes:00}m";
        if (left.TotalMinutes >= 1)
            return $"{(int)left.TotalMinutes}m";
        return "<1m";
    }

    private bool ShouldShow()
    {
        if (Repositioning)
            return true;
        if (_plugin.IsInDuty && Config.HideInDuty)
            return false;

        return Config.WindowCondition switch
        {
            VentureWindowCondition.AnyComplete => AnyComplete(),
            VentureWindowCondition.AllComplete => AllComplete(),
            _                                  => true,
        };
    }

    private bool AnyComplete()
    {
        foreach (var r in _cached)
            if (Complete(r)) return true;
        return false;
    }

    private bool AllComplete()
    {
        var running = 0;
        foreach (var r in _cached)
        {
            if (r.DoneAt == 0) continue;
            running++;
            if (!Complete(r)) return false;
        }
        return running > 0;
    }

    private static bool Complete(RetainerVenture r)
        => r.DoneAt != 0 && DateTimeOffset.FromUnixTimeSeconds(r.DoneAt) <= DateTimeOffset.Now;

    private void Refresh()
    {
        if (DateTime.UtcNow - _lastRead < RefreshInterval)
            return;

        _lastRead = DateTime.UtcNow;
        _cached   = _reader.Read(Plugin.PlayerState)?.Retainers ?? Array.Empty<RetainerVenture>();
    }

    public void Dispose() { }
}
