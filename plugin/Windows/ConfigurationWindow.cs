using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace VentureBell.Windows;

public sealed class ConfigurationWindow : IDisposable
{
    private static readonly Vector4 Heading = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Warning = new(0.95f, 0.6f, 0.25f, 1f);

    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    private bool _isVisible;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    // Off by default so the token is not sitting on screen during a stream.
    private bool _showToken;
    private bool _showPushoverToken;

    // The indicator is only meaningful if something checked recently, and the
    // window opening is the moment someone wants to know.
    private bool _wasVisible;

    public ConfigurationWindow(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void Draw()
    {
        if (IsVisible && !_wasVisible)
            _plugin.CheckConnection();
        _wasVisible = IsVisible;

        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(480, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Venture Bell###VentureBellSettings", ref _isVisible))
        {
            ImGui.End();
            return;
        }

        Toggle("Enable plugin", Config.Enabled, v => Config.Enabled = v);

        ImGui.BeginDisabled(!Config.Enabled);

        Section("Server");
        ImGui.TextDisabled("Where your venturebell server is listening.");
        Text(Config.ServerUrl, v => Config.ServerUrl = v, "##url", "https://venture-bell.example.com");

        ImGui.Spacing();
        ImGui.TextDisabled("The server's BELL_TOKEN.");
        Text(Config.Token, v => Config.Token = v, "##token", "",
             _showToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
        ImGui.Checkbox("Show token", ref _showToken);

        ImGui.Spacing();
        Link();

        Section("Pushover");
        ImGui.TextDisabled("The server stores no Pushover account of its own, so these are");
        ImGui.TextDisabled("what it notifies you with. Both are needed.");

        ImGui.Spacing();
        ImGui.TextDisabled("Application API token — pushover.net/apps/build, any name.");
        Text(Config.PushoverToken, v => Config.PushoverToken = v, "##pushover-token", "",
             _showPushoverToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
        ImGui.Checkbox("Show application token", ref _showPushoverToken);

        ImGui.Spacing();
        ImGui.TextDisabled("User key — top right of the Pushover dashboard.");
        Text(Config.PushoverUser, v => Config.PushoverUser = v, "##pushover-user", "uxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");

        if (!Config.HasPushover)
            ImGui.TextColored(Warning, "  Without both, the server has nowhere to send your notifications.");

        Section("When to sync");
        ImGui.TextDisabled("  Always on closing the summoning bell, and this often otherwise.");
        var poll = Config.PollSeconds;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##poll", ref poll, 15, 300, "%d seconds"))
        {
            Config.PollSeconds = poll;
            _plugin.SaveConfig();
        }
        ImGui.TextDisabled("  A request only goes out when a timer has actually changed.");

        Section("Debug");
        Toggle("Log every sync", Config.DebugMode, v => Config.DebugMode = v);
        ImGui.TextDisabled("  Writes to the Dalamud log (/xllog).");

        ImGui.EndDisabled();

        Section("Test");
        if (ImGui.Button("Send now"))
            _plugin.SyncNow();
        ImGui.SameLine();
        if (ImGui.Button("Check connection"))
            _plugin.CheckConnection();
        ImGui.TextDisabled("  \"Send now\" pushes the current timers even if nothing changed.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(
            "Sends your character name, your retainers' names, their venture completion times and " +
            "your Pushover key to the address above. Nothing else, nowhere else, and nothing at all " +
            "until a server and token are set.");

        ImGui.End();
    }

    /// <summary>
    /// A dot and a line of text for whether the server is answering. Drawn
    /// rather than written as a glyph, so it cannot come out as tofu in a font
    /// that lacks one.
    /// </summary>
    private void Link()
    {
        var (colour, label) = _plugin.Link switch
        {
            LinkState.Connected   => (new Vector4(0.3f, 0.8f, 0.4f, 1f), "Connected"),
            LinkState.Unreachable => (new Vector4(0.9f, 0.35f, 0.35f, 1f), "Not reachable"),
            LinkState.Checking    => (new Vector4(0.9f, 0.8f, 0.3f, 1f), "Checking…"),
            _                     => (new Vector4(0.6f, 0.6f, 0.6f, 1f), "Not checked"),
        };

        var radius = ImGui.GetTextLineHeight() * 0.28f;
        var origin = ImGui.GetCursorScreenPos();
        var centre = origin with { X = origin.X + radius, Y = origin.Y + ImGui.GetTextLineHeight() * 0.5f };
        ImGui.GetWindowDrawList().AddCircleFilled(centre, radius, ImGui.GetColorU32(colour));

        ImGui.Dummy(new Vector2(radius * 2f, ImGui.GetTextLineHeight()));
        ImGui.SameLine();
        ImGui.TextColored(colour, label);

        // The detail line carries the reason a red dot is red.
        ImGui.TextDisabled("  " + _plugin.Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static void Section(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(Heading, label);
        ImGui.Separator();
    }

    private void Toggle(string label, bool value, Action<bool> write)
    {
        var local = value;
        if (!ImGui.Checkbox(label, ref local)) return;
        write(local);
        _plugin.SaveConfig();
    }

    private void Text(string value, Action<string> write, string id, string hint,
                      ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var local = value;
        ImGui.SetNextItemWidth(-1);
        var changed = hint.Length > 0
            ? ImGui.InputTextWithHint(id, hint, ref local, 256, flags)
            : ImGui.InputText(id, ref local, 256, flags);
        if (!changed) return;
        write(local);
        _plugin.SaveConfig();
    }

    public void Dispose() { }
}
