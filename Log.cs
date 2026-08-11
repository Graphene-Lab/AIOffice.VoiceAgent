using System.Diagnostics;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Provides step-level logging to a file for diagnostics.
/// Logs are written to <c>{AppBase}/logs/{ProcessId}.txt</c> with elapsed seconds
/// and the calling method name.
///
/// Enable via <see cref="IsEnabled"/> (default true in DEBUG builds).
/// </summary>
public static class Log
{
    /// <summary>
    /// Gets or sets whether logging is enabled.
    /// Auto-enabled in Debug builds; disabled in Release.
    /// </summary>
    public static bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// Returns the last message that was logged (for in-process inspection).
    /// </summary>
    public static string? LastLog => _lastLog;

    private static bool _isEnabled =
#if DEBUG
        true;
#else
        false;
#endif
    private static string? _lastLog;
    private static string? _logDir;
    private static string? _logFile;
    private static DateTime _processStart;
    private static readonly object _lock = new();

    /// <summary>
    /// Initializes the logger. Called once at startup.
    /// </summary>
    public static void Initialize(bool enabled)
    {
        _isEnabled = enabled;
        if (!_isEnabled) return;

        try
        {
            _processStart = Process.GetCurrentProcess().StartTime;
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            _logFile = Path.Combine(_logDir, $"{Environment.ProcessId}.txt");
            Directory.CreateDirectory(_logDir);

            // Start fresh each run
            if (File.Exists(_logFile))
                File.Delete(_logFile);

            LogStep("Logger initialized");
        }
        catch
        {
            // If logging setup fails, disable logging silently
            _isEnabled = false;
        }
    }

    /// <summary>
    /// Logs a step message to the process-specific log file.
    /// Format: <c>[elapsed_seconds] [calling_method] message</c>
    /// </summary>
    /// <param name="message">The message to log.</param>
    public static void LogStep(string message)
    {
        if (!_isEnabled) return;

        try
        {
            // Get caller method name from stack trace
            string callerMemberName = "Unknown";
            var stackTrace = new StackTrace();
            var frame = stackTrace.GetFrame(1);
            if (frame?.GetMethod() != null)
                callerMemberName = frame.GetMethod().Name;

            var elapsed = (DateTime.Now - _processStart).TotalSeconds;
            var logMessage = $"[{elapsed:F2}] [{callerMemberName}] {message}";

            lock (_lock)
            {
                _lastLog = logMessage;
                if (_logFile != null)
                    File.AppendAllText(_logFile, logMessage + Environment.NewLine);
            }
        }
        catch
        {
            // Swallow logging errors — logging must never break the app
        }
    }

    /// <summary>
    /// Logs a step message with explicit method name (avoids stack walk overhead).
    /// </summary>
    public static void LogStep(string method, string message)
    {
        if (!_isEnabled) return;

        try
        {
            var elapsed = (DateTime.Now - _processStart).TotalSeconds;
            var logMessage = $"[{elapsed:F2}] [{method}] {message}";

            lock (_lock)
            {
                _lastLog = logMessage;
                if (_logFile != null)
                    File.AppendAllText(_logFile, logMessage + Environment.NewLine);
            }
        }
        catch { }
    }
}
