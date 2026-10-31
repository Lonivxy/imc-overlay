using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

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
    const int ProcessHandleInformation = 51;
    const int ProcessCommandLineInformation = 60;

    readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<LogEvent> _onEvent;
    readonly Func<IEnumerable<string>> _discover;
    readonly TimeSpan _pollInterval;
    readonly CancellationTokenSource _stop = new();
    readonly object _sync = new();
    bool _scanArmed, _connected, _forceImc;

    public string? ActiveLogPath { get; private set; }
    public event Action<string>? ActiveLogDetected;

    public MinecraftLogWatcher(Action<LogEvent> onEvent, bool forceImc, Func<IEnumerable<string>>? discover = null, TimeSpan? pollInterval = null)
    { _onEvent = onEvent; _forceImc = forceImc; _discover = discover ?? Discover; _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(400); }
    public void ArmScan() { lock (_sync) _scanArmed = true; }
    public void SetForceImc(bool value) { lock (_sync) _forceImc = value; }
    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));
    public void Dispose() { _stop.Cancel(); }

    void SetActiveLogPath(string path)
    {
        lock (_sync)
        {
            if (string.Equals(ActiveLogPath, path, StringComparison.OrdinalIgnoreCase)) return;
            ActiveLogPath = path;
        }
        ActiveLogDetected?.Invoke(path);
    }

    async Task RunAsync(CancellationToken token)
    {
        var nextDiscovery = DateTime.MinValue;
        string[] paths = [];
        try
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                if (DateTime.UtcNow >= nextDiscovery)
                {
                    paths = _discover().ToArray();
                    nextDiscovery = DateTime.UtcNow.AddSeconds(3);
                    if (paths.Length > 0 && string.IsNullOrEmpty(ActiveLogPath))
                    {
                        SetActiveLogPath(paths[0]);
                    }
                }
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    try { ReadNewLines(path, token); }
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
        if (cursor.Offset > initialOffset)
        {
            SetActiveLogPath(path);
        }
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

    public static List<string> FindActiveProcessLogs()
    {
        var results = new List<string>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return results;

        Process[] processes;
        try
        {
            var javaw = Process.GetProcessesByName("javaw");
            var java = Process.GetProcessesByName("java");
            processes = javaw.Concat(java).ToArray();
        }
        catch
        {
            return results;
        }

        foreach (var proc in processes)
        {
            using (proc)
            {
                try
                {
                    var logs = FindLogsForProcess(proc.Id);
                    results.AddRange(logs);
                }
                catch { }
            }
        }

        results = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        results.Sort((a, b) =>
        {
            bool aMc = a.Contains("minecraft", StringComparison.OrdinalIgnoreCase);
            bool bMc = b.Contains("minecraft", StringComparison.OrdinalIgnoreCase);
            if (aMc != bMc) return aMc ? -1 : 1;
            try
            {
                long lenA = File.Exists(a) ? new FileInfo(a).Length : 0;
                long lenB = File.Exists(b) ? new FileInfo(b).Length : 0;
                return lenB.CompareTo(lenA);
            }
            catch { return 0; }
        });

        return results;
    }

    static List<string> FindLogsForProcess(int pid)
    {
        var logs = new List<string>();
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, pid);
        if (hProcess == IntPtr.Zero)
            return logs;

        try
        {
            int needed = 0;
            int status = NtQueryInformationProcess(hProcess, ProcessHandleInformation, IntPtr.Zero, 0, out needed);
            int bufSize = Math.Max(needed * 2, 1024 * 1024);
            IntPtr pBuf = Marshal.AllocHGlobal(bufSize);
            try
            {
                status = NtQueryInformationProcess(hProcess, ProcessHandleInformation, pBuf, bufSize, out needed);
                if (status >= 0)
                {
                    long handleCount = IntPtr.Size == 8 ? Marshal.ReadInt64(pBuf) : Marshal.ReadInt32(pBuf);
                    int stride = IntPtr.Size == 8 ? 40 : 28;
                    int offset = IntPtr.Size * 2;
                    IntPtr currentProcess = GetCurrentProcess();
                    var sb = new StringBuilder(2048);

                    for (int i = 0; i < handleCount; i++)
                    {
                        int at = offset + i * stride;
                        if (at + stride > bufSize) break;

                        IntPtr handleVal = Marshal.ReadIntPtr(pBuf, at);
                        uint access = (uint)Marshal.ReadInt32(pBuf, at + IntPtr.Size * 3);

                        if ((access & (FILE_WRITE_DATA | FILE_APPEND_DATA)) == 0)
                            continue;

                        if (!DuplicateHandle(hProcess, handleVal, currentProcess, out IntPtr copy, 0, false, DUPLICATE_SAME_ACCESS))
                            continue;

                        try
                        {
                            if (GetFileType(copy) != FILE_TYPE_DISK)
                                continue;

                            sb.Clear();
                            uint len = GetFinalPathNameByHandleW(copy, sb, (uint)sb.Capacity, 0);
                            if (len > 0 && len < sb.Capacity)
                            {
                                string path = NormalizeDevicePath(sb.ToString());
                                if (path.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase))
                                {
                                    logs.Add(path);
                                }
                            }
                        }
                        finally
                        {
                            CloseHandle(copy);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }

            if (logs.Count == 0)
            {
                string cmdLine = TryGetCommandLine(hProcess);
                if (!string.IsNullOrEmpty(cmdLine))
                {
                    var dirLogs = ExtractLogsFromCommandLine(cmdLine);
                    logs.AddRange(dirLogs);
                }
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return logs;
    }

    static string TryGetCommandLine(IntPtr hProcess)
    {
        try
        {
            int needed = 0;
            NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, IntPtr.Zero, 0, out needed);
            int size = Math.Max(needed, 131072);
            IntPtr pBuf = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, pBuf, size, out needed);
                if (status >= 0)
                {
                    ushort length = (ushort)Marshal.ReadInt16(pBuf);
                    IntPtr pStr = Marshal.ReadIntPtr(pBuf, IntPtr.Size == 8 ? 8 : 4);
                    if (pStr != IntPtr.Zero && length > 0)
                    {
                        return Marshal.PtrToStringUni(pStr, length / 2) ?? "";
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }
        catch { }
        return "";
    }

    static List<string> ExtractLogsFromCommandLine(string cmd)
    {
        var list = new List<string>();
        var match = Regex.Match(cmd, @"--gameDir\s+(?:""([^""]+)""|([^\s]+))", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string dir = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var candidates = new[]
                {
                    Path.Combine(dir, "logs", "latest.log"),
                    Path.Combine(dir, "logs", "blclient", "minecraft", "latest.log"),
                    Path.Combine(dir, "logs", "blclient", "chat", "latest.log"),
                    Path.Combine(dir, ".minecraft", "logs", "latest.log")
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) list.Add(c);
                }
            }
        }
        return list;
    }

    static IEnumerable<string> Discover()
    {
        var processLogs = FindActiveProcessLogs();
        if (processLogs.Count > 0)
            return processLogs;

        var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string>
        {
            Path.Combine(app, ".minecraft", "logs", "latest.log"),
            Path.Combine(app, ".minecraft", "logs", "blclient", "minecraft", "latest.log"),
            Path.Combine(home, ".lunarclient", "profiles", "lunar", "1.8", "logs", "latest.log"),
            Path.Combine(home, ".feather", "player_logs", "latest.log")
        };
        foreach (var root in new[] { Path.Combine(app, ".minecraft", "logs"), Path.Combine(app, ".minecraft", "versions") })
        {
            try
            {
                if (Directory.Exists(root))
                    paths.AddRange(Directory.EnumerateFiles(root, "latest.log", SearchOption.AllDirectories).Take(30));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
