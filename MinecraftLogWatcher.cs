using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace IMCBedwarsOverlay;

internal enum LogEventKind { Roster, PlayerJoined, StatsCommand }
internal sealed record LogEvent(LogEventKind Kind, string[] Names);

internal sealed class MinecraftLogWatcher : IDisposable
{
    sealed class Cursor { public long Offset; public byte[] Pending = []; public bool SkipFirstLine; }
    static readonly Regex Username = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    static readonly Regex Colors = new(@"(?i)(?:§|\\u00a7|&)[0-9a-z]", RegexOptions.Compiled);
    static readonly Regex Plus = new(@"^\[\+\]\s*([A-Za-z0-9_]{3,16})", RegexOptions.Compiled);
    static readonly Regex Joined = new(@"^([A-Za-z0-9_]{3,16}).*(?:joined the game|has joined)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Stats = new(@"(?:^|\s)[-!/]?stats\s+([A-Za-z0-9_]{3,16})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<LogEvent> _onEvent;
    readonly Func<IEnumerable<string>> _discover;
    readonly TimeSpan _pollInterval;
    readonly CancellationTokenSource _stop = new();
    readonly object _sync = new();
    bool _scanArmed, _connected, _forceImc;

    public MinecraftLogWatcher(Action<LogEvent> onEvent, bool forceImc, Func<IEnumerable<string>>? discover = null, TimeSpan? pollInterval = null)
    { _onEvent = onEvent; _forceImc = forceImc; _discover = discover ?? Discover; _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(400); }
    public void ArmScan() { lock (_sync) _scanArmed = true; }
    public void SetForceImc(bool value) { lock (_sync) _forceImc = value; }
    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));
    public void Dispose() { _stop.Cancel(); }

    async Task RunAsync(CancellationToken token)
    {
        var nextDiscovery = DateTime.MinValue;
        string[] paths = [];
        try
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                if (DateTime.UtcNow >= nextDiscovery) { paths = _discover().ToArray(); nextDiscovery = DateTime.UtcNow.AddSeconds(3); }
                foreach (var path in paths) { token.ThrowIfCancellationRequested(); try { ReadNewLines(path, token); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal void ReadNewLines(string path, CancellationToken token)
    {
        var info = new FileInfo(path); if (!info.Exists) return;
        if (!_cursors.TryGetValue(path, out var cursor))
        {
            var offset = Math.Max(0, info.Length - 65536);
            _cursors[path] = cursor = new Cursor { Offset = offset, SkipFirstLine = offset > 0 };
        }
        if (info.Length < cursor.Offset) { cursor.Offset = 0; cursor.Pending = []; cursor.SkipFirstLine = false; }
        if (info.Length <= cursor.Offset) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Position = cursor.Offset;
        using var buffer = new MemoryStream(); buffer.Write(cursor.Pending);
        var chunk = new byte[16384]; int count; int remaining = 1024 * 1024;
        while (remaining > 0 && (count = file.Read(chunk, 0, Math.Min(chunk.Length, remaining))) > 0) { token.ThrowIfCancellationRequested(); buffer.Write(chunk, 0, count); remaining -= count; }
        cursor.Offset = file.Position;
        var bytes = buffer.ToArray(); int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (cursor.SkipFirstLine) cursor.SkipFirstLine = false;
            else ProcessLine(Encoding.UTF8.GetString(bytes, start, i - start).TrimEnd('\r'));
            start = i + 1;
        }
        cursor.Pending = bytes[start..];
        if (cursor.Pending.Length > 1024 * 1024) { cursor.Pending = []; cursor.SkipFirstLine = true; }
    }

    internal void ProcessLine(string line)
    {
        var low = line.ToLowerInvariant();
        if (low.Contains("connecting to")) { lock (_sync) _connected = low.Contains("imc.re"); }
        var clean = Colors.Replace(line, ""); var index = clean.LastIndexOf("[CHAT]", StringComparison.Ordinal); if (index < 0) return;
        var payload = clean[(index + 6)..].Trim();
        if (payload.Contains(", "))
        {
            var names = payload.Split(", ", StringSplitOptions.TrimEntries);
            if (names.Length is >= 2 and <= 36 && names.All(n => Username.IsMatch(n)) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length)
            {
                bool use; lock (_sync) { use = _scanArmed && (_forceImc || _connected); if (use) _scanArmed = false; }
                if (use) _onEvent(new LogEvent(LogEventKind.Roster, names));
            }
        }
        var plus = Plus.Match(payload); var joined = Joined.Match(payload);
        if (plus.Success) _onEvent(new LogEvent(LogEventKind.PlayerJoined, [plus.Groups[1].Value]));
        else if (joined.Success) _onEvent(new LogEvent(LogEventKind.PlayerJoined, [joined.Groups[1].Value]));
        var stats = Stats.Match(payload);
        if (stats.Success) _onEvent(new LogEvent(LogEventKind.StatsCommand, [stats.Groups[1].Value]));
    }

    static IEnumerable<string> Discover()
    {
        var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string> { Path.Combine(app, ".minecraft", "logs", "latest.log"), Path.Combine(app, ".minecraft", "logs", "blclient", "minecraft", "latest.log"), Path.Combine(home, ".lunarclient", "profiles", "lunar", "1.8", "logs", "latest.log"), Path.Combine(home, ".feather", "player_logs", "latest.log") };
        foreach (var root in new[] { Path.Combine(app, ".minecraft", "logs"), Path.Combine(app, ".minecraft", "versions") })
        {
            try { if (Directory.Exists(root)) paths.AddRange(Directory.EnumerateFiles(root, "latest.log", SearchOption.AllDirectories).Take(30)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
