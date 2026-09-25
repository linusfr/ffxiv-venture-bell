using Dalamud.Configuration;
using Dalamud.Plugin.Services;

namespace VentureBell;

/// <summary>When the on-screen list is shown.</summary>
public enum VentureWindowCondition
{
    Always,
    AnyComplete,
    AllComplete,
}

[System.Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// The notification half: syncing timers to a server. Off stops every sync;
    /// the on-screen list is unaffected, and works with nothing configured.
    public bool NotificationsEnabled { get; set; } = true;

    /// Was the master switch before the two halves became independent. Read once
    /// on load, then cleared.
    [System.Obsolete("Migrated to NotificationsEnabled.")]
    public bool? Enabled { get; set; }

    /// Base address of your venturebell server, e.g. "http://127.0.0.1:8770".
    /// Empty until you set it, and an empty address means nothing is ever sent.
    public string ServerUrl { get; set; } = "";

    /// The server's BELL_TOKEN, sent as "Authorization: Bearer …".
    public string Token { get; set; } = "";

    /// Your Pushover user key. Sent with every sync: the server holds no
    /// credentials, which is what lets one serve several people.
    public string PushoverUser { get; set; } = "";

    /// Your Pushover application's API token. Needed alongside the user key.
    public string PushoverToken { get; set; } = "";

    /// Both halves, or the server has nothing to notify you with.
    internal bool HasPushover => PushoverUser.Length > 0 && PushoverToken.Length > 0;

    /// Seconds between checks. A memory read and a comparison; a request only
    /// goes out when something changed.
    public int PollSeconds { get; set; } = 60;

    /// On: take the highest venture the retainer actually qualifies for, which
    /// is whatever the game offers. Off: stop instead, since a lower tier on
    /// offer means the gear is behind the level and you may want to fix that.
    public bool TakeHighestQualified { get; set; } = true;

    /// Show the buttons above the summoning bell's retainer list.
    public bool ShowRetainerBar { get; set; } = true;

    /// Show the on-screen list of what the retainers are doing.
    public bool ShowWindow { get; set; } = true;

    /// When that list is on screen.
    public VentureWindowCondition WindowCondition { get; set; } = VentureWindowCondition.Always;

    /// Where the list was last left. Here rather than in ImGui's ini, which
    /// survives neither a reload nor a reinstall.
    public bool  WindowPlaced { get; set; }
    public float WindowX      { get; set; }
    public float WindowY      { get; set; }

    /// Locked: it will not move and clicks pass through to the game.
    public bool WindowLocked { get; set; } = true;

    /// Text size in pixels. A real size rather than a scale factor, which
    /// stretches the glyphs and goes soft above 1x.
    public float WindowFontSize { get; set; } = 16f;

    /// Turn the whole list green while a venture is back, rather than leaving it
    /// to the word next to the retainer. Off by default: it is loud on purpose.
    public bool HighlightWhenBack { get; set; }

    /// Background opacity. 0 is nothing but text over the game.
    public float WindowBackgroundAlpha { get; set; } = 0.35f;

    /// Name the venture next to the retainer, not just the time.
    public bool ShowVentureNames { get; set; } = true;

    /// Keep it out of the way while you are in a duty.
    public bool HideInDuty { get; set; } = true;

    /// Log every sync, and what was in it, to the Dalamud log.
    public bool DebugMode { get; set; } = false;

    internal void Debug(IPluginLog log, string message)
    {
        if (DebugMode)
            log.Information(message);
    }

    /// Nothing is sent without an address and a token.
    internal bool IsConfigured => NotificationsEnabled && ServerUrl.Length > 0 && Token.Length > 0;

    /// <summary>Carries a pre-2 setting over. Returns true when something moved.</summary>
    internal bool Migrate()
    {
#pragma warning disable CS0618
        if (Enabled is not bool legacy)
            return false;

        NotificationsEnabled = legacy;
        Enabled              = null;
#pragma warning restore CS0618
        Version = 2;
        return true;
    }
}
