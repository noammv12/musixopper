using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Palon.Agent;

namespace Palon.Notes;

/// <summary>Ask-Palon outcome: Action is "open" (CommandId set), "open_url"
/// (Url set), or "answer" (Text set).</summary>
sealed record AssistResult(string Action, string? CommandId, string? Text, string? Url = null);

/// <summary>One function call the model asked for (OpenAI tools format).
/// ExtraContent is the provider's opaque per-call payload — on Gemini 3.x
/// it carries <c>google.thought_signature</c>, which must be echoed back
/// verbatim on the next request or the call is rejected with a 400.</summary>
sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson, JsonElement? ExtraContent = null);

/// <summary>One model turn: plain content, tool calls, or both.</summary>
sealed record ChatTurn(string? Content, IReadOnlyList<ToolCallRequest> ToolCalls);

/// <summary>
/// Palon's AI brain: an OpenAI-compatible chat client behind a provider
/// chain — Gemini first when its key exists (free tier, 1,500 calls/day),
/// DeepSeek as the second. Call summaries, dictation polish, follow-up
/// drafts and Ask-Palon intent all route through here. Every failure
/// returns null and records LastError — AI output is never worth losing
/// the underlying text over.
/// </summary>
static class AiChat
{
    const int MaxTranscriptChars = 100_000;
    const int MaxDictationChars = 8_000;

    static readonly string SummaryPrompt = PalonPersona.Ghostwriting(
        "You write the quick note a salesperson jots down for themselves right after a " +
        "sales call, from its transcript. Reply in the language the transcript is mostly " +
        "in (Hebrew transcript → Hebrew note). One short flowing paragraph, 1–3 sentences, " +
        "telegraphic and informal — first person, run-on clauses and dashes are fine, no " +
        "bullets, no headings, no labels. Open with the prospect's experience/relevance " +
        "status, then what happened on the call, and end with the agreed next step and its " +
        "timeframe. Only facts from the transcript — never invent a WhatsApp send or a " +
        "callback that wasn't agreed. Examples of the voice to imitate (style only — never " +
        "copy their facts):\n" +
        "\"אין ניסיון - התחיל בלא רלוונטי רוצה לסחור מהבנק, הוסבר על קולמקס ישראל והעלתה " +
        "מישהו לקו לראות האם עידף לה על הבנק והבינו ביחד שכן, הוסבר על הפרטים וביקשה לדבר " +
        "בשבוע הבא כי היא בדיוק מסיימת תהליך גירושים מבעלה.... נשלח ווצאפ ואחזור אליה שבוע הבא.\"\n" +
        "\"סחר באינטראקטיב ישראל בעבר - יותר רלוונטי לקולמקס פרו רוצה לפתוח חשבון ב2,000$ " +
        "הוסבר על הפרטים ואחזור אליו בימיםה קרובים, נשלח ווצאפ\"\n" +
        "Then, only if the call agreed a specific time to call back (either side promising, " +
        "e.g. \"אחזור אליך מחר ב-11\", \"call me back in an hour\"), add one final line exactly " +
        "like: CALLBACK: {\"when_iso\":\"yyyy-MM-ddTHH:mm\",\"phrase\":\"<the words used>\"," +
        "\"reason\":\"<what to do, few words, transcript language>\"} — when_iso in local time, " +
        "resolved against the call end time given. Vague timing (\"בימים הקרובים\") gets no line.");

    const string PolishPrompt =
        "You clean up dictated text. Fix punctuation and casing, remove filler words, false " +
        "starts and immediate self-corrections (keep the corrected version), and fix obvious " +
        "speech-to-text mistakes. Keep the original language, wording and meaning — do not " +
        "add, drop, summarize or translate anything. Reply with the cleaned text only.";

    const string ProfessionalPrompt = PolishPrompt +
        " Then lightly smooth the phrasing so it reads as clear, professional business " +
        "writing — still without adding or removing information.";

    static readonly string RecapPrompt = PalonPersona.Speaking(
        "You write the user's short spoken end-of-day recap from their raw activity log. " +
        "Reply in the language most of the log is in (Hebrew → Hebrew). 4 to 6 short lines: calls and talk time, the two or three things that mattered, " +
        "the next steps that were promised, then pending reminders if any. Plain text — no emoji, " +
        "no headings, no bullets.");

