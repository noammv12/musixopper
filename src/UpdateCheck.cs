using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Palon;

/// <summary>
/// Quiet update nudge: at most once a day, ask GitHub for the latest
/// release and, when it's newer than this build, surface a small link in
/// the flyout footer. No auto-update, no telemetry — one anonymous GET to
/// the public releases API, and total silence on any failure.
/// </summary>
static class UpdateCheck
{
    const string ReleasesApi = "https://api.github.com/repos/noammv12/musixopper/releases/latest";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Fire-and-forget from startup; onUpdate(version, url) may be
    /// called from a background thread — the UI side marshals.</summary>
    public static void Run(Action<string, string> onUpdate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // A newer version we already know about surfaces instantly,
                // even when the daily fetch is skipped.
                if (Parse(Settings.UpdateLatestSeen) is { } known && IsNewer(known.Version, Current))
                    onUpdate(known.Version, known.Url);

                if (Settings.UpdateLastCheckedUtc is { } last &&
                    DateTime.UtcNow - last < CheckEvery)
                    return;
                Settings.UpdateLastCheckedUtc = DateTime.UtcNow;

                using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Palon", Program.Version));
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return; // no releases yet, rate limited — fine
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                if (tag is null || url is null) return;

                var version = tag.TrimStart('v', 'V');
                Settings.UpdateLatestSeen = $"{version} {url}";
                if (IsNewer(version, Current)) onUpdate(version, url);
            }
            catch (Exception ex)
            {
                Log.Write($"Update check failed: {ex.Message}");
            }
        });
    }

    static Version Current =>
        typeof(UpdateCheck).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    internal static bool IsNewer(string version, Version current) =>
        Version.TryParse(Normalize(version), out var parsed) && parsed > current;

    /// <summary>"8.2" → "8.2.0" so a two-part tag still compares.</summary>
    static string Normalize(string version) =>
        version.Count(c => c == '.') == 1 ? version + ".0" : version;

    internal static (string Version, string Url)? Parse(string? stored)
    {
        if (stored is null) return null;
        var space = stored.IndexOf(' ');
        if (space <= 0 || space == stored.Length - 1) return null;
        return (stored[..space], stored[(space + 1)..]);
    }
}
