using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace Palon.Salesforce;

enum EdgeState { NotInstalled, BlockedByPolicy, NotRunning, Running, NeedsRestart }

/// <summary>
/// Palon's own Edge profile, driven over CDP.
/// Facts (verified 2026-09): since Chromium 136, Chrome — and Edge, which
/// inherits it — ignores --remote-debugging-port on the default user-data-dir,
/// so Palon always uses a dedicated dir (%LOCALAPPDATA%\Palon\edge-profile);
/// the user signs in to Salesforce there once and the session persists.
/// The Edge policy RemoteDebuggingAllowed=0 (HKLM/HKCU\SOFTWARE\Policies\
/// Microsoft\Edge) disables the switch entirely — detected and reported.
/// The port is a random free loopback port; DevToolsActivePort in the
/// profile dir is the source of truth for reconnecting later.
/// </summary>
static class EdgeBrowser
{
    public static string ProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "edge-profile");

    static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    public static string? FindExe()
    {
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
                if (key?.GetValue(null) is string p && File.Exists(p)) return p;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>True when an Edge policy turns remote debugging off.</summary>
    public static bool PolicyBlocks()
    {
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge");
                if (key?.GetValue("RemoteDebuggingAllowed") is int v && v == 0) return true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return false;
    }

    /// <summary>The live debugging port of Palon's Edge, or null.</summary>
    public static async Task<int?> LivePortAsync(CancellationToken ct)
    {
        var file = Path.Combine(ProfileDir, "DevToolsActivePort");
        if (!File.Exists(file)) return null;
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(file, ct);
        }
        catch (IOException)
        {
            return null;
        }
        if (lines.Length == 0 || !int.TryParse(lines[0], out var port)) return null;
        return await VersionAsync(port, ct) is not null ? port : null; // the file outlives the browser
    }

    static async Task<JsonElement?> VersionAsync(int port, CancellationToken ct)
    {
        try
        {
            using var r = await Http.GetAsync($"http://127.0.0.1:{port}/json/version", ct);
            if (r.StatusCode != HttpStatusCode.OK) return null;
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public static async Task<EdgeState> StateAsync(CancellationToken ct)
    {
        if (FindExe() is null) return EdgeState.NotInstalled;
        if (PolicyBlocks()) return EdgeState.BlockedByPolicy;
        if (await LivePortAsync(ct) is not null) return EdgeState.Running;
        return ProfileInUse() ? EdgeState.NeedsRestart : EdgeState.NotRunning;
    }

    /// <summary>Chromium holds "lockfile" open while a browser uses the profile.</summary>
    static bool ProfileInUse()
    {
        var lockFile = Path.Combine(ProfileDir, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var _ = File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// The debugging port of Palon's Edge, launching it if needed (optionally
    /// on <paramref name="startUrl"/>). Throws with a Hebrew, user-facing
    /// message when that is impossible.
    /// </summary>
    public static async Task<int> EnsureAsync(string? startUrl, CancellationToken ct)
    {
        if (await LivePortAsync(ct) is { } live) return live;
        var exe = FindExe() ?? throw new CdpException("Microsoft Edge לא נמצא במחשב");
        if (PolicyBlocks()) throw new CdpException("מדיניות הארגון חוסמת שליטה ב-Edge (RemoteDebuggingAllowed). צריך אישור מה-IT.");
        if (ProfileInUse())
            throw new CdpException("חלון ה-Edge של Palon פתוח בלי חיבור. סגור אותו ונסה שוב.");

        Directory.CreateDirectory(ProfileDir);
        var port = FreePort();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[]
                 {
                     $"--user-data-dir={ProfileDir}",
                     $"--remote-debugging-port={port}",
                     "--remote-debugging-address=127.0.0.1",
                     "--no-first-run",
                     "--no-default-browser-check",
                     "--disable-features=msEdgeStartupBoost",
                     startUrl ?? "about:blank",
                 })
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        Log.Write($"Salesforce: launched Palon's Edge on 127.0.0.1:{port}");

        // First start of a fresh profile can take a while; a browser that
        // ignored the switch never answers — don't hang on it.
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            if (await VersionAsync(port, ct) is not null) return port;
            await Task.Delay(250, ct);
        }
        throw new CdpException("Edge נפתח אבל לא מאפשר חיבור. סגור את כל חלונות ה-Edge של Palon ונסה שוב.");
    }

    public sealed record Target(string Id, string Type, string Url, string WsUrl);

    public static async Task<List<Target>> TargetsAsync(int port, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(t => t.TryGetProperty("webSocketDebuggerUrl", out _))
            .Select(t => new Target(t.GetProperty("id").GetString()!, t.GetProperty("type").GetString()!,
                t.GetProperty("url").GetString() ?? "", t.GetProperty("webSocketDebuggerUrl").GetString()!))
            .ToList();
    }

    public static async Task<Target> NewTabAsync(int port, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(url)}");
        using var r = await Http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var t = doc.RootElement;
        return new Target(t.GetProperty("id").GetString()!, "page", url, t.GetProperty("webSocketDebuggerUrl").GetString()!);
    }

    /// <summary>
    /// A page on the Salesforce org: an existing Lightning tab if there is one
    /// (it carries the session), else a new tab on the saved org URL.
    /// </summary>
    public static async Task<CdpPage> SalesforcePageAsync(string? orgOrigin, CancellationToken ct)
    {
        var port = await EnsureAsync(orgOrigin is null ? null : orgOrigin + "/lightning/page/home", ct);
        var targets = await TargetsAsync(port, ct);
        var sf = targets.FirstOrDefault(t => t.Type == "page" && SfOrg.IsLightning(t.Url))
                 ?? targets.FirstOrDefault(t => t.Type == "page" && t.Url.Contains("salesforce.com", StringComparison.OrdinalIgnoreCase));
        if (sf is null)
        {
            if (orgOrigin is null) throw new CdpException("לא נמצאה לשונית Salesforce. פתח את Salesforce בחלון ה-Edge של Palon והתחבר.");
            sf = await NewTabAsync(port, orgOrigin + "/lightning/page/home", ct);
        }
        return await CdpPage.OpenAsync(sf.WsUrl, sf.Id, ct);
    }
}

/// <summary>The org Palon works in, learned from the first Lightning tab it sees.</summary>
static class SfOrg
{
    static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "salesforce", "org.txt");

    public static bool IsLightning(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
        && (u.Host.EndsWith(".lightning.force.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".my.salesforce.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>"https://acme.lightning.force.com", or null.</summary>
    public static string? Origin(string url) =>
        IsLightning(url) && Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? $"https://{u.Host.Replace(".my.salesforce.com", ".lightning.force.com", StringComparison.OrdinalIgnoreCase)}"
            : null;

    public static string? Saved
    {
        get
        {
            try
            {
                return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() is { Length: > 0 } s ? s : null : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, value ?? "");
            }
            catch (IOException)
            {
            }
        }
    }
}