    static readonly string FollowUpPrompt = PalonPersona.Ghostwriting(
        "You draft the short follow-up message a salesperson sends right after a call, " +
        "WhatsApp style. Write in the language of the notes (Hebrew notes → Hebrew " +
        "message). Warm and brief — 2 to 4 sentences: reference what was discussed, " +
        "then end with the agreed next step. No subject line, no signature, no " +
        "placeholders like [name]. Reply with the message text only.");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Why the last ChatAsync returned null (per provider), for UI surfacing.</summary>
    public static string? LastError { get; private set; }

    /// <summary>True when any AI provider is configured.</summary>
    public static bool HasKey => Settings.GeminiKey is not null || Settings.DeepSeekKey is not null;

    // Per-request time budgets. The HttpClient's 60 s stays as the outer
    // bound, but a user standing there waiting must never pay it: a hanging
    // provider (unlike a fast 4xx) otherwise eats a minute per agent round.
    const int InteractiveTimeoutMs = 15_000; // Ask-Palon: someone is waiting for a spoken answer
    const int ForegroundTimeoutMs = 30_000;  // polish / follow-up / recap: on screen, less urgent
    const int BackgroundTimeoutMs = 60_000;  // call summaries: nobody is watching

    /// <summary>The summary, or null plus the per-call failure reason —
    /// returned inline so concurrent AI calls can't garble the reason.</summary>
    /// <param name="withFacts">Also ask for the client-memory FACTS trailer
    /// (same call — memory never costs an extra request).</param>
    /// <param name="knownFacts">The client's known facts with integer ids, for "replaces".</param>
    public static Task<(string? Summary, string? Error)> SummarizeAsync(
        string transcript, CancellationToken ct, DateTime? callEndedLocal = null,
        bool withFacts = false, string? knownFacts = null)
    {
        if (transcript.Length > MaxTranscriptChars) transcript = transcript[..MaxTranscriptChars];
        var header = callEndedLocal is { } ended
            ? $"Call ended: {ended.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (local time)\n"
            : "";
        if (withFacts && !string.IsNullOrEmpty(knownFacts)) header += knownFacts + "\n\n";
        return ChatCoreAsync(withFacts ? SummaryPrompt + Palon.Memory.FactBook.SummaryInstruction : SummaryPrompt,
            header + "Transcript:\n" + transcript, 0.3, withFacts ? 750 : 400, ct,
            requestTimeoutMs: BackgroundTimeoutMs);
    }

    /// <summary>A background memory-inference call (pure JSON reply), or null.</summary>
    public static Task<string?> MemoryJsonAsync(string systemPrompt, string userContent, CancellationToken ct) =>
        ChatAsync(systemPrompt, userContent, 0.0, 500, ct, rejectTruncated: true, requestTimeoutMs: BackgroundTimeoutMs);

    /// <summary>One-shot element pick for Salesforce step recovery: a short,
    /// deterministic reply ({"ref":"eN"} or {"ref":null}); null on failure.</summary>
    public static Task<string?> LocateElementAsync(string systemPrompt, string userContent, CancellationToken ct) =>
        ChatAsync(systemPrompt, userContent, 0.0, 60, ct, rejectTruncated: true, requestTimeoutMs: ForegroundTimeoutMs);

    /// <summary>Cleaned-up dictation, or null when unavailable (caller keeps the raw text).</summary>
    public static Task<string?> PolishAsync(string text, bool professional, CancellationToken ct)
    {
        if (text.Length > MaxDictationChars) return Task.FromResult<string?>(null); // too long to round-trip — keep raw
        // rejectTruncated: a polish cut off at the token cap must never
        // replace the full raw transcript.
        return ChatAsync(professional ? ProfessionalPrompt : PolishPrompt, text, 0.2, 4096, ct, rejectTruncated: true);
    }

    /// <summary>The end-of-day recap, or null when unavailable.</summary>
    public static Task<string?> RecapAsync(string activityData, CancellationToken ct) =>
        ChatAsync(RecapPrompt, activityData, 0.4, 500, ct);

    /// <summary>A paste-ready follow-up message, or null when unavailable.</summary>
    public static Task<string?> FollowUpAsync(string noteText, CancellationToken ct)
    {
        if (noteText.Length > MaxTranscriptChars) noteText = noteText[..MaxTranscriptChars];
        return ChatAsync(FollowUpPrompt, "Call notes:\n" + noteText, 0.5, 300, ct);
    }

