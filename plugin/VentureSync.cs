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
/// Decides when the server needs to hear from us. Two triggers: closing the
/// summoning bell, which is when timers have just changed, and a slow poll that
/// catches everything else — a venture assigned from a retainer already open, a
/// server that was down when the first attempt went out.
/// </summary>
internal sealed class VentureSync : IDisposable
{
    private const string RetainerListAddon = "RetainerList";

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
        // PreFinalize rather than PostSetup: on the way out, whatever you just
        // assigned is already in memory.
        _addons.RegisterListener(AddonEvent.PreFinalize, RetainerListAddon, OnRetainerListClosing);
    }

    private void OnRetainerListClosing(AddonEvent type, AddonArgs args)
    {
        _config.Debug(_log, "VentureBell: retainer list closing, checking timers.");
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

    /// <summary>
    /// Reads the game and sends if anything changed. <paramref name="force"/>
    /// sends even when nothing has, which is what the settings window's button
    /// and the /venturebell sync command are for.
    /// </summary>
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

        var fingerprint = snapshot.Value.Fingerprint;
        if (!force && fingerprint == _lastSent)
            return;

        // One request at a time. Without this, a server that has stopped
        // answering collects a pending request per poll.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        _config.Debug(_log, $"VentureBell: sending {snapshot.Value.Retainers.Count} retainers for {snapshot.Value.Character}.");
        _ = SendAsync(snapshot.Value, fingerprint);
    }

    /// <summary>
    /// Asks the server whether it is reachable and the token accepted, without
    /// sending anything. What the settings window's indicator is built on.
    /// </summary>
    internal void CheckConnection()
    {
        if (!_config.IsConfigured)
        {
            Link   = LinkState.Unknown;
            Status = "Set a server address and a token first.";
            return;
        }

        Link = LinkState.Checking;
        _ = CheckAsync();
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
                // Only a delivered payload counts as sent, so a failure is
                // retried by the next poll rather than forgotten.
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
            // Plugin unloading mid-request. Nothing to report to a window that
            // is already gone.
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _addons.UnregisterListener(AddonEvent.PreFinalize, RetainerListAddon, OnRetainerListClosing);

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
