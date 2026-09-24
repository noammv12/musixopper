using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Palon.Salesforce;

/// <summary>User settings for the nightly rehearsal (salesforce\rehearsal.json).</summary>
sealed class RehearsalSettings
{
    /// <summary>Null = on by default once a skill is taught; false = user turned it off.</summary>
    public bool? Enabled { get; set; }
    public string Time { get; set; } = "21:30";
    /// <summary>Israeli work week by default: Sunday–Thursday.</summary>
    public List<DayOfWeek> Days { get; set; } = new() { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday };
    /// <summary>The test record the user taught on — rehearsals only ever run there.</summary>
    public string? TestRecordUrl { get; set; }
    public DateTime? LastRunLocal { get; set; }
    public RehearsalStatus? LastStatus { get; set; }

    public TimeSpan TimeOfDay =>
        TimeSpan.TryParseExact(Time, @"hh\:mm", CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : new TimeSpan(21, 30, 0);

    static string PathFor => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "salesforce", "rehearsal.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RehearsalSettings Load()
    {
        try
        {
            return File.Exists(PathFor) ? JsonSerializer.Deserialize<RehearsalSettings>(File.ReadAllText(PathFor)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor)!);
            File.WriteAllText(PathFor, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Salesforce: rehearsal settings save failed: {ex.Message}");
        }
    }
}

sealed record RehearsalSkillStatus(string Skill, bool Ok, bool Healed, string? BrokenStep);

sealed record RehearsalStatus(DateTime AtLocal, IReadOnlyList<RehearsalSkillStatus> Skills, string? Error = null)
{
    public bool Ok => Error is null && Skills.All(s => s.Ok);

    /// <summary>One line for the Salesforce section and the quiet notification.</summary>
    public string Summary()
    {
        if (Error is not null) return $"חזרה לילית {AtLocal:dd/MM HH:mm}: {Error}";
        var broken = Skills.Where(s => !s.Ok).ToList();
        if (broken.Count == 0)
            return $"חזרה לילית {AtLocal:dd/MM HH:mm}: כל {Skills.Count} הכישורים עובדים ✓" + (Skills.Any(s => s.Healed) ? " (חלק תוקנו)" : "");
        return $"חזרה לילית {AtLocal:dd/MM HH:mm}: " + string.Join("; ", broken.Select(b => $"{b.Skill} נשבר ב\"{b.BrokenStep}\""));
    }
}

/// <summary>Pure scheduling rules for the nightly rehearsal.</summary>
static class RehearsalSchedule
{
    /// <summary>
    /// Is it on? Off until at least one skill has been taught and a test
    /// record is known; after that on unless the user turned it off.
    /// </summary>
    public static bool Active(RehearsalSettings s, bool anySkillTaught) =>
        anySkillTaught && s.TestRecordUrl is not null && s.Enabled != false && s.Days.Count > 0;

    /// <summary>The next slot at or after <paramref name="nowLocal"/> (null when inactive).</summary>
    public static DateTime? Next(RehearsalSettings s, bool anySkillTaught, DateTime nowLocal)
    {
        if (!Active(s, anySkillTaught)) return null;
        for (var d = 0; d <= 7; d++)
        {
            var day = nowLocal.Date.AddDays(d);
            var slot = day + s.TimeOfDay;
            if (s.Days.Contains(day.DayOfWeek) && slot >= nowLocal) return slot;
        }
        return null;
    }

    /// <summary>
    /// Due when today's slot has passed and nothing ran since it. A slot missed
    /// by more than <paramref name="grace"/> (PC was off) is skipped, not run at
    /// 3 a.m. — the next work day covers it.
    /// </summary>
    public static bool IsDue(RehearsalSettings s, bool anySkillTaught, DateTime nowLocal, TimeSpan? grace = null)
    {
        if (!Active(s, anySkillTaught)) return false;
        if (!s.Days.Contains(nowLocal.DayOfWeek)) return false;
        var slot = nowLocal.Date + s.TimeOfDay;
        if (nowLocal < slot || nowLocal - slot > (grace ?? TimeSpan.FromHours(2))) return false;
        return s.LastRunLocal is not { } last || last < slot;
    }

    /// <summary>The skills worth rehearsing: the ones the user taught.</summary>
    public static IReadOnlyList<SfSkill> Taught(IEnumerable<SfSkill> skills) =>
        skills.Where(k => k.TaughtAt is not null && k.Steps.Count > 0 && k.Skill is "LogCall" or "NewTask").ToList();
}

/// <summary>
/// The nightly dry run: each taught skill against the user's test record,
/// stopping before Save (the runner's dry-run guard presses Escape instead).
/// Heals happen in the dry run and are cached only after the step's
/// post-condition passed — the runner's normal rule.
/// </summary>
static class Rehearsal
{
    public static event Action<RehearsalStatus>? Finished;

