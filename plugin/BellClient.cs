using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VentureBell;

// The wire format, and the only thing this plugin ever sends anywhere. It is
// deliberately the whole retainer list rather than an event: the server replaces
// what it knows on every sync, so a missed message costs nothing.
internal sealed class SyncPayload
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";

    [JsonPropertyName("retainers")] public List<SyncRetainer> Retainers { get; set; } = new();
}

internal sealed class SyncRetainer
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("venture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Venture { get; set; }

    [JsonPropertyName("done_at")] public long DoneAt { get; set; }
}

/// <summary>Posts snapshots to the venturebell server.</summary>
internal sealed class BellClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    // Ten seconds: long enough for a homelab round trip, short enough that a
    // server which has gone away does not tie up a request for a minute.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Sends one snapshot. Returns null when the server accepted it, and a line
    /// worth showing the user when it did not — every failure here is something
    /// they can fix: wrong address, wrong token, server not running.
    /// </summary>
    internal async Task<string?> SendAsync(string baseUrl, string token, Snapshot snapshot, CancellationToken ct)
    {
        if (!TryBuildUrl(baseUrl, out var url))
            return $"'{baseUrl}' is not a valid http(s) address.";

        var payload = new SyncPayload { Character = snapshot.Character };
        foreach (var r in snapshot.Retainers)
        {
            payload.Retainers.Add(new SyncRetainer
            {
                Name = r.Name,
                // The server omits an empty venture; sending "" back would be a
                // field it has to ignore.
                Venture = string.IsNullOrEmpty(r.Venture) ? null : r.Venture,
                DoneAt = r.DoneAt,
            });
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return $"{(int)response.StatusCode} {response.ReasonPhrase}: {Shorten(body)}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "the server did not answer within ten seconds.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool TryBuildUrl(string baseUrl, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/sync", UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        url = parsed;
        return true;
    }

    private static string Shorten(string body)
    {
        body = body.Trim().ReplaceLineEndings(" ");
        return body.Length <= 200 ? body : body[..200] + "...";
    }

    public void Dispose() => _http.Dispose();
}
