using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Palon.Salesforce;

sealed class CdpException(string message) : Exception(message);

/// <summary>
/// A lean Chrome DevTools Protocol client over one page target's WebSocket:
/// request/response by id, events by method name. No Node, no driver —
/// Palon ships as a single exe and only needs a handful of CDP domains.
/// </summary>
sealed class CdpSession : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _life = new();
    int _nextId;

    public event Action<string, JsonElement>? Event;
    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<CdpSession> ConnectAsync(string webSocketUrl, CancellationToken ct)
    {
        var s = new CdpSession();
        s._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(5000);
        await s._ws.ConnectAsync(new Uri(webSocketUrl), timeout.Token);
        _ = s.ReceiveLoopAsync();
        return s;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken ct = default, int timeoutMs = 15000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        timeout.CancelAfter(timeoutMs);
        await using var reg = timeout.Token.Register(() => tcs.TrySetException(new CdpException($"{method}: timed out or cancelled")));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_life.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _life.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                Dispatch(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        foreach (var p in _pending.Values) p.TrySetException(new CdpException("browser connection closed"));
    }

    void Dispatch(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idEl) && _pending.TryGetValue(idEl.GetInt32(), out var tcs))
        {
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(new CdpException(err.TryGetProperty("message", out var m) ? m.GetString() ?? "CDP error" : "CDP error"));
            else
                tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
        }
        else if (root.TryGetProperty("method", out var method))
        {
            var p = root.TryGetProperty("params", out var ps) ? ps.Clone() : default;
            try
            {
                Event?.Invoke(method.GetString() ?? "", p);
            }
            catch (Exception ex)
            {
                Log.Write($"CDP event handler failed: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _ws.Dispose();
    }
}

/// <summary>
/// The page-level operations Palon needs, built on <see cref="CdpSession"/>.
/// All element work goes through backend node ids from the AX tree or from
/// a shadow-piercing attribute query — never through CSS paths.
/// </summary>
sealed class CdpPage : IAsyncDisposable
{
    public CdpSession Session { get; }
    public string TargetId { get; }

    CdpPage(CdpSession session, string targetId)
    {
        Session = session;
        TargetId = targetId;
    }

    public static async Task<CdpPage> OpenAsync(string wsUrl, string targetId, CancellationToken ct)
    {
        var s = await CdpSession.ConnectAsync(wsUrl, ct);
        var page = new CdpPage(s, targetId);
        await s.SendAsync("Page.enable", ct: ct);
        await s.SendAsync("DOM.enable", ct: ct);
        await s.SendAsync("Accessibility.enable", ct: ct);
        await s.SendAsync("Runtime.enable", ct: ct);
        return page;
    }

    public async Task<JsonElement?> EvalAsync(string expression, CancellationToken ct, bool awaitPromise = false)
    {
        var r = await Session.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise }, ct);
        if (r.TryGetProperty("exceptionDetails", out var ex))
            throw new CdpException("page script failed: " + (ex.TryGetProperty("text", out var t) ? t.GetString() : "?"));
        return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : null;
    }

    public async Task<string> UrlAsync(CancellationToken ct) =>
        (await EvalAsync("location.href", ct))?.GetString() ?? "";

    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        await Session.SendAsync("Page.navigate", new { url }, ct);
        await SettleAsync(ct, 15000);
    }

    public Task ReloadAsync(CancellationToken ct) => Session.SendAsync("Page.reload", new { ignoreCache = false }, ct);

    public async Task BringToFrontAsync(CancellationToken ct) => await Session.SendAsync("Page.bringToFront", ct: ct);

    public async Task<List<AxNode>> AxTreeAsync(CancellationToken ct) =>
        AxTree.Parse(await Session.SendAsync("Accessibility.getFullAXTree", new { }, ct, 20000));

    /// <summary>
    /// Waits for Lightning to settle: document complete, no visible spinner,
    /// and no DOM mutation for 500 ms (an injected observer). Never a fixed sleep.
    /// </summary>
    public async Task SettleAsync(CancellationToken ct, int timeoutMs = 10000)
    {
        const string probe = """
            (() => {
              if (!window.__palonMut) {
                window.__palonMut = Date.now();
                try { new MutationObserver(() => window.__palonMut = Date.now())
                        .observe(document, {subtree:true, childList:true, attributes:true}); } catch (e) {}
              }
              const spin = [...document.querySelectorAll('.slds-spinner, lightning-spinner, .forceListViewPlaceholder')]
                .some(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; });
              return { ready: document.readyState === 'complete', spin, quiet: Date.now() - window.__palonMut };
            })()
            """;
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var v = await EvalAsync(probe, ct);
                if (v is { } o && o.GetProperty("ready").GetBoolean() && !o.GetProperty("spin").GetBoolean()
                    && o.GetProperty("quiet").GetDouble() >= 500) return;
            }
            catch (CdpException)
            {
                // navigation in flight: the context is gone for a moment
            }
            await Task.Delay(150, ct);
        }
    }

    // ---- elements ---------------------------------------------------------------------

    /// <summary>
    /// Elements matching an attribute locator, piercing open shadow roots
    /// (synthetic shadow is light DOM anyway). Returns backend node ids of
    /// visible elements only.
    /// </summary>
    public async Task<List<int>> QueryAttrAsync(SfLocator l, CancellationToken ct)
    {
        var selector = l.FieldApiName is { } f
            ? $"[data-target-selection-name$=\"{Esc(f)}\"] input, [data-target-selection-name$=\"{Esc(f)}\"] textarea, [data-target-selection-name$=\"{Esc(f)}\"] button, lightning-input-field[field-name=\"{Esc(f.Split('.').Last())}\"] input, lightning-input-field[field-name=\"{Esc(f.Split('.').Last())}\"] textarea"
            : l.AriaLabel is { } a ? $"[aria-label=\"{Esc(a)}\"]"
            : l.Css is { } c && LocatorRanking.IsSafeCss(c) ? c
            : null;
        if (selector is null) return new List<int>();
        var js = DeepQueryJs + $"window.__palonFound = __palonDeep({JsonSerializer.Serialize(selector)}).filter(__palonVisible); window.__palonFound.length";
        var count = (await EvalAsync(js, ct))?.GetInt32() ?? 0;
        var ids = new List<int>();
        for (var i = 0; i < count && i < 5; i++)
        {
            var obj = await Session.SendAsync("Runtime.evaluate", new { expression = $"window.__palonFound[{i}]" }, ct);
            if (obj.GetProperty("result").TryGetProperty("objectId", out var oid)
                && await BackendIdAsync(oid.GetString()!, ct) is { } bid) ids.Add(bid);
        }
        return ids;
    }

    public const string DeepQueryJs = """
        window.__palonDeep = window.__palonDeep || function(sel) {
          const out = []; const seen = new Set();
          const walk = root => {
            try { root.querySelectorAll(sel).forEach(e => { if (!seen.has(e)) { seen.add(e); out.push(e); } }); } catch (e) {}
            root.querySelectorAll('*').forEach(e => { if (e.shadowRoot) walk(e.shadowRoot); });
          };
          walk(document); return out;
        };
        window.__palonVisible = window.__palonVisible || function(e) {
          const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
          return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
        };

        """;

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async Task<int?> BackendIdAsync(string objectId, CancellationToken ct)
    {
        var d = await Session.SendAsync("DOM.describeNode", new { objectId }, ct);
        return d.GetProperty("node").TryGetProperty("backendNodeId", out var b) ? b.GetInt32() : null;
    }

    async Task<string> ObjectIdAsync(int backendNodeId, CancellationToken ct)
    {
        var r = await Session.SendAsync("DOM.resolveNode", new { backendNodeId }, ct);
        return r.GetProperty("object").GetProperty("objectId").GetString()!;
    }

    /// <summary>Visible (has a box), for AX matches.</summary>
    public async Task<bool> IsVisibleAsync(int backendNodeId, CancellationToken ct)
    {
        try
        {
            var r = await CallOnAsync(backendNodeId, "function(){ return window.__palonVisible ? __palonVisible(this) : (this.getClientRects().length > 0); }", ct);
            return r?.ValueKind == JsonValueKind.True;
        }
        catch (CdpException)
        {
            return false;
        }
    }

    public async Task<JsonElement?> CallOnAsync(int backendNodeId, string functionDeclaration, CancellationToken ct, params object[] args)
    {
        var objectId = await ObjectIdAsync(backendNodeId, ct);
        var r = await Session.SendAsync("Runtime.callFunctionOn", new
        {
            objectId,
            functionDeclaration,
            arguments = args.Select(a => new { value = a }).ToArray(),
            returnByValue = true,
        }, ct);
        if (r.TryGetProperty("exceptionDetails", out _)) throw new CdpException("element script failed");
        return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : null;
    }

    /// <summary>Attributes Palon learns locators from: field api-name container, aria-label, tag, value.</summary>
    public async Task<ElementFacts> FactsAsync(int backendNodeId, CancellationToken ct)
    {
        const string fn = """
            function() {
              let f = null; let n = this;
              for (let i = 0; n && i < 12 && !f; i++) {
                if (n.getAttribute) {
                  const t = n.getAttribute('data-target-selection-name');
                  if (t && t.startsWith('sfdc:RecordField.')) f = t.substring('sfdc:RecordField.'.length);
                  else if (n.tagName === 'LIGHTNING-INPUT-FIELD' && n.getAttribute('field-name')) f = n.getAttribute('field-name');
                }
                n = n.parentNode || (n.host ?? null);
              }
              return { aria: this.getAttribute ? this.getAttribute('aria-label') : null, tag: this.tagName || '',
                       field: f, value: ('value' in this) ? String(this.value ?? '') : null };
            }
            """;
        string? role = null, name = null;
        try
        {
            var ax = await Session.SendAsync("Accessibility.getPartialAXTree", new { backendNodeId, fetchRelatives = false }, ct);
            var node = AxTree.Parse(ax).FirstOrDefault(n => !n.Ignored);
            role = node?.Role;
            name = node?.Name;
        }
        catch (CdpException)
        {
        }
        var v = await CallOnAsync(backendNodeId, fn, ct);
        string? S(string p) => v is { } o && o.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        return new ElementFacts(role, name, S("aria"), S("field"), S("tag"), S("value"));
    }

    public async Task ClickAsync(int backendNodeId, CancellationToken ct)
    {
        await Session.SendAsync("DOM.scrollIntoViewIfNeeded", new { backendNodeId }, ct);
        var box = await Session.SendAsync("DOM.getBoxModel", new { backendNodeId }, ct);
        var q = box.GetProperty("model").GetProperty("border").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var x = (q[0] + q[2] + q[4] + q[6]) / 4;
        var y = (q[1] + q[3] + q[5] + q[7]) / 4;
        await Session.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct);
        await Session.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct);
        await Session.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct);
    }

    /// <summary>Replaces the element's text the way a user would: focus, select all, insert.</summary>
    public async Task FillAsync(int backendNodeId, string text, CancellationToken ct)
    {
        await Session.SendAsync("DOM.scrollIntoViewIfNeeded", new { backendNodeId }, ct);
        await Session.SendAsync("DOM.focus", new { backendNodeId }, ct);
        await CallOnAsync(backendNodeId, "function(){ if (this.select) this.select(); else document.execCommand('selectAll'); }", ct);
        if (text.Length == 0)
            await PressAsync("Backspace", ct);
        else
            await Session.SendAsync("Input.insertText", new { text }, ct);
        // LWC inputs commit on change/blur.
        await CallOnAsync(backendNodeId, "function(){ this.dispatchEvent(new Event('change', {bubbles:true, composed:true})); }", ct);
    }

    /// <summary>A visible Lightning success toast (forceToastMessage / slds-notify_toast, success theme).</summary>
    public async Task<bool> SuccessToastAsync(CancellationToken ct)
    {
        var v = await EvalAsync(DeepQueryJs + """
            __palonDeep('.forceToastMessage, .slds-notify_toast, .toastContainer .slds-notify').filter(__palonVisible)
              .some(t => /success/i.test(t.className + ' ' + (t.getAttribute('data-key') || '') + ' ' + (t.querySelector('[data-key]')?.getAttribute('data-key') || '')))
            """, ct);
        return v?.ValueKind == JsonValueKind.True;
    }

    public async Task<string?> ValueAsync(int backendNodeId, CancellationToken ct) =>
        (await CallOnAsync(backendNodeId, "function(){ return ('value' in this) ? String(this.value ?? '') : (this.innerText || ''); }", ct))?.GetString();

    public async Task PressAsync(string key, CancellationToken ct)
    {
        var (code, vk) = key switch
        {
            "Enter" => ("Enter", 13),
            "Escape" => ("Escape", 27),
            "Tab" => ("Tab", 9),
            "Backspace" => ("Backspace", 8),
            _ => throw new ArgumentException("unsupported key " + key),
        };
        var text = key == "Enter" ? "\r" : null;
        await Session.SendAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, code, windowsVirtualKeyCode = vk, text }, ct);
        await Session.SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code, windowsVirtualKeyCode = vk }, ct);
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