    static System.Threading.Timer? _timer;

    sealed class DryGuard : ICommitGuard
    {
        public bool DryRun => true;
        public Task<string?> CheckAsync(string currentUrl, string elementName, CancellationToken ct) =>
            Task.FromResult<string?>("rehearsal never saves");
    }

    static bool AnyTaught() => RehearsalSchedule.Taught(DefaultSkills.Names.Select(SkillStore.Load)).Count > 0;

    /// <summary>Checks every 5 minutes whether a rehearsal is due. Idempotent.</summary>
    public static void StartScheduler()
    {
        _timer ??= new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    static int _running;

    static async Task TickAsync()
    {
        try
        {
            var s = RehearsalSettings.Load();
            if (!RehearsalSchedule.IsDue(s, AnyTaught(), DateTime.Now)) return;
            await RunAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write($"Salesforce rehearsal tick failed: {ex.Message}");
        }
    }

    /// <summary>Remembers the test record the user taught on (called when teaching ends).</summary>
    public static void RememberTestRecord(string? url)
    {
        if (SfPhones.ParseRecordUrl(url) is null) return;
        var s = RehearsalSettings.Load();
        s.TestRecordUrl = url;
        s.Save();
    }

    public static async Task<RehearsalStatus> RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return new RehearsalStatus(DateTime.Now, Array.Empty<RehearsalSkillStatus>(), "כבר רץ");
        var settings = RehearsalSettings.Load();
        settings.LastRunLocal = DateTime.Now;
        settings.Save(); // even a failed attempt counts: never retry-loop all night
        RehearsalStatus status;
        try
        {
            status = await RunCoreAsync(settings, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
        settings = RehearsalSettings.Load();
        settings.LastStatus = status;
        settings.Save();
        Audit.WriteRehearsal(new { ok = status.Ok, summary = status.Summary() });
        Log.Write("Salesforce " + status.Summary());
        Finished?.Invoke(status);
        return status;
    }

    static async Task<RehearsalStatus> RunCoreAsync(RehearsalSettings settings, CancellationToken ct)
    {
        var skills = RehearsalSchedule.Taught(DefaultSkills.Names.Select(SkillStore.Load));
        var testUrl = settings.TestRecordUrl;
        var testId = SfPhones.ParseRecordUrl(testUrl)?.Id;
        if (skills.Count == 0 || testUrl is null || testId is null)
            return new RehearsalStatus(DateTime.Now, Array.Empty<RehearsalSkillStatus>(), "אין כישור שנלמד או רשומת בדיקה");
        var results = new List<RehearsalSkillStatus>();
        try
        {
            await using var page = await EdgeBrowser.SalesforcePageAsync(SfOrg.Saved, ct);
            foreach (var skill in skills)
            {
                ct.ThrowIfCancellationRequested();
                await page.NavigateAsync(testUrl, ct);
                if (!SfPhones.SameId(SfPhones.ParseRecordUrl(await page.UrlAsync(ct))?.Id, testId))
                    return new RehearsalStatus(DateTime.Now, results,
                        SfSafety.IsLoginUrl(await page.UrlAsync(ct)) ? "Salesforce מבקש התחברות" : "רשומת הבדיקה לא נפתחה");
                var vars = new Dictionary<string, string>
                {
                    ["subject"] = "Palon rehearsal",
                    ["comments"] = "Palon rehearsal — not saved",
                    ["date"] = SfVars.FormatDate(DateOnly.FromDateTime(DateTime.Now.AddDays(1)), skill.DateFormat),
                };
                var run = await new SkillRunner(page, SalesforceAgent.Ask, new LoopDetector())
                    .RunAsync(skill, vars, new DryGuard(), testId, ct);
                var broken = run.Ok ? null : run.Steps.Count < skill.Steps.Count ? skill.Steps[run.Steps.Count].Intent : run.Error;
                results.Add(new RehearsalSkillStatus(skill.Skill, run.Ok, run.Healed, broken ?? run.Error));
                if (run.NeedsLogin) return new RehearsalStatus(DateTime.Now, results, "Salesforce מבקש התחברות");
            }
        }
        catch (Exception ex) when (ex is CdpException or IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException)
        {
            return new RehearsalStatus(DateTime.Now, results, "Edge של Palon לא זמין: " + ex.Message);
        }
        return new RehearsalStatus(DateTime.Now, results);
    }
}
