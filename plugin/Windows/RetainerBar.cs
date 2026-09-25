using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace VentureBell.Windows;

/// <summary>
/// A strip of buttons above the summoning bell's retainer list, so sending
/// everyone out again does not mean opening a settings window first. It exists
/// only while that list is on screen and follows it around.
/// </summary>
internal sealed class RetainerBar : IDisposable
{
    private const string Overview = "RetainerList";

    private static readonly Vector4 Muted  = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 Ready  = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 Accent = new(1f, 0.8f, 0.3f, 1f);

    private readonly Plugin         _plugin;
    private readonly RetainerReader _reader;
    private Configuration Config => _plugin.Config;

    private float                          _height = 40f;
    private DateTime                       _readAt = DateTime.MinValue;
    private IReadOnlyList<RetainerVenture> _cached = Array.Empty<RetainerVenture>();

    internal RetainerBar(Plugin plugin, RetainerReader reader)
    {
        _plugin = plugin;
        _reader = reader;
    }

    internal unsafe void Draw()
    {
        if (!Config.ShowRetainerBar)
            return;

        var list = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Overview).Address;
        if (list is null || !list->IsVisible)
            return;

        Refresh();
        if (_cached.Count == 0)
            return;

        // Anchored to the game's own window, using last frame's height: working
        // it out from line heights left the bar floating well above the list.
        ImGui.SetNextWindowPos(new Vector2(list->X, list->Y - _height - 2f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);

        var flags = ImGuiWindowFlags.NoDecoration
                  | ImGuiWindowFlags.AlwaysAutoResize
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("###VentureBellBar", flags))
        {
            ImGui.End();
            return;
        }

        Header();

        if (ImGui.BeginTable("##bar", 3, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("name");
            ImGui.TableSetupColumn("state", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("actions");

            foreach (var retainer in _cached)
                Row(retainer);

            ImGui.EndTable();
        }

        _height = ImGui.GetWindowHeight();
        ImGui.End();
    }

    /// <summary>Everyone at once, and how many that is.</summary>
    private void Header()
    {
        var free = 0;
        foreach (var r in _cached)
            if (r.DoneAt <= DateTimeOffset.Now.ToUnixTimeSeconds())
                free++;

        ImGui.TextColored(Accent, "Venture Bell");
        ImGui.SameLine();
        ImGui.TextDisabled(free == 0 ? "· all out" : $"· {free} free");

        ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 2f);
        ImGui.BeginDisabled(free == 0);
        if (ImGui.SmallButton($"all: {Family()}"))
            Queue(best: true);
        ImGui.SameLine();
        if (ImGui.SmallButton("all: quick"))
            Queue(best: false);
        ImGui.EndDisabled();

        ImGui.Separator();
    }

    private void Row(RetainerVenture retainer)
    {
        var running = retainer.DoneAt > DateTimeOffset.Now.ToUnixTimeSeconds();
        var best    = _plugin.Resolver.BestExploration(retainer.ClassJob, retainer.Level);
        var quick   = _plugin.Resolver.QuickExploration();

        ImGui.TableNextRow();
        ImGui.PushID(retainer.Name);

        ImGui.TableNextColumn();
        ImGui.TextColored(running ? Muted : Ready, retainer.Name);

        ImGui.TableNextColumn();
        if (running)
            ImGui.TextDisabled(VentureWindow.Format(DateTimeOffset.FromUnixTimeSeconds(retainer.DoneAt) - DateTimeOffset.Now));
        else if (best is not null)
            ImGui.TextDisabled(best.Value.Name);

        ImGui.TableNextColumn();
        ImGui.BeginDisabled(running);
        if (best is not null && ImGui.SmallButton(VentureName.Word(best.Value.Name)))
            _plugin.Assign(retainer.Name, best.Value, Collectable(retainer));
        ImGui.SameLine();
        if (quick is not null && ImGui.SmallButton("quick"))
            _plugin.Assign(retainer.Name, quick.Value, Collectable(retainer));
        ImGui.EndDisabled();

        ImGui.PopID();
    }

    private void Queue(bool best)
    {
        foreach (var r in _cached)
        {
            if (r.DoneAt > DateTimeOffset.Now.ToUnixTimeSeconds())
                continue;

            var venture = best ? _plugin.Resolver.BestExploration(r.ClassJob, r.Level)
                               : _plugin.Resolver.QuickExploration();
            if (venture is not null)
                _plugin.Assign(r.Name, venture.Value, Collectable(r));
        }
    }

    /// <summary>A finished venture has to be collected before another is assigned.</summary>
    private static bool Collectable(RetainerVenture r)
        => r.DoneAt > 0 && r.DoneAt <= DateTimeOffset.Now.ToUnixTimeSeconds();

    /// <summary>
    /// The word the retainers agree on. Everyone counts, not only the free
    /// ones: with all of them out the button would otherwise fall back to
    /// "exploration" and read as if it meant something else.
    /// </summary>
    private string Family()
    {
        var word = "";
        foreach (var r in _cached)
        {
            var best = _plugin.Resolver.BestExploration(r.ClassJob, r.Level);
            if (best is null)
                continue;

            var candidate = VentureName.Word(best.Value.Name);
            if (word.Length == 0)
                word = candidate;
            else if (word != candidate)
                return "exploration";   // A mixed bag of jobs.
        }

        return word.Length == 0 ? "exploration" : word;
    }

    private void Refresh()
    {
        if (DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(1))
            return;

        _readAt = DateTime.UtcNow;
        _cached = _reader.Read(Plugin.PlayerState)?.Retainers ?? Array.Empty<RetainerVenture>();
    }

    public void Dispose() { }
}
