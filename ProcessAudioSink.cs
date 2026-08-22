using System.Diagnostics;
using System.Text;

namespace AIOffice.VoiceAgent;

/// <summary>
/// <see cref="IAudioSink"/> for Linux/macOS: feeds the streaming PCM to the system player
/// (<c>aplay</c> / <c>paplay</c> on Linux, <c>afplay</c> stdin is NOT supported on macOS — there
/// aplay-pipe equivalent does not read stdin, so macOS falls back to the parked file-based path;
/// the active player here is aplay/paplay) over a continuous stdin pipe — no temp file, no gaps.
/// </summary>
public sealed class ProcessAudioSink : IAudioSink
{
    private readonly object _sync = new();
    private Process? _proc;
    private Stream? _stdin;
    private bool _started;

    /// <summary>Spawns the player with redirected stdin (aplay/paplay — read raw PCM from stdin).</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            var player = FindPlayer();
            if (player == null)
            {
                Log.LogStep("ProcessAudioSink: no aplay/paplay found");
                return;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = player,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };
                // aplay: raw PCM 24 kHz mono 16-bit ("-t raw -c 1 -r 24000 -f S16_LE"); paplay: raw
                // via --raw --rate --channels --format. Both read from stdin continuously.
                if (player.Contains("paplay", StringComparison.OrdinalIgnoreCase))
                {
                    psi.ArgumentList.Add("--raw");
                    psi.ArgumentList.Add("--rate=24000");
                    psi.ArgumentList.Add("--channels=1");
                    psi.ArgumentList.Add("--format=s16le");
                }
                else
                {
                    psi.ArgumentList.Add("-q");
                    psi.ArgumentList.Add("-t");
                    psi.ArgumentList.Add("raw");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-r");
                    psi.ArgumentList.Add("24000");
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("S16_LE");
                }
                _proc = Process.Start(psi);
                _stdin = _proc?.StandardInput.BaseStream;
                _started = _proc != null;
                if (_started) Log.LogStep($"ProcessAudioSink: {player} started (24 kHz streaming)");
            }
            catch (Exception ex)
            {
                Log.LogStep($"ProcessAudioSink start failed: {ex.Message}");
                _started = false;
            }
        }
    }

    /// <summary>Writes one synthesized sentence to the player's stdin.</summary>
    public void Write(byte[] pcm24k)
    {
        lock (_sync)
        {
            if (!_started || _stdin == null) return;
            try { _stdin.Write(pcm24k, 0, pcm24k.Length); _stdin.Flush(); }
            catch (Exception ex) { Log.LogStep($"ProcessAudioSink write failed: {ex.Message}"); }
        }
    }

    /// <summary>Closes the player for the turn (the tail was already written; the player drains
    /// its own buffer). The instance is reused across turns: Start() spawns a fresh player.</summary>
    public Task EndAsync()
    {
        lock (_sync)
        {
            if (!_started) return Task.CompletedTask;
            _started = false;
            try { _stdin?.Dispose(); } catch { }
            try { _proc?.WaitForExit(2000); } catch { }
            try { _proc?.Kill(true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _stdin = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>Releases the player process — called when the conversation closes.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _started = false;
            try { _stdin?.Dispose(); } catch { }
            try { _proc?.Kill(true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _stdin = null;
        }
    }

    private static string? FindPlayer()
    {
        foreach (var name in new[] { "paplay", "aplay" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(name, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p != null)
                {
                    p.WaitForExit(1000);
                    if (p.HasExited && p.ExitCode == 0) return name;
                }
            }
            catch { }
        }
        return null;
    }
}
