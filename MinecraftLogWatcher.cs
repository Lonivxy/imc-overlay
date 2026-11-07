using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IMCBedwarsOverlay;

internal enum LogEventKind { Roster, PlayerJoined, StatsCommand }
internal sealed record LogEvent(LogEventKind Kind, string[] Names);
internal sealed record LogSource(string Path, bool ConfirmedByOpenHandle, int? ProcessId);
internal sealed record LogCandidate(string Path, int ProcessId, bool ConfirmedByOpenHandle, bool IsForeground, DateTime LastWriteUtc);

internal sealed class MinecraftLogWatcher : IDisposable
{
    sealed class Cursor { public long Offset; public byte[] Pending = []; public bool SkipFirstLine; }
    static readonly Regex Username = new($@"^{PlayerNameRules.Pattern}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Colors = new(@"(?i)(?:§|\\u00a7|&)[0-9a-z]", RegexOptions.Compiled);
    static readonly Regex Plus = new($@"^\[\+\]\s*({PlayerNameRules.Pattern})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Joined = new($@"^({PlayerNameRules.Pattern}).*(?:joined the game|has joined)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Stats = new($@"(?:^|\s)[-!/]?stats\s+({PlayerNameRules.Pattern})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_DUP_HANDLE = 0x0040;
    const uint DUPLICATE_SAME_ACCESS = 0x0002;
    const uint FILE_TYPE_DISK = 0x0001;
    const uint FILE_WRITE_DATA = 0x0002;
    const uint FILE_APPEND_DATA = 0x0004;
    const uint FILE_NAME_OPENED = 0x0008;
    const int ProcessHandleInformation = 51;
    const int ProcessCommandLineInformation = 60;
    const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    const int InitialHandleSnapshotSize = 1024 * 1024;
    const int MaximumHandleSnapshotSize = 32 * 1024 * 1024;
    const int MaximumProcessHandleCount = 300_000;

    static readonly Regex GameDirectoryArgument = new(
        @"(?:^|\s)--gameDir(?:=|\s+)(?:""(?<quoted>[^""]+)""|(?<bare>[^\s""]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ClientMainClass = new(
        @"(?:^|\s)(?:net\.minecraft\.client\.main\.Main|net\.fabricmc\.loader\.impl\.launch\.knot\.KnotClient|org\.quiltmc\.loader\.impl\.launch\.knot\.KnotClient|net\.minecraft\.client\.Minecraft)(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex ClientLauncherClass = new(
        @"(?:^|\s)(?:net\.minecraft\.launchwrapper\.Launch|cpw\.mods\.modlauncher\.Launcher)(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<LogEvent> _onEvent;
    readonly Func<IEnumerable<string>>? _discoverPaths;
    readonly Func<LogSource?> _discoverSource;
    readonly TimeSpan _pollInterval;
    readonly CancellationTokenSource _stop = new();
    readonly object _sync = new();
    bool _scanArmed, _connected, _forceImc;
    bool _publishedSource;

    public LogSource? ActiveLogSource { get; private set; }
    public event Action<LogSource?>? ActiveLogDetected;

    public MinecraftLogWatcher(Action<LogEvent> onEvent, bool forceImc, Func<IEnumerable<string>>? discover = null, TimeSpan? pollInterval = null)
    {
        _onEvent = onEvent;
        _forceImc = forceImc;
        _discoverPaths = discover;
        _discoverSource = discover is null ? FindActiveClientLog : DiscoverInjectedPath;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(400);
    }
    public void ArmScan() { lock (_sync) _scanArmed = true; }
    public void SetForceImc(bool value) { lock (_sync) _forceImc = value; }
    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));
    public void Dispose() { _stop.Cancel(); }

    void SetActiveLogSource(LogSource? source)
    {
        bool notify;
        bool reset;
        lock (_sync)
        {
            var previous = ActiveLogSource;
            reset = !string.Equals(previous?.Path, source?.Path, StringComparison.OrdinalIgnoreCase)
                || previous?.ProcessId != source?.ProcessId;
            notify = !_publishedSource || reset
                || previous?.ConfirmedByOpenHandle != source?.ConfirmedByOpenHandle;
            ActiveLogSource = source;
            _publishedSource = true;
            if (reset)
            {
                _cursors.Clear();
                _connected = false;
                if (previous is not null)
                    _scanArmed = false;
            }
        }
        if (notify) ActiveLogDetected?.Invoke(source);
    }

    async Task RunAsync(CancellationToken token)
    {
        var nextDiscovery = DateTime.MinValue;
        Task<LogSource?>? pendingDiscovery = null;
        LogSource? source = null;
        try
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                if (pendingDiscovery is { IsCompleted: true })
                {
                    try
                    {
                        source = await pendingDiscovery.ConfigureAwait(false);
                    }
                    catch { source = null; }
                    pendingDiscovery = null;
                    SetActiveLogSource(source);
                }

                if (pendingDiscovery is null && DateTime.UtcNow >= nextDiscovery)
                {
                    pendingDiscovery = Task.Run(_discoverSource);
                    _ = pendingDiscovery.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    nextDiscovery = DateTime.UtcNow.AddSeconds(3);
                }

                token.ThrowIfCancellationRequested();
                if (source is not null)
                {
                    try { ReadNewLines(source.Path, token); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
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
        var initialOffset = cursor.Offset;
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

    internal static string NormalizeDevicePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path.Substring(4);
        return path;
    }

    internal static LogCandidate? ChooseBestCandidate(IEnumerable<LogCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.IsForeground)
            .ThenByDescending(candidate => candidate.ConfirmedByOpenHandle)
            .ThenByDescending(candidate => LogPathPriority(candidate.Path))
            .ThenByDescending(candidate => candidate.LastWriteUtc)
            .ThenBy(candidate => candidate.ProcessId)
            .FirstOrDefault();
    }

    internal static bool IsMinecraftClientCommandLine(string commandLine, out string? gameDirectory)
    {
        gameDirectory = ExtractGameDirectory(commandLine);
        if (ClientMainClass.IsMatch(commandLine))
            return true;
        return gameDirectory is not null && ClientLauncherClass.IsMatch(commandLine);
    }

    internal static string? ExtractGameDirectory(string commandLine)
    {
        var match = GameDirectoryArgument.Match(commandLine);
        if (!match.Success) return null;
        var value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value); }
        catch (Exception) { return null; }
    }

    LogSource? DiscoverInjectedPath()
    {
        if (_discoverPaths is null) return null;
        try
        {
            var candidates = _discoverPaths()
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new LogCandidate(path, 0, false, false, GetLastWriteUtc(path)));
            var chosen = ChooseBestCandidate(candidates);
            return chosen is null ? null : new LogSource(chosen.Path, false, null);
        }
        catch { return null; }
    }