    /// <summary>
    /// Ask-Palon intent: run a saved command, open a well-known URL, or
    /// answer. Null only when every provider failed; malformed model output
    /// degrades to treating plain-prose replies as the answer.
    /// </summary>
    public static async Task<AssistResult?> AssistAsync(
        string question, IReadOnlyList<PalonCommand> commands, CancellationToken ct)
    {
        var prompt = new StringBuilder(PalonPersona.Speaking(
            "Be decisive. The input is an " +
            "imperfect speech-recognition transcript of a Hebrew (occasionally English) speaker: " +
            "if it reads as any other language, or as nonsense, it is almost certainly Hebrew " +
            "misheard — reinterpret it phonetically as Hebrew before deciding (e.g. 'La Rabia de " +
            "Argentina' is 'הבירה של ארגנטינה'). Decide ONE action and reply with PURE JSON only, " +
            "no markdown fences:\n" +
            "1. {\"action\":\"open\",\"id\":\"<command id>\"} — the request matches one of the user's " +
            "saved commands (match generously across languages and phrasings: Hebrew " +
            "'תפתחי סיילספורס' matches a command labeled 'Salesforce').\n" +
            "2. {\"action\":\"open_url\",\"url\":\"https://…\"} — the request is to open a well-known " +
            "website that is NOT a saved command (YouTube → https://www.youtube.com, Gmail, " +
            "WhatsApp Web…), or an EXPLICIT search request ('חפש X' / 'search for X' → " +
            "https://www.google.com/search?q=X, URL-encoded). A question is never a search — " +
            "questions get action \"answer\".\n" +
            "3. {\"action\":\"answer\",\"text\":\"...\"} — anything else: answer from your own " +
            "knowledge, in Hebrew (male grammatical forms for yourself) — English only when the " +
            "user clearly spoke English — at most 2 short sentences unless they clearly asked for " +
            "more, plain text, no emoji.\n" +
            "HARD RULES: never ask a clarifying question, never reply with a generic 'how can I " +
            "help'. If asked to open something you can't resolve to a command or a URL, the answer " +
            "is one short sentence telling the user to add it under Commands. Only if you truly " +
            "cannot recover the meaning, say you didn't catch it. Prefer a saved command over " +
            "open_url when both fit."));
        if (commands.Count > 0)
        {
            prompt.Append("\nSaved commands:");
            foreach (var command in commands)
                prompt.Append($"\n- id={command.Id} label=\"{command.Label}\"");
        }
        else
        {
            prompt.Append("\nThe user has no saved commands.");
        }

        // rejectTruncated: half a JSON object must not reach the fallback
        // below, where it would be displayed — and spoken — verbatim.
        var raw = await ChatAsync(prompt.ToString(), question, 0.2, 500, ct, rejectTruncated: true,
            requestTimeoutMs: InteractiveTimeoutMs);
        if (raw is null) return null;

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
                var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
                if (action == "open"
                    && doc.RootElement.TryGetProperty("id", out var id)
                    && id.GetString() is { Length: > 0 } commandId)
                    return new AssistResult("open", commandId, null);
                if (action == "open_url"
                    && doc.RootElement.TryGetProperty("url", out var u)
                    && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    return new AssistResult("open_url", null, null, uri.AbsoluteUri);
                if (action == "answer"
                    && doc.RootElement.TryGetProperty("text", out var t)
                    && t.GetString() is { Length: > 0 } text)
                    return new AssistResult("answer", null, text);
                return null; // valid JSON but no usable action/content
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        // The model ignored the JSON contract. Plain prose is still a usable
        // answer; a JSON-looking fragment is not.
        return raw.StartsWith('{') ? null : new AssistResult("answer", null, raw);
    }

    // ---- provider chain ---------------------------------------------------

    sealed record Provider(string Name, string Url, string Model, string Key, bool IsGemini);

    const string GeminiUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    const string DeepSeekUrl = "https://api.deepseek.com/chat/completions";

    static Provider MakeProvider(AiRoute route, string key) => route.Provider == AiModels.Gemini
        ? new Provider("Gemini", GeminiUrl, route.Model, key, IsGemini: true)
        : new Provider("DeepSeek", DeepSeekUrl, route.Model, key, IsGemini: false);

    /// <summary>
    /// A tiny request against one provider with the given key, for the
    /// settings Test button: latency on success, the error otherwise.
    /// </summary>
    public static async Task<(bool Ok, long Ms, string? Error)> TestKeyAsync(string provider, string key, CancellationToken ct)
    {
        var model = AiModels.Resolve(Settings.AiModelChoice, provider == AiModels.Gemini, provider == AiModels.DeepSeek,
            Settings.GeminiModel, Settings.DeepSeekModel)[0];
        var p = MakeProvider(model, key.Trim());
        var messages = new object[] { new { role = "user", content = "ping. answer: ok" } };
        var started = Environment.TickCount64;
        var (turn, error) = await RequestChatAsync(p, BuildBody(p, messages, 0, 8, tools: null), rejectTruncated: false, ct,
            retryDelayMs: 300, requestTimeoutMs: InteractiveTimeoutMs);
        var ms = Environment.TickCount64 - started;
        return turn is not null ? (true, ms, null) : (false, ms, error.Length > 0 ? error : "no reply");
    }

    static IEnumerable<Provider> Providers()
    {
        // Auto: Gemini first (its free tier absorbs the daily volume),
        // DeepSeek the paid fallback. A picked model makes its provider
        // primary; the other stays the fallback. Either alone also works.
        var geminiKey = Settings.GeminiKey;
        var deepSeekKey = Settings.DeepSeekKey;
        var all = AiModels.Resolve(Settings.AiModelChoice, geminiKey is not null, deepSeekKey is not null,
                Settings.GeminiModel, Settings.DeepSeekModel)
            .Select(r => MakeProvider(r, r.Provider == AiModels.Gemini ? geminiKey! : deepSeekKey!))
            .ToList();
        // Benched providers go last, not away: they still get a turn when
        // they're all we have, and any success un-benches them. Materialized
        // eagerly — a lazy Concat would re-check cooldowns mid-walk and hand
        // a provider benched by this very walk a second turn at the end.
        var healthy = all.Where(p => !IsCooling(p.Name)).ToList();
        healthy.AddRange(all.Except(healthy));
        return healthy;
    }

    // ---- provider cooldown ------------------------------------------------
    // A provider whose requests hang or die at the network layer gets benched
    // briefly. Without this, the agent loop re-walks the chain from the top
    // every round — one dead-hanging provider taxes every round (and every
    // next question) with its full timeout.

    static readonly object CooldownGate = new();
    static readonly Dictionary<string, DateTime> CoolingUntil = new();
    static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(90);

    static bool IsCooling(string name)
    {
        lock (CooldownGate)
            return CoolingUntil.TryGetValue(name, out var until) && until > DateTime.UtcNow;
    }

    static void MarkCooling(Provider provider)
    {
        lock (CooldownGate) CoolingUntil[provider.Name] = DateTime.UtcNow + Cooldown;
        Log.Write($"AI: {provider.Name} benched for {Cooldown.TotalSeconds:0}s after a slow failure");
    }

    static void ClearCooling(Provider provider)
    {
        lock (CooldownGate) CoolingUntil.Remove(provider.Name);
    }

    static async Task<string?> ChatAsync(string systemPrompt, string userContent, double temperature, int maxTokens, CancellationToken ct, bool rejectTruncated = false, int requestTimeoutMs = ForegroundTimeoutMs) =>
        (await ChatCoreAsync(systemPrompt, userContent, temperature, maxTokens, ct, rejectTruncated, requestTimeoutMs)).Text;

    static async Task<(string? Text, string? Error)> ChatCoreAsync(string systemPrompt, string userContent, double temperature, int maxTokens, CancellationToken ct, bool rejectTruncated = false, int requestTimeoutMs = ForegroundTimeoutMs)
    {
        var errors = new List<string>(2);
        foreach (var provider in Providers())
        {
            var (text, error) = await ChatOnceAsync(provider, systemPrompt, userContent, temperature, maxTokens, ct, rejectTruncated, requestTimeoutMs);
            if (text is not null)
            {
                LastError = null;
                return (text, null);
            }
            errors.Add($"{provider.Name}: {error}");
            if (ct.IsCancellationRequested) break;
        }
        var reason = errors.Count > 0 ? string.Join(" → ", errors) : "no AI key";
        LastError = reason;
        if (errors.Count > 0) Log.Write($"AI chain failed: {reason}");
        return (null, reason);
    }

    static async Task<(string? Text, string Error)> ChatOnceAsync(
        Provider provider, string systemPrompt, string userContent,
        double temperature, int maxTokens, CancellationToken ct, bool rejectTruncated, int requestTimeoutMs)
    {
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userContent },
        };
        var (turn, error) = await RequestChatAsync(
            provider, BuildBody(provider, messages, temperature, maxTokens, tools: null), rejectTruncated, ct,
            requestTimeoutMs: requestTimeoutMs);
        return string.IsNullOrWhiteSpace(turn?.Content) ? (null, error.Length > 0 ? error : "empty reply") : (turn!.Content!.Trim(), "");
    }

    // ---- tool calling -----------------------------------------------------

    /// <summary>
    /// One model turn of the agent loop: full message list (system, history,
    /// tool results) + the tools spec, through the same provider chain.
    /// Null when every provider failed (LastError says why) — the caller
    /// falls back to the legacy single-shot intent.
    /// </summary>
    public static Task<ChatTurn?> ToolChatAsync(
        IReadOnlyList<object> messages, object[] tools, double temperature, int maxTokens, CancellationToken ct) =>
        new ToolConversation().ChatAsync(messages, tools, temperature, maxTokens, ct);

    /// <summary>
    /// One agent run's view of the provider chain. The first provider that
    /// answers is pinned for the rest of the run: tool-call ids and Gemini
    /// thought signatures only make sense to the provider that minted them.
    /// If the pinned provider dies mid-run, the next one takes over with
    /// every provider-specific extra stripped (a clean hand-off, never a
    /// splice of foreign state) and is pinned from then on.
    /// </summary>
    public sealed class ToolConversation
    {
        Provider? _pinned;

        /// <summary>The provider this run is pinned to, once one has answered.</summary>
        public string? PinnedProvider => _pinned?.Name;

        /// <summary>toolChoiceNone: tools stay declared (the history refers
        /// to them) but none may be called — the forced final-answer turn.</summary>
        public async Task<ChatTurn?> ChatAsync(
            IReadOnlyList<object> messages, object[] tools, double temperature, int maxTokens, CancellationToken ct,
            bool toolChoiceNone = false)
        {
            var chain = Providers().ToList();
            if (_pinned is { } pinned)
            {
                chain.RemoveAll(p => p.Name == pinned.Name);
                chain.Insert(0, pinned);
            }
            var errors = new List<string>(2);
            foreach (var provider in chain)
            {
                var foreign = _pinned is not null && provider.Name != _pinned.Name;
                var body = BuildBody(provider, foreign ? StripProviderExtras(messages) : messages,
                    temperature, maxTokens, tools);
                if (toolChoiceNone && body.ContainsKey("tools")) body["tool_choice"] = "none";
                // rejectTruncated: half a tool call is unusable, half an answer
                // would be displayed — and spoken — verbatim. The short retry
                // delay keeps a transient failure from stalling a user who is
                // standing there waiting for the spoken answer.
                var (turn, error) = await RequestChatAsync(
                    provider, body, rejectTruncated: true, ct,
                    retryDelayMs: 500, requestTimeoutMs: InteractiveTimeoutMs);
                if (turn is not null)
                {
                    if (foreign) Log.Write($"AI: agent run moved {_pinned!.Name} → {provider.Name} (provider extras stripped)");
                    _pinned = provider;
                    LastError = null;
                    return turn;
                }
                errors.Add($"{provider.Name}: {error}");
                if (ct.IsCancellationRequested) break;
            }
            var reason = errors.Count > 0 ? string.Join(" → ", errors) : "no AI key";
            LastError = reason;
            if (errors.Count > 0) Log.Write($"AI tool chain failed: {reason}");
            return null;
        }
    }

    /// <summary>The message list with every per-call provider payload
    /// (extra_content) removed — what a different provider gets when it
    /// takes over a run mid-way. Messages are not mutated.</summary>
    internal static IReadOnlyList<object> StripProviderExtras(IReadOnlyList<object> messages) => messages
        .Select(message =>
        {
            if (message is not Dictionary<string, object?> dict
                || !dict.TryGetValue("tool_calls", out var raw)
                || raw is not object[] calls)
                return message;
            return new Dictionary<string, object?>(dict)
            {
                ["tool_calls"] = calls
                    .Select(call => call is Dictionary<string, object?> c && c.ContainsKey("extra_content")
                        ? c.Where(kv => kv.Key != "extra_content").ToDictionary(kv => kv.Key, kv => kv.Value)
                        : call)
                    .ToArray(),
            };
        })
        .ToList();

    static Dictionary<string, object> BuildBody(
        Provider provider, object messages, double temperature, int maxTokens, object[]? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = provider.Model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["stream"] = false,
            // Gemini Flash thinks by default and thinking tokens count
            // against max_tokens — the caps sized for DeepSeek's non-thinking
            // chat mode would be eaten by reasoning and return empty replies. The 4096 floor is
            // for long inputs (call-summary transcripts): even at low effort
            // the thinking scales with the input, and 2048 was observed fully
            // consumed with nothing left for the answer. A high cap costs
            // nothing unless tokens are actually generated.
            ["max_tokens"] = provider.IsGemini ? Math.Max(maxTokens * 4, 4096) : maxTokens,
        };
        if (provider.IsGemini) body["reasoning_effort"] = "low";
        if (tools is { Length: > 0 })
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    static async Task<(ChatTurn? Turn, string Error)> RequestChatAsync(
        Provider provider, Dictionary<string, object> body, bool rejectTruncated, CancellationToken ct,
        int retryDelayMs = 2000, int requestTimeoutMs = ForegroundTimeoutMs)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Per-request budget under the HttpClient's 60 s: sized to who is
            // waiting (15 s when someone stands there listening for the answer).
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(requestTimeoutMs);
            var started = Environment.TickCount64;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, provider.Url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, budget.Token);
                var status = (int)response.StatusCode;
                if ((status == 429 || status >= 500) && attempt == 0)
                {
                    await Task.Delay(retryDelayMs, ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized) return (null, "key rejected (401)");
                if (status == 402) return (null, "insufficient balance (402)");
                if (status == 429) return (null, "rate limited (429)");
                if (!response.IsSuccessStatusCode) return (null, $"HTTP {status}");

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (rejectTruncated
                    && choice.TryGetProperty("finish_reason", out var finish)
                    && finish.GetString() == "length")
                    return (null, "hit the token cap");

                var message = choice.GetProperty("message");
                var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                var calls = new List<ToolCallRequest>();
                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var function)
                            || !function.TryGetProperty("name", out var nameEl)
                            || nameEl.GetString() is not { Length: > 0 } name)
                            continue;
                        var argumentsJson =
                            function.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String
                                ? argsEl.GetString() ?? "{}"
                                : "{}";
                        var id = call.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } rawId
                            ? rawId
                            : Guid.NewGuid().ToString("n");
                        // Gemini 3.x: extra_content.google.thought_signature
                        // must round-trip verbatim — keep the whole object.
                        JsonElement? extra = call.TryGetProperty("extra_content", out var extraEl)
                            && extraEl.ValueKind == JsonValueKind.Object
                                ? extraEl.Clone()
                                : null;
                        calls.Add(new ToolCallRequest(id, name, argumentsJson, extra));
                    }
                }
                if (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
                {
                    // Name the shape of the emptiness: finish=length with
                    // tokens spent means thinking ate the cap; finish=stop
                    // with 0 tokens means the model really said nothing.
                    var finishReason = choice.TryGetProperty("finish_reason", out var fin) ? fin.GetString() : null;
                    var spent = doc.RootElement.TryGetProperty("usage", out var usage)
                        && usage.TryGetProperty("completion_tokens", out var spentEl)
                        ? spentEl.GetRawText() : "?";
                    return (null, $"empty reply (finish={finishReason ?? "?"}, completion_tokens={spent})");
                }
                ClearCooling(provider);
                var seconds = (Environment.TickCount64 - started) / 1000.0;
                if (seconds > 3) Log.Write($"AI: {provider.Name} answered in {seconds:0.0}s");
                return (new ChatTurn(content, calls), "");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The budget fired, not the caller. No second attempt — a
                // retry would double the stall for whoever is waiting.
                MarkCooling(provider);
                return (null, $"timed out ({requestTimeoutMs / 1000}s)");
            }
            catch (OperationCanceledException)
            {
                return (null, "cancelled");
            }
            catch (Exception) when (attempt == 0)
            {
                try
                {
                    await Task.Delay(retryDelayMs, ct);
                }
                catch
                {
                    return (null, "cancelled");
                }
            }
            catch (Exception ex)
            {
                // Two straight network-layer deaths: bench the provider so
                // the next round (and question) doesn't pay for it again.
                if (ex is HttpRequestException or IOException) MarkCooling(provider);
                return (null, ex.Message);
            }
        }
        // Not reachable — attempt 1 always returns above; the compiler just
        // can't prove it.
        return (null, "network error");
    }
}
