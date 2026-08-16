using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Bridget.Voice;

/// <summary>
/// Microsoft's free Edge Read-Aloud neural TTS — the source of "Hila", the
/// natural female Hebrew voice Windows itself doesn't ship. This is the
/// browser's own endpoint, not an official API: each connection is signed
/// the way Edge signs it (Sec-MS-GEC), and the classic host is tried first
/// with the newer one Microsoft is migrating to as backup. Any failure
/// returns null and the Speaker falls back to the offline Windows voice.
/// </summary>
static class EdgeTts
{
    const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    const string ChromiumVersion = "143.0.3650.75";
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
    const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";

    static readonly string[] Hosts =
    {
        "speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1",
        "api.msedgeservices.com/tts/cognitiveservices/websocket/v1",
    };

    public const string HebrewVoice = "he-IL-HilaNeural";
    public const string DefaultVoice = "en-US-AriaNeural";

    /// <summary>MP3 bytes for the utterance, or null on any failure (logged).</summary>
    public static async Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        foreach (var host in Hosts)
        {
            try
            {
                var audio = await SynthesizeOnceAsync(host, text, voice, ct);
                if (audio is { Length: > 0 }) return audio;
                Log.Write($"Edge TTS via {host.Split('/')[0]}: empty audio");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log.Write($"Edge TTS via {host.Split('/')[0]} failed: {ex.Message}");
            }
        }
        return null;
    }

    static async Task<byte[]?> SynthesizeOnceAsync(string host, string text, string voice, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        ws.Options.SetRequestHeader("Origin", Origin);
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");

        var url = $"wss://{host}?TrustedClientToken={TrustedClientToken}" +
                  $"&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version=1-{ChromiumVersion}" +
                  $"&ConnectionId={Guid.NewGuid():N}";
        await ws.ConnectAsync(new Uri(url), token);

        var stamp = DateTime.UtcNow.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);

        await SendTextAsync(ws,
            $"X-Timestamp:{stamp}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
            "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}", token);

        await SendTextAsync(ws,
            $"X-RequestId:{Guid.NewGuid():N}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{stamp}Z\r\n" +
            "Path:ssml\r\n\r\n" +
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{voice}'>{Escape(text)}</voice></speak>", token);

        using var audio = new MemoryStream();
        using var message = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return audio.Length > 0 ? audio.ToArray() : null;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data).Contains("Path:turn.end")) break;
            }
            else if (data.Length > 2)
            {
                // Binary frame: 2-byte big-endian header length, header text, then MP3.
                var headerLength = (data[0] << 8) | data[1];
                var start = 2 + headerLength;
                if (start > data.Length) continue;
                if (!Encoding.UTF8.GetString(data, 2, headerLength).Contains("Path:audio")) continue;
                audio.Write(data, start, data.Length - start);
            }
        }

        // No graceful close: the audio is fully buffered, and a blackholed
        // connection would hang the close handshake forever. Disposal aborts.
        return audio.ToArray();
    }

    static Task SendTextAsync(ClientWebSocket ws, string message, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>The signature Edge sends: SHA-256 of (Windows file time floored
    /// to the latest 5-minute mark + the trusted client token), upper hex.</summary>
    static string SecMsGec()
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L;
        unix -= unix % 300;
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes($"{unix * 10_000_000L}{TrustedClientToken}"));
        return Convert.ToHexString(bytes);
    }

    static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("'", "&apos;").Replace("\"", "&quot;");
}
