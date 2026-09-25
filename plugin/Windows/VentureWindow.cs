using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace VentureBell.Windows;

/// <summary>
/// A small list of what the retainers are doing and when they are back. No
/// title bar, no buttons: the numbers, and nothing around them.
/// </summary>
internal sealed class VentureWindow : IDisposable
{
    private static readonly Vector4 Done    = new(0.45f, 0.85f, 0.5f, 1f);
    private static readonly Vector4 Pending = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 Muted   = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin         _plugin;
    private readonly RetainerReader _reader;
    private Configuration Config => _plugin.Config;

    // Minute resolution, so reading more often buys nothing — and five seconds
    // still catches a venture assigned at the bell before you look away.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private DateTime                  _lastRead = DateTime.MinValue;
    private IReadOnlyList<RetainerVenture> _cached = Array.Empty<RetainerVenture>();

    // Where it was last frame, and when it last moved: saving per frame would
    // rewrite the config a hundred times for one drag.
    private Vector2  _lastPos;
    private bool     _seen;
    private DateTime _movedAt    = DateTime.MinValue;
    private DateTime _hurryUntil = DateTime.MinValue;

    // Rebuilt when the size changes, rather than stretching one bitmap font.
    private IFontHandle? _font;
    private float        _fontSize;
    private DateTime     _fontSettledAt = DateTime.MinValue;

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

        EnsureFont();
        using var font = _font!.Push();

        // Identical in both modes but for the inputs: what you drag has to be
        // the size of what you get, or it lands somewhere else.
        var flags = ImGuiWindowFlags.NoDecoration
                  | ImGuiWindowFlags.AlwaysAutoResize
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoNav;
        // With nothing to show, the list is a button instead — so it has to be
        // clickable even when locked.
        var asButton = _cached.Count == 0;
        if (!Repositioning && Config.WindowLocked && !asButton)
            // Locked means the mouse goes through it to the game underneath.
            flags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        // All move mode changes: a background you can aim at, costing no space.
        ImGui.SetNextWindowBgAlpha(Repositioning ? 0.85f : Config.WindowBackgroundAlpha);
        if (Repositioning)
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.20f, 0.35f, 0.25f, 1f));

        // Only when it has drifted. Setting it unconditionally would swallow
        // the drag, which ImGui applies before Begin.
        if (Config.WindowPlaced)
        {
            var want = new Vector2(Config.WindowX, Config.WindowY);
            if (!_seen || Vector2.Distance(want, _lastPos) > 0.5f)
                ImGui.SetNextWindowPos(want, ImGuiCond.Always);
        }

        var open = ImGui.Begin("###VentureBellOverlay", flags);
        if (Repositioning)
            ImGui.PopStyleColor();
        if (!open)
        {
            ImGui.End();
            return;
        }

        // Where it ended up, for the next frame to compare against.
        var previous   = _lastPos;
        var firstFrame = !_seen;
        _lastPos = ImGui.GetWindowPos();
        _seen    = true;

        // Only a drag decides where the list lives; anything else that moves a
        // window is a side effect, undone next frame. The first frame seeds from
        // wherever ImGui put it, so an existing placement survives.
        var dragged = _lastPos != previous && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        if (!Config.WindowPlaced || (dragged && !firstFrame))
            Remember(_lastPos);

        if (asButton)
        {
            if (ImGui.Button("Load venture timers"))
            {
                _plugin.LoadTimers();
                // Read more often for a moment: the answer lands in a few
                // hundred milliseconds and waiting five seconds for a button to
                // do something feels like it did not work.
                _hurryUntil = DateTime.UtcNow.AddSeconds(5);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Opens and closes the Timers window, which is what makes the game send them.");

            ImGui.End();
            Flush();
            return;
        }

        // A table rather than SameLine, so the times line up under each other.
        // The column is measured here: right-aligning against the space left in
        // the cell feeds back into an auto-resizing window — cursor right widens
        // the window widens the cell — and the list creeps sideways forever.
        var timeWidth = 0f;
        foreach (var r in _cached)
            timeWidth = MathF.Max(timeWidth, ImGui.CalcTextSize(Remaining(r).Text).X);

        var columns = Config.ShowVentureNames ? 3 : 2;
        if (ImGui.BeginTable("##ventures", columns, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("name");
            if (columns == 3)
                ImGui.TableSetupColumn("venture");
            ImGui.TableSetupColumn("time", ImGuiTableColumnFlags.WidthFixed, timeWidth);

            foreach (var r in _cached)
                Row(r, columns, timeWidth);

            ImGui.EndTable();
        }

        ImGui.End();
        Flush();
    }

    /// <summary>Records a position without writing the config yet.</summary>
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

    /// <summary>What the time column says for one retainer, and in what colour.</summary>
    private static (string Text, Vector4 Colour) Remaining(RetainerVenture r)
    {
        if (r.DoneAt == 0)
            return ("idle", Muted);

        var left = DateTimeOffset.FromUnixTimeSeconds(r.DoneAt) - DateTimeOffset.Now;
        return left <= TimeSpan.Zero ? ("back", Done) : (Format(left), Pending);
    }

    private void Row(RetainerVenture r, int columns, float timeWidth)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(r.DoneAt == 0 ? Muted : Pending, r.Name);

        if (columns == 3)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, r.Venture);
        }

        ImGui.TableNextColumn();
        var (text, colour) = Remaining(r);

        // Against the measured width, a constant for the frame.
        var offset = timeWidth - ImGui.CalcTextSize(text).X;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextColored(colour, text);
    }

    /// <summary>"2h07m", "47m", "&lt;1m" — enough to plan around at a glance.</summary>
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
        var interval = DateTime.UtcNow < _hurryUntil ? TimeSpan.FromMilliseconds(250) : RefreshInterval;
        if (DateTime.UtcNow - _lastRead < interval)
            return;

        _lastRead = DateTime.UtcNow;
        _cached   = _reader.Read(Plugin.PlayerState)?.Retainers ?? Array.Empty<RetainerVenture>();
    }

    private void EnsureFont()
    {
        if (_font is not null && MathF.Abs(_fontSize - Config.WindowFontSize) < 0.01f)
        {
            _fontSettledAt = DateTime.MinValue;
            return;
        }

        // The slider reports every frame it is held and each size is an atlas
        // build, so wait for the number to stop moving.
        if (_font is not null)
        {
            if (_fontSettledAt == DateTime.MinValue)
            {
                _fontSettledAt = DateTime.UtcNow;
                return;
            }
            if (DateTime.UtcNow - _fontSettledAt < TimeSpan.FromMilliseconds(250))
                return;
        }

        _fontSettledAt = DateTime.MinValue;
        _font?.Dispose();
        _fontSize = Config.WindowFontSize;
        _font = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(_fontSize, null)));
    }

    public void Dispose() => _font?.Dispose();
}
