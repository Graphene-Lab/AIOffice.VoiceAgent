using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Whisper.net.Ggml;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Verifies the dependencies required by whisper.net and the TTS engine, downloading or
/// installing whatever is missing so the user does not have to do anything by hand:
/// <list type="bullet">
/// <item>ggml whisper model — auto-downloaded from Hugging Face on first use (~74 MB for "base").</item>
/// <item>Linux — <c>arecord</c> (alsa-utils) and <c>libstdc++6</c>/glibc 2.31+; installed via apt-get when absent.</item>
/// <item>Windows — Microsoft Visual C++ Redistributable 2022; installed silently when absent (only relevant for the whisper fallback path).</item>
/// </list>
/// The Kokoro ONNX model (~320 MB) is NOT handled here: KokoroSharp downloads it itself on first
/// <c>KokoroTTS.LoadModel*</c> call.
/// </summary>
public static class Dependencies
{
    /// <summary>
    /// Whisper model size, from the <c>AIOFFICE_WHISPER_MODEL</c> environment variable
    /// (tiny/base/small/medium/largev2/largev3). Default "small": near-perfect comprehension
    /// (100% on the Italian dictation test) with a reasonable 466 MB download.
    /// </summary>
    private static readonly string ModelName = (Environment.GetEnvironmentVariable("AIOFFICE_WHISPER_MODEL") ?? "small").ToLowerInvariant();

    private static GgmlType ModelType => ModelName switch
    {
        "tiny" => GgmlType.Tiny,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        "largev2" => GgmlType.LargeV2,
        "largev3" => GgmlType.LargeV3,
        _ => GgmlType.Base,
    };

    /// <summary>Full path of the whisper ggml model (resolved against the agent base directory).</summary>
    public static string ModelPath { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"ggml-{ModelName}.bin");

    /// <summary>Approximate download sizes (MB) per model, used to report download progress.</summary>
    private static readonly Dictionary<string, long> ModelSizeMb = new()
    {
        ["tiny"] = 75,
        ["base"] = 147,
        ["small"] = 487,
        ["medium"] = 1610,
        ["largev2"] = 3090,
        ["largev3"] = 3090,
    };

    /// <summary>
    /// Ensures every dependency is present. Never throws: problems are logged and reported as
    /// <c>{"type":"status"}</c> lines so recognition simply reports an error if something is unusable.
    /// </summary>
    public static void EnsureAll()
    {
        ReportStatus("Checking speech recognition dependencies...");
        EnsureWhisperModel().GetAwaiter().GetResult();
        if (OperatingSystem.IsLinux())
            EnsureLinuxTools();
        else if (OperatingSystem.IsWindows())
            EnsureWindowsVcRedist();
        ReportStatus(File.Exists(ModelPath) ? "Speech recognition dependencies OK" : "Speech recognition dependencies incomplete");
    }

