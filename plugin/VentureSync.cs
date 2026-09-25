using System;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace VentureBell;

/// <summary>What the settings window's indicator shows.</summary>
public enum LinkState
{
    /// Nothing has been tried yet, or there is nothing configured to try.
    Unknown,
    Checking,
    Connected,
    Unreachable,
}

/// <summary>
/// Decides when the server needs to hear from us: closing the Timers window or
/// a summoning bell, and a slow poll for everything else — a venture assigned
/// with the list open, or a server that was down for the first attempt.
/// </summary>
internal sealed class VentureSync : IDisposable
{
    // Both windows make the client ask the server for venture timers, which is
    // the only way the data arrives. Timers works from anywhere, so it is the
    // one people actually use; the bell is where the timers change.
    private static readonly string[] TimerAddons = { "RetainerList", "ContentsInfo" };

    private readonly Configuration   _config;
    private readonly RetainerReader  _reader;
    private readonly BellClient      _client;
    private readonly IFramework      _framework;
    private readonly IAddonLifecycle _addons;
    private readonly IPlayerState    _player;
    private readonly IPluginLog      _log;

    private readonly CancellationTokenSource _shutdown = new();

    private DateTime _lastCheck = DateTime.MinValue;
    private string   _lastSent  = "";
    private int      _inFlight;

    /// <summary>The last thing that happened, for the settings window to show.</summary>
    internal string Status { get; private set; } = "Nothing sent yet.";

    /// <summary>Whether the server answered the last time we spoke to it.</summary>
    internal LinkState Link { get; private set; } = LinkState.Unknown;

    internal VentureSync(
        Configuration config, RetainerReader reader, BellClient client,
        IFramework framework, IAddonLifecycle addons, IPlayerState player, IPluginLog log)
    {
        _config    = config;
        _reader    = reader;
        _client    = client;
        _framework = framework;
        _addons    = addons;
        _player    = player;
        _log       = log;

        _framework.Update += OnUpdate;
        // PreFinalize: on the way out, what you just assigned is in memory.
        _addons.RegisterListener(AddonEvent.PreFinalize, TimerAddons, OnTimersClosing);
    }

    private void OnTimersClosing(AddonEvent type, AddonArgs args)
    {
        _config.Debug(_log, $"VentureBell: {args.AddonName} closing, checking timers.");
        Check(force: false);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!_config.IsConfigured)
            return;

        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.PollSeconds));
        if (DateTime.UtcNow - _lastCheck < interval)
            return;

        _lastCheck = DateTime.UtcNow;
        Check(force: false);
    }

    /// <summary>Reads the game and sends if anything changed.
    /// <paramref name="force"/> sends regardless, for the button and
    /// /venturebell sync.</summary>
    internal void Check(bool force)
    {
        if (!_config.IsConfigured)
        {
            if (force)
                Status = "Set a server address and a token first.";
            return;
        }

        var snapshot = _reader.Read(_player);
        if (snapshot is null)
        {
            if (force)
                Status = "The game has no retainer timers yet — visit a summoning bell once.";
            return;
        }

        // Credentials are part of the fingerprint, or changing them would wait
        // for a venture to change before reaching the server.
        var fingerprint = snapshot.Value.Fingerprint + "|" + _config.PushoverUser + "|" + _config.PushoverToken;
        if (!force && fingerprint == _lastSent)
            return;

        // One at a time, or a server that stopped answering collects a request
        // per poll.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        if (force)
            Status = "Sending…";
        _config.Debug(_log, $"VentureBell: sending {snapshot.Value.Retainers.Count} retainers for {snapshot.Value.Character}.");
        _ = SendAsync(snapshot.Value, fingerprint);
    }

    /// <summary>Whether the server is reachable and the token accepted, without
    /// sending anything. What the settings indicator is built on.</summary>
    internal void CheckConnection()
    {
        if (!_config.IsConfigured)
        {
            Link   = LinkState.Unknown;
            Status = "Set a server address and a token first.";
            return;
        }

        Link   = LinkState.Checking;
        Status = "Checking…";
        _ = CheckAsync();
    }

    /// <summary>Asks the server to send one notification with the configured
    /// credentials, so the user can see whether they work.</summary>
    internal void SendTestNotification()
    {
        if (!_config.IsConfigured)
        {
            Status = "Set a server address and a token first.";
            return;
        }

        Status = "Sending a test notification…";
        _ = TestAsync();
    }

    private async Task TestAsync()
    {
        try
        {
            var snapshot  = _reader.Read(_player);
            var character = snapshot?.Character ?? "Venture Bell";

            var error = await _client.TestAsync(_config, character, _shutdown.Token).ConfigureAwait(false);
            if (error is null)
            {
                Link   = LinkState.Connected;
                Status = $"Test sent at {DateTime.Now:HH:mm:ss} — check your Pushover client.";
            }
            else
            {
                Status = $"Test failed — {error}";
                _log.Warning("VentureBell: test notification failed — " + error);
            }
        }
        catch (OperationCanceledException)
        {
            // Unloading mid-request.
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var error = await _client.CheckAsync(_config, _shutdown.Token).ConfigureAwait(false);
            if (error is null)
            {
                Link   = LinkState.Connected;
                Status = $"Connected at {DateTime.Now:HH:mm:ss}.";
            }
            else
            {
                Link   = LinkState.Unreachable;
                Status = error;
            }
        }
        catch (OperationCanceledException)
        {
            // Unloading mid-request.
        }
    }

    private async Task SendAsync(Snapshot snapshot, string fingerprint)
    {
        try
        {
            var error = await _client
                .SendAsync(_config, snapshot, _shutdown.Token)
                .ConfigureAwait(false);

            if (error is null)
            {
                // Only a delivered payload counts, so a failure is retried.
                _lastSent = fingerprint;
                Link      = LinkState.Connected;
                Status    = $"Sent {snapshot.Retainers.Count} retainers at {DateTime.Now:HH:mm:ss}.";
                _config.Debug(_log, "VentureBell: " + Status);
            }
            else
            {
                _lastSent = "";
                Link      = LinkState.Unreachable;
                Status    = $"Failed at {DateTime.Now:HH:mm:ss} — {error}";
                _log.Warning("VentureBell: sync failed — " + error);
            }
        }
        catch (OperationCanceledException)
        {
            // Unloading mid-request; the window is already gone.
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _addons.UnregisterListener(AddonEvent.PreFinalize, TimerAddons, OnTimersClosing);

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
