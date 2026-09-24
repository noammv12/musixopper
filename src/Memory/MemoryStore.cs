using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palon.Memory;

/// <summary>
/// Palon's two memories on disk, kept strictly apart:
/// <c>memory/profile.dat</c> ("About you" + suggestions + change history)
/// and <c>memory/clients.dat</c> (bitemporal client facts). Both are JSON
/// encrypted with DPAPI (CurrentUser), like the API keys. Loaded once and
/// cached; every mutation writes through atomically and raises Changed.
/// </summary>
static class MemoryStore
{
    static readonly object Gate = new();
    static ProfileData? _profile;
    static List<ClientFact>? _facts;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "memory");
    static string ProfilePath => Path.Combine(Dir, "profile.dat");
    static string ClientsPath => Path.Combine(Dir, "clients.dat");

    /// <summary>Any memory change (screen refresh).</summary>
    public static event Action? Changed;

    /// <summary>A line was saved to "About you" from a conversation — the
    /// UI shows a toast with Undo (change id) and Edit.</summary>
    public static event Action<ProfileItem, string>? Remembered;

    /// <summary>A line was forgotten from a conversation — toast with Undo.</summary>
    public static event Action<ProfileItem, string>? Forgotten;

    // ---- encryption -----------------------------------------------------------------

    static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    static byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Palon.Memory.v1");

    static T? ReadEncrypted<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var bytes = Unprotect(File.ReadAllBytes(path));
        return JsonSerializer.Deserialize<T>(bytes, Json);
    }

    static void WriteEncrypted<T>(string path, T value)
    {
        Directory.CreateDirectory(Dir);
        var bytes = Protect(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    static ProfileData ProfileUnlocked()
    {
        if (_profile is not null) return _profile;
        try
        {
            _profile = ReadEncrypted<ProfileData>(ProfilePath) ?? new ProfileData();
        }
        catch (Exception ex)
        {
            // Never overwrite an unreadable file (other Windows user, copied
            // profile): park it and start clean.
            Log.Write($"Memory: profile unreadable ({ex.Message}) — starting fresh, old file kept as .bad");
            TryPark(ProfilePath);
            _profile = new ProfileData();
        }
        return _profile;
    }

    static List<ClientFact> FactsUnlocked()
    {
        if (_facts is not null) return _facts;
        try
        {
            _facts = ReadEncrypted<List<ClientFact>>(ClientsPath) ?? new List<ClientFact>();
        }
        catch (Exception ex)
        {
            Log.Write($"Memory: client facts unreadable ({ex.Message}) — starting fresh, old file kept as .bad");
            TryPark(ClientsPath);
            _facts = new List<ClientFact>();
        }
        return _facts;
    }

    static void TryPark(string path)
    {
        try { if (File.Exists(path)) File.Move(path, path + ".bad", overwrite: true); } catch { }
    }

    // ---- "About you" --------------------------------------------------------------

    /// <summary>A deep-enough snapshot for reading (UI, prompts).</summary>
    public static ProfileData Profile
    {
        get
        {
            lock (Gate)
            {
                var p = ProfileUnlocked();
                return new ProfileData
                {
                    Items = p.Items.ToList(), Suggestions = p.Suggestions.ToList(), Rejected = p.Rejected.ToList(),
                    Paused = p.Paused, History = p.History.ToList(),
                };
            }
        }
    }

    public static bool Paused => Profile.Paused;

    public static T MutateProfile<T>(Func<ProfileData, T> change)
    {
        T result;
        lock (Gate)
        {
            var p = ProfileUnlocked();
            result = change(p);
            try
            {
                WriteEncrypted(ProfilePath, p);
            }
            catch (Exception ex)
            {
                Log.Write($"Memory: profile save failed: {ex.Message}");
            }
        }
        Changed?.Invoke();
        return result;
    }

    /// <summary>Saves an explicit "remember…" and raises Remembered (toast).</summary>
    public static (ProfileItem Item, bool Added) RememberFromChat(string text, ProfileSection section, string source, string reason, TimeRule? rule)
    {
        var (item, added) = MutateProfile(d => ProfileBook.Remember(d, text, section, source, reason, DateTime.UtcNow, rule));
        if (added)
        {
            var changeId = Profile.History.LastOrDefault(c => c.After?.Id == item.Id)?.Id ?? "";
            Remembered?.Invoke(item, changeId);
        }
        return (item, added);
    }

    public static ProfileItem? ForgetFromChat(string query, string reason)
    {
        var removed = MutateProfile(d => ProfileBook.Find(d, query) is { } hit ? ProfileBook.Delete(d, hit.Id, reason, DateTime.UtcNow) : null);
        if (removed is not null)
        {
            var changeId = Profile.History.LastOrDefault(c => c.Before?.Id == removed.Id && c.Op == "delete")?.Id ?? "";
            Forgotten?.Invoke(removed, changeId);
        }
        return removed;
    }

    public static bool Undo(string changeId) => MutateProfile(d => ProfileBook.Undo(d, changeId, DateTime.UtcNow));

    public static void SetPaused(bool paused) => MutateProfile(d =>
    {
        d.Paused = paused;
        if (paused) d.Suggestions.Clear(); // nothing inferred while paused survives
        return 0;
    });

    public static IReadOnlyList<TimeRule> Rules() => ProfileBook.Rules(Profile).ToList();

    /// <summary>Rules for a callback target (name, or a phone whose name
    /// client memory knows).</summary>
    public static IReadOnlyList<TimeRule> RulesFor(string? name, string? phone)
    {
        var rules = Rules();
        if (rules.Count == 0) return rules;
        if (string.IsNullOrWhiteSpace(name) && phone is not null) name = NameFor(phone);
        return TimeRules.For(rules, name, phone);
    }

    // ---- client facts -------------------------------------------------------------

    public static List<ClientFact> Facts
    {
        get
        {
            lock (Gate) return FactsUnlocked().ToList();
        }
    }

    public static T MutateFacts<T>(Func<List<ClientFact>, T> change)
    {
        T result;
        lock (Gate)
        {
            var f = FactsUnlocked();
            result = change(f);
            try
            {
                WriteEncrypted(ClientsPath, f);
            }
            catch (Exception ex)
            {
                Log.Write($"Memory: client facts save failed: {ex.Message}");
            }
        }
        Changed?.Invoke();
        return result;
    }

    public static string? NameFor(string? phone) =>
        phone is null ? null : FactBook.ForClient(Facts, null, phone).Select(f => f.ClientName).LastOrDefault(n => n is not null);

    /// <summary>Known facts for the summary prompt (by the caller's number).</summary>
    public static (string Block, List<string> IdMap) KnownFactsFor(string? phone)
    {
        if (phone is null) return ("", new List<string>());
        try
        {
            return FactBook.KnownFactsBlock(FactBook.ForClient(Facts, null, phone));
        }
        catch (Exception ex)
        {
            Log.Write($"Memory: known facts lookup failed: {ex.Message}");
            return ("", new List<string>());
        }
    }

    /// <summary>Stores what the summary call extracted (no extra API call).</summary>
    public static void ApplyCallFacts(string? phone, FactsPayload payload, IReadOnlyList<string> idMap, string noteId, DateTime callUtc)
    {
        if (Paused) return;
        var name = payload.ClientName;
        var key = FactBook.KeyFor(phone, name);
        if (key is null || payload.Facts.Count == 0) return;
        try
        {
            var applied = MutateFacts(all => FactBook.Apply(all, key, name, phone, payload, idMap, "call:" + noteId, callUtc, DateTime.UtcNow));
            Log.Write($"Memory: {applied.Added.Count} client fact(s) added, {applied.Duplicates} duplicate(s), {applied.Invalidated.Count} invalidated");
        }
        catch (Exception ex)
        {
            Log.Write($"Memory: applying client facts failed: {ex.Message}");
        }
    }
}

/// <summary>
/// LangMem-style debounced background extraction: every Ask exchange is
/// buffered and (re)schedules one inference call ~90 s after the
/// conversation goes quiet. Its output only ever becomes SUGGESTIONS.
/// </summary>
static class ProfileReflector
{
    static readonly TimeSpan Delay = TimeSpan.FromSeconds(90);
    const int MaxBuffered = 8;
    static readonly object Gate = new();
    static readonly List<(string Question, string Answer)> Buffer = new();
    static CancellationTokenSource? _pending;

    public static void Submit(string question, string answer)
    {
        if (!Palon.Notes.AiChat.HasKey || MemoryStore.Paused) return;
        CancellationToken token;
        lock (Gate)
        {
            Buffer.Add((question, answer));
            if (Buffer.Count > MaxBuffered) Buffer.RemoveAt(0);
            _pending?.Cancel();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }
        _ = RunAfterDelayAsync(token);
    }

    static async Task RunAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Delay, token);
            List<(string, string)> batch;
            lock (Gate)
            {
                batch = Buffer.ToList();
                Buffer.Clear();
            }
            if (batch.Count == 0 || MemoryStore.Paused) return;
            var input = ProfileBook.BuildReflectionInput(MemoryStore.Profile, batch);
            var reply = await Palon.Notes.AiChat.MemoryJsonAsync(ProfileBook.ReflectionPrompt, input, token);
            if (reply is null || MemoryStore.Paused) return;
            var suggestions = ProfileBook.ParseSuggestions(reply, DateTime.UtcNow);
            var added = MemoryStore.MutateProfile(d => ProfileBook.AddSuggestions(d, suggestions));
            if (added > 0) Log.Write($"Memory: {added} profile suggestion(s) pending approval");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write($"Memory: reflection failed: {ex.Message}");
        }
    }
}
