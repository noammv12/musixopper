using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Whisper.net.LibraryLoader;

namespace Saley.Notes;

/// <summary>
/// Loads the whisper.cpp natives. They ship as embedded resources (the
/// NuGet runtime package delivers them as loose content files, which a
/// single-file publish would silently drop), get extracted once to
/// %LOCALAPPDATA%\Saley\whisper-runtime\, and are loaded manually in
/// dependency order; RuntimeOptions.LoadedLibrary then bypasses
/// Whisper.net's own path probing entirely.
/// </summary>
static class WhisperRuntime
{
    const string Version = "1.9.1";

    // whisper.cpp's verified dependency chain — order matters.
    static readonly string[] LoadOrder =
    {
        "ggml-base-whisper.dll",
        "ggml-cpu-whisper.dll",
        "ggml-whisper.dll",
        "whisper.dll",
    };

    static readonly object Gate = new();
    static bool _loaded;

    public static string? UnavailableReason { get; private set; }

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return true;
            if (UnavailableReason is not null) return false;

            if (!Avx2.IsSupported || !Fma.IsSupported)
            {
                UnavailableReason = "This CPU doesn't support transcription (AVX2 required).";
                Log.Write("Whisper: CPU lacks AVX2/FMA");
                return false;
            }

            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Saley", "whisper-runtime", Version);
                Directory.CreateDirectory(dir);

                var assembly = typeof(WhisperRuntime).Assembly;
                foreach (var name in LoadOrder)
                    ExtractIfNeeded(assembly, "Saley.WhisperNative." + name, Path.Combine(dir, name));
                foreach (var name in LoadOrder)
                    NativeLibrary.Load(Path.Combine(dir, name));

                RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu; // bypass Whisper.net's own probing
                _loaded = true;
                Log.Write("Whisper natives loaded");
                return true;
            }
            catch (Exception ex)
            {
                UnavailableReason = "Transcription engine failed to load — see log (VC++ 2022 runtime missing?).";
                Log.Write($"Whisper native load failed: {ex.Message}");
                return false;
            }
        }
    }

    static void ExtractIfNeeded(Assembly assembly, string resourceName, string target)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"missing embedded resource {resourceName}");
        if (File.Exists(target) && new FileInfo(target).Length == resource.Length) return;
        var tmp = target + ".tmp";
        using (var file = File.Create(tmp))
        {
            resource.CopyTo(file);
        }
        File.Move(tmp, target, overwrite: true);
    }
}