    /// <summary>Downloads the ggml model when it is not already on disk.</summary>
    private static async Task EnsureWhisperModel()
    {
        if (File.Exists(ModelPath))
        {
            Log.LogStep($"Whisper model present: {ModelPath}");
            return;
        }

        Log.LogStep("Whisper model missing, downloading...");
        ReportStatus($"Downloading whisper model (ggml-{ModelName}.bin, ~{ModelSizeMb.GetValueOrDefault(ModelName, 0)} MB), first run only...");
        try
        {
            // Download to a .tmp file, close it, then move: File.Move while the writer is still
            // open (using var semantics) throws "file in use" on Windows.
            using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ModelType))
            using (var fileWriter = File.OpenWrite(ModelPath + ".tmp"))
            {
                // Chunked copy with periodic progress reporting so the caller does not look stuck.
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                var expectedMb = ModelSizeMb.GetValueOrDefault(ModelName, 0);
                var lastReported = -1L;
                while ((read = await modelStream.ReadAsync(buffer)) > 0)
                {
                    await fileWriter.WriteAsync(buffer.AsMemory(0, read));
                    total += read;
                    var mb = total / (1024 * 1024);
                    if (mb != lastReported && (mb % 10 == 0 || (expectedMb > 0 && mb >= expectedMb)))
                    {
                        lastReported = mb;
                        ReportStatus($"Downloading whisper model... {mb}/{expectedMb} MB (first run only)");
                    }
                }
            }
            File.Move(ModelPath + ".tmp", ModelPath, overwrite: true);
            Log.LogStep($"Whisper model downloaded: {ModelPath}");
        }
        catch (Exception ex)
        {
            Log.LogStep($"Whisper model download FAILED: {ex.Message}");
            try { if (File.Exists(ModelPath + ".tmp")) File.Delete(ModelPath + ".tmp"); } catch { }
        }
    }

    /// <summary>
    /// Ensures Linux tools: <c>arecord</c> (mic capture) and <c>libstdc++6</c> (whisper.cpp native).
    /// Installs via apt-get as root, or through passwordless sudo, or pkexec (GUI elevation prompt).
    /// </summary>
    private static void EnsureLinuxTools()
    {
        if (!CommandExists("arecord"))
        {
            Log.LogStep("arecord not found, installing alsa-utils...");
            ReportStatus("Installing alsa-utils (arecord) via apt-get...");
            InstallLinuxPackage("alsa-utils");
        }
        else
        {
            Log.LogStep("arecord present");
        }

        if (!LibraryExists("libstdc++.so.6"))
        {
            Log.LogStep("libstdc++6 not found, installing...");
            ReportStatus("Installing libstdc++6 via apt-get...");
            InstallLinuxPackage("libstdc++6");
        }
        else
        {
            Log.LogStep("libstdc++6 present");
        }

        if (!GlibcMeetsMinimum(2, 31))
            ReportStatus("Warning: glibc older than 2.31 — whisper.net may fail to load. Upgrade the OS or glibc.");
    }

    /// <summary>Installs a package with apt-get using the best available elevation method.</summary>
    private static void InstallLinuxPackage(string package)
    {
        var command = $"apt-get install -y {package}";
        if (IsRoot())
        {
            RunProcess("apt-get", $"install -y {package}");
            return;
        }
        if (RunProcess("sudo", $"-n {command}"))
            return;
        // pkexec shows a GUI elevation prompt — the only user interaction ever required.
        RunProcess("pkexec", command);
    }

    /// <summary>
    /// Ensures the Visual C++ Redistributable (whisper.cpp needs it on Windows).
    /// Detection: registry key Installed under HKLM\...\VC\Runtimes\x64 (covers 2015-2022).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsVcRedist()
    {
        if (IsVcRedistInstalled())
        {
            Log.LogStep("VC++ Redistributable present");
            return;
        }

        Log.LogStep("VC++ Redistributable missing, installing...");
        ReportStatus("Installing Microsoft Visual C++ Redistributable 2022 (silent)...");
        try
        {
            var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vc_redist.x64.exe");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = client.GetAsync("https://aka.ms/vs/17/release/vc_redist.x64.exe").GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var fs = File.Create(exePath);
                response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "/install /quiet /norestart",
                UseShellExecute = true,
                Verb = "runas", // one UAC elevation prompt, then fully silent
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5 * 60 * 1000);
            File.Delete(exePath);
            Log.LogStep(IsVcRedistInstalled() ? "VC++ Redistributable installed" : "VC++ Redistributable install did not complete");
        }
        catch (Exception ex)
        {
            Log.LogStep($"VC++ Redistributable install FAILED: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsVcRedistInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            return key?.GetValue("Installed") is int installed && installed == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool CommandExists(string command)
        => RunProcess("sh", $"-c \"command -v {command}\"", quiet: true);

    private static bool LibraryExists(string library)
        => RunProcess("sh", $"-c \"ldconfig -p | grep -q {library}\"", quiet: true);

    private static bool IsRoot()
        => RunProcess("sh", "-c \"test $(id -u) -eq 0\"", quiet: true);

    private static bool GlibcMeetsMinimum(int major, int minor)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "ldd", Arguments = "--version", RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var firstLine = proc.StandardOutput.ReadLine() ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(firstLine, @"(\d+)\.(\d+)");
            if (!match.Success) return false;
            var (maj, min) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            return maj > major || (maj == major && min >= minor);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a process and returns whether it exited successfully. Kills it after 2 minutes.</summary>
    private static bool RunProcess(string fileName, string arguments, bool quiet = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = quiet,
                RedirectStandardError = quiet,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (quiet)
            {
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
            }
            if (!proc.WaitForExit(120_000)) { proc.Kill(entireProcessTree: true); return false; }
            Log.LogStep($"run '{fileName} {arguments}' -> exit {proc.ExitCode}");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.LogStep($"run '{fileName}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Emits a <c>{"type":"status"}</c> line (ignored by Voice.cs, visible in the agent log).</summary>
    private static void ReportStatus(string text)
    {
        Log.LogStep(text);
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new { type = "status", text }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        catch { }
    }
}