    internal static LogSource? FindActiveClientLog()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var candidates = new List<LogCandidate>();
        var foregroundPid = GetForegroundProcessId();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("javaw")
                .Concat(Process.GetProcessesByName("java"))
                .ToArray();
        }
        catch { return null; }

        foreach (var process in processes)
        {
            using (process)
            {
                var pid = process.Id;
                var isForeground = pid == foregroundPid;
                string title;
                try { title = process.MainWindowTitle; }
                catch { title = ""; }

                IntPtr processHandle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, pid);
                bool canDuplicateHandles = processHandle != IntPtr.Zero;
                if (processHandle == IntPtr.Zero)
                    processHandle = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);

                try
                {
                    string? gameDirectory = null;
                    bool commandLineIdentifiesClient = false;
                    if (processHandle != IntPtr.Zero)
                    {
                        try
                        {
                            commandLineIdentifiesClient = IsMinecraftClientCommandLine(
                                TryGetCommandLine(processHandle) ?? "", out gameDirectory);
                        }
                        catch { }
                    }

                    bool titleIdentifiesClient = IsMinecraftClientWindowTitle(title);
                    var openLogs = canDuplicateHandles && processHandle != IntPtr.Zero
                        ? FindWritableLatestLogs(processHandle)
                        : [];
                    bool strongPathIdentifiesClient = openLogs.Any(IsStrongMinecraftLogPath);
                    if (!commandLineIdentifiesClient && !titleIdentifiesClient && !strongPathIdentifiesClient)
                        continue;

                    foreach (var path in openLogs)
                    {
                        candidates.Add(new LogCandidate(path, pid, true, isForeground, GetLastWriteUtc(path)));
                    }

                    if (openLogs.Count == 0)
                    {
                        foreach (var path in FindGameDirectoryLogs(gameDirectory))
                            candidates.Add(new LogCandidate(path, pid, false, isForeground, GetLastWriteUtc(path)));
                    }
                }
                catch { }
                finally
                {
                    if (processHandle != IntPtr.Zero)
                        CloseHandle(processHandle);
                }
            }
        }

        var selected = ChooseBestCandidate(candidates);
        return selected is null
            ? null
            : new LogSource(selected.Path, selected.ConfirmedByOpenHandle, selected.ProcessId);
    }

    static uint GetForegroundProcessId()
    {
        try
        {
            var window = GetForegroundWindow();
            return window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out var pid) == 0 ? 0 : pid;
        }
        catch { return 0; }
    }

    static bool IsMinecraftClientWindowTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Contains("server", StringComparison.OrdinalIgnoreCase))
            return false;
        return title.Contains("minecraft", StringComparison.OrdinalIgnoreCase)
            || title.Contains("badlion", StringComparison.OrdinalIgnoreCase)
            || title.Contains("lunar client", StringComparison.OrdinalIgnoreCase)
            || title.Contains("feather", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsStrongMinecraftLogPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains(@"\logs\blclient\minecraft\latest.log", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\.minecraft\logs\latest.log", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\.feather\player_logs\latest.log", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\.lunarclient\profiles\", StringComparison.OrdinalIgnoreCase);
    }

    static int LogPathPriority(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Contains(@"\logs\blclient\chat\latest.log", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\logs\chat\latest.log", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (normalized.Contains(@"\logs\blclient\minecraft\latest.log", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 2;
    }

    static DateTime GetLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    static List<string> FindWritableLatestLogs(IntPtr processHandle)
    {
        var logs = new List<string>();
        var snapshot = QueryHandleSnapshot(processHandle, out var bufferSize);
        if (snapshot == IntPtr.Zero) return logs;

        try
        {
            long handleCount = IntPtr.Size == 8 ? Marshal.ReadInt64(snapshot) : Marshal.ReadInt32(snapshot);
            if (handleCount is < 0 or > MaximumProcessHandleCount) return logs;

            int stride = IntPtr.Size == 8 ? 40 : 28;
            int firstEntry = IntPtr.Size * 2;
            IntPtr currentProcess = GetCurrentProcess();
            for (int i = 0; i < handleCount; i++)
            {
                long entryOffset = firstEntry + (long)i * stride;
                if (entryOffset + stride > bufferSize || entryOffset > int.MaxValue) break;
                int at = (int)entryOffset;
                var handleValue = Marshal.ReadIntPtr(snapshot, at);
                uint access = (uint)Marshal.ReadInt32(snapshot, at + IntPtr.Size * 3);
                if ((access & (FILE_WRITE_DATA | FILE_APPEND_DATA)) == 0)
                    continue;

                if (!DuplicateHandle(processHandle, handleValue, currentProcess, out var duplicate, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                try
                {
                    if (GetFileType(duplicate) != FILE_TYPE_DISK)
                        continue;
                    var path = GetOpenedHandlePath(duplicate);
                    if (path is not null
                        && string.Equals(Path.GetFileName(path), "latest.log", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path))
                        logs.Add(path);
                }
                finally { CloseHandle(duplicate); }
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(snapshot); }

        return logs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static IntPtr QueryHandleSnapshot(IntPtr processHandle, out int bufferSize)
    {
        bufferSize = 0;
        int required = 0;
        try { NtQueryInformationProcess(processHandle, ProcessHandleInformation, IntPtr.Zero, 0, out required); }
        catch { return IntPtr.Zero; }

        int size = Math.Max(InitialHandleSnapshotSize, required);
        while (size <= MaximumHandleSnapshotSize)
        {
            var buffer = Marshal.AllocHGlobal(size);
            int status;
            try { status = NtQueryInformationProcess(processHandle, ProcessHandleInformation, buffer, size, out required); }
            catch { Marshal.FreeHGlobal(buffer); return IntPtr.Zero; }

            if (status >= 0)
            {
                bufferSize = size;
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);
            if (status is not StatusInfoLengthMismatch and not StatusBufferTooSmall)
                return IntPtr.Zero;
            long next = required > size ? required : (long)size * 2;
            if (next > MaximumHandleSnapshotSize) return IntPtr.Zero;
            size = (int)next;
        }
        return IntPtr.Zero;
    }

    static string? GetOpenedHandlePath(IntPtr fileHandle)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandleW(fileHandle, buffer, (uint)buffer.Capacity, FILE_NAME_OPENED);
        if (length == 0) return null;
        if (length >= buffer.Capacity)
        {
            if (length > 32767) return null;
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(fileHandle, buffer, (uint)buffer.Capacity, FILE_NAME_OPENED);
        }
        if (length == 0 || length >= buffer.Capacity) return null;
        return NormalizeDevicePath(buffer.ToString());
    }

    static string? TryGetCommandLine(IntPtr processHandle)
    {
        try
        {
            int needed = 0;
            NtQueryInformationProcess(processHandle, ProcessCommandLineInformation, IntPtr.Zero, 0, out needed);
            int size = Math.Clamp(needed, 4096, 1024 * 1024);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryInformationProcess(processHandle, ProcessCommandLineInformation, buffer, size, out needed);
                    if (status >= 0)
                    {
                        ushort length = (ushort)Marshal.ReadInt16(buffer);
                        IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
                        return text == IntPtr.Zero || length == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }

                if (needed <= size || needed > 1024 * 1024) return null;
                size = needed;
            }
        }
        catch { }
        return null;
    }

    internal static IEnumerable<string> FindGameDirectoryLogs(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return [];
        var paths = new[]
        {
            Path.Combine(gameDirectory, "logs", "blclient", "minecraft", "latest.log"),
            Path.Combine(gameDirectory, "logs", "latest.log"),
            Path.Combine(gameDirectory, "logs", "blclient", "chat", "latest.log"),
            Path.Combine(gameDirectory, ".minecraft", "logs", "latest.log")
        };
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists);
    }
}
