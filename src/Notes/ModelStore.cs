using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Bridget.Notes;

/// <summary>
/// The Whisper voice model: downloaded once from Hugging Face into
/// %LOCALAPPDATA%\Bridget\models with resume support (.part + Range).
/// </summary>
static class ModelStore
{
    const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    const long MinValidBytes = 400_000_000; // the real file is ~488 MB
    public const string DisplaySize = "466 MB";

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridget", "models");
    public static string ModelPath => Path.Combine(Dir, "ggml-small.bin");

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static CancellationTokenSource? _cts;

    public static bool IsReady
    {
        get
        {
            try
            {
                return File.Exists(ModelPath) && new FileInfo(ModelPath).Length >= MinValidBytes;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsDownloading => _cts is not null;

    /// <summary>(receivedBytes, totalBytes or -1). Raised from a background thread.</summary>
    public static event Action<long, long>? Progress;

    /// <summary>(success, error). Raised from a background thread.</summary>
    public static event Action<bool, string?>? Completed;

    public static void StartDownload()
    {
        if (IsDownloading || IsReady) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => DownloadAsync(cts.Token));
    }

    public static void CancelDownload() => _cts?.Cancel();

    static async Task DownloadAsync(CancellationToken ct)
    {
        var part = ModelPath + ".part";
        var success = false;
        string? error = null;
        try
        {
            Directory.CreateDirectory(Dir);
            long existing = 0;
            try
            {
                if (File.Exists(part)) existing = new FileInfo(part).Length;
            }
            catch
            {
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The .part already covers the whole file (a previous run was
                // killed between the last byte and the final move) — or it's
                // junk. Finalize or restart; never wedge.
                if (existing >= MinValidBytes)
                {
                    File.Move(part, ModelPath, overwrite: true);
                    Log.Write("Whisper model download finalized from existing .part");
                    success = true;
                    return;
                }
                File.Delete(part);
                throw new IOException("resume mismatch — press Download to restart");
            }
            response.EnsureSuccessStatusCode();

            var resuming = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            var received = resuming ? existing : 0L;
            var total = response.Content.Headers.ContentLength is { } len ? len + received : -1L;

            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(part, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[1 << 16];
                long lastReport = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (received - lastReport >= 4_000_000)
                    {
                        lastReport = received;
                        Progress?.Invoke(received, total);
                    }
                }
            }

            if (new FileInfo(part).Length < MinValidBytes)
                throw new IOException("download ended before the file was complete");
            File.Move(part, ModelPath, overwrite: true);
            Log.Write("Whisper model downloaded");
            success = true;
        }
        catch (OperationCanceledException)
        {
            error = "cancelled"; // .part is kept — next attempt resumes
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Write($"Model download failed: {ex.Message}");
        }
        finally
        {
            _cts = null;
            Completed?.Invoke(success, error);
        }
    }
}
