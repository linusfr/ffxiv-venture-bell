using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace VentureBell.Windows;

public sealed class ConfigurationWindow : IDisposable
{
    private static readonly Vector4 Heading = new(1f, 0.8f, 0.3f, 1f);

    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    private bool _isVisible;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    // Off by default so the token is not sitting on screen during a stream.
    private bool _showToken;

    public ConfigurationWindow(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void Draw()
    {
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
        Text(Config.ServerUrl, v => Config.ServerUrl = v, "##url", "http://127.0.0.1:8770");

        ImGui.Spacing();
        ImGui.TextDisabled("The server's BELL_TOKEN.");
        Text(Config.Token, v => Config.Token = v, "##token", "",
             _showToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
        ImGui.Checkbox("Show token", ref _showToken);

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
        ImGui.TextDisabled("Sends the current timers even if nothing changed.");

        ImGui.Spacing();
        ImGui.TextWrapped(_plugin.Status);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(
            "Sends your character name, your retainers' names and their venture completion times " +
            "to the address above. Nothing else, nowhere else, and nothing at all until both fields are set.");

        ImGui.End();
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
