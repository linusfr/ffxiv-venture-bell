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

        ImGui.SetNextWindowSize(new Vector2(480, 440), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Venture Bell###VentureBellSettings", ref _isVisible))
        {
            ImGui.End();
            return;
        }

        // One tab per half, because they are independent: the list works with
        // nothing configured, and the notifications need no list.
        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem("On-screen list"))
            {
                DrawWindowTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Notifications"))
            {
                DrawNotificationsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // ── The list ──────────────────────────────────────────────────────────────
    private void DrawWindowTab()
    {
        ImGui.Spacing();
        Toggle("Show the list on screen", Config.ShowWindow, v => Config.ShowWindow = v);
        ImGui.TextDisabled("  Reads the game only. Needs no server and no account.");

        ImGui.BeginDisabled(!Config.ShowWindow);

        Section("When");
        var condition = (int)Config.WindowCondition;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##condition", ref condition, "Always\0When a venture is back\0When all are back\0"))
        {
            Config.WindowCondition = (VentureWindowCondition)condition;
            _plugin.SaveConfig();
        }
        Toggle("Hide it in duties and cutscenes", Config.HideInDuty, v => Config.HideInDuty = v);

        Section("Appearance");
        Toggle("Name the venture, not just the time", Config.ShowVentureNames, v => Config.ShowVentureNames = v);

        var size = Config.WindowFontSize;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##fontsize", ref size, 10f, 36f, "text %.0f px"))
        {
            Config.WindowFontSize = size;
            _plugin.SaveConfig();
        }

        var alpha = Config.WindowBackgroundAlpha;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##alpha", ref alpha, 0f, 1f, "background %.2f"))
        {
            Config.WindowBackgroundAlpha = alpha;
            _plugin.SaveConfig();
        }

        Section("Placement");
        // Locked is click-through, so moving it has to be asked for.
        if (_plugin.Repositioning)
        {
            if (ImGui.Button("Anchor it here"))
            {
                _plugin.Repositioning = false;
                Config.WindowLocked   = true;
                _plugin.SaveConfig();
            }
            ImGui.SameLine();
            ImGui.TextColored(Warning, "Drag the list, then anchor it.");
        }
        else
        {
            if (ImGui.Button("Move it"))
            {
                _plugin.Repositioning = true;
                Config.WindowLocked   = false;
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Locked, so clicks pass through to the game.");
        }

        ImGui.EndDisabled();
    }

    // ── The notifications ─────────────────────────────────────────────────────
    private void DrawNotificationsTab()
    {
        ImGui.Spacing();
        Toggle("Send my timers to a server", Config.NotificationsEnabled, v => Config.NotificationsEnabled = v);
        ImGui.TextDisabled("  So a venture finishing reaches your phone with the game closed.");

        ImGui.BeginDisabled(!Config.NotificationsEnabled);

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
        ImGui.TextDisabled("The server stores no account of its own, so these are what it");
        ImGui.TextDisabled("notifies you with. Both are needed.");

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

        Toggle("Log every sync to /xllog", Config.DebugMode, v => Config.DebugMode = v);

        Section("Actions");
        if (ImGui.Button("Send now"))
            _plugin.SyncNow();
        ImGui.SameLine();
        if (ImGui.Button("Check connection"))
            _plugin.CheckConnection();
        ImGui.SameLine();
        ImGui.BeginDisabled(!Config.HasPushover);
        if (ImGui.Button("Test notification"))
            _plugin.SendTestNotification();
        ImGui.EndDisabled();

        // Every button reports here, next to the buttons.
        ImGui.Spacing();
        ImGui.TextColored(Heading, "Last action");
        ImGui.TextWrapped(_plugin.Status);

        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(
            "Sends your character name, your retainers' names, their venture completion times and " +
            "your Pushover key to the address above. Nothing else, nowhere else, and nothing at all " +
            "until a server and token are set.");
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
