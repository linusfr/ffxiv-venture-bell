using Dalamud.Configuration;
using Dalamud.Plugin.Services;

namespace VentureBell;

[System.Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// Master switch. Off stops every sync; the plugin stays loaded and sends nothing.
    public bool Enabled { get; set; } = true;

    /// Base address of your venturebell server, e.g. "http://127.0.0.1:8770".
    /// Empty until you set it, and an empty address means nothing is ever sent.
    public string ServerUrl { get; set; } = "";

    /// The server's BELL_TOKEN, sent as "Authorization: Bearer …".
    public string Token { get; set; } = "";

    /// Seconds between checks for changed venture timers. The check is a memory read
    /// and a comparison; a request only goes out when something actually changed.
    public int PollSeconds { get; set; } = 60;

    /// Log every sync, and what was in it, to the Dalamud log.
    public bool DebugMode { get; set; } = false;

    internal void Debug(IPluginLog log, string message)
    {
        if (DebugMode)
            log.Information(message);
    }

    /// Nothing is sent without somewhere to send it and something to authenticate with.
    internal bool IsConfigured => Enabled && ServerUrl.Length > 0 && Token.Length > 0;
}
