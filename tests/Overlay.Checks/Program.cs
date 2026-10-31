using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IMCBedwarsOverlay;

internal static class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--screenshot") { CaptureScreenshot(args[1]); return; }
        if (args.Length == 1 && args[0] == "--live") { CheckLiveAsync().GetAwaiter().GetResult(); return; }
        CheckRating();
        CheckLogs();
        CheckConfig();
        CheckApiAsync().GetAwaiter().GetResult();
        CheckStaleRosterRequest(false);
        CheckStaleRosterRequest(true);
        Console.WriteLine("All overlay checks passed.");
    }

    static async Task CheckLiveAsync()
    {
        var api = new StatsApiClient();
        var bw = await api.FetchAsync("NewbieL1314", "bedwars", CancellationToken.None);
        var ce = await api.FetchAsync("player_ypm", "counterember", CancellationToken.None);
        Check(bw.State == "ok" && bw.Matches > 0 && bw.OverallRating.HasValue, "live BW query failed");
        Check(ce.State == "ok" && ce.Matches > 0 && ce.Rating > 0, "live CounterEmber query failed");
        Console.WriteLine("Live BedWars and CounterEmber queries passed.");
    }

    static void CaptureScreenshot(string output)
    {
        var window = new MainWindow(new StatsApiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Ok(1))))), false)
        { Width = 980, Height = 552, Left = -4000, Top = -4000, ShowActivated = false, Opacity = 1, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("ApplyMode", flags)!.Invoke(window, ["bedwars", false]);
        var rows = (System.Collections.ObjectModel.ObservableCollection<PlayerRow>)typeof(MainWindow).GetField("Players", flags)!.GetValue(window)!;
        rows.Add(new PlayerRow { Username = "Example_Alex", State = "ok", GameMode = "bedwars", Matches = 120, Wins = 80, WinRate = .667, Fkdr = 3.2, AvgBeds = 1.3, BedsBroken = 156, FinalKills = 410, Kills = 625, MVPs = 30 });
        rows.Add(new PlayerRow { Username = "Example_Steve", State = "ok", GameMode = "bedwars", Matches = 75, Wins = 39, WinRate = .52, Fkdr = 1.8, AvgBeds = .8, BedsBroken = 60, FinalKills = 165, Kills = 240, MVPs = 12 });
        rows.Add(new PlayerRow { Username = "Example_Player", State = "ok", GameMode = "bedwars", Matches = 36, Wins = 15, WinRate = .417, Fkdr = .9, AvgBeds = .4, BedsBroken = 14, FinalKills = 43, Kills = 90, MVPs = 4 });
        window.Show(); window.UpdateLayout(); PumpFor(200);
        var bitmap = new RenderTargetBitmap(980, 552, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = File.Create(output)) encoder.Save(file);
        window.Close();
        Console.WriteLine("Saved screenshot with sample data.");
    }

    static void CheckRating()
    {
        var small = BedwarsRatingCalculator.Calculate(1, 1, 5, 2)!.Value;
        var large = BedwarsRatingCalculator.Calculate(100, 1, 5, 2)!.Value;
        Check(small > 1 && small < large, "low sample confidence must reduce rating");
        Check(BedwarsRatingCalculator.Calculate(0, 1, 5, 2) is null, "zero games should not have a rating");
        Check(BedwarsRatingCalculator.Calculate(20, double.NaN, 1, 0.5) is null, "invalid values should not produce a rating");
        var a = new PlayerRow { State = "ok", Matches = 20, WinRate = .5, Fkdr = 1, AvgBeds = .5, BedsBroken = 10 };
        var b = new PlayerRow { State = "ok", Matches = 20, WinRate = .5, Fkdr = 1, AvgBeds = .5, BedsBroken = 1000 };
        Check(a.OverallRating == b.OverallRating, "total beds must not inflate rating");
        Check(new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.77 }.OverallRating == 1.77, "CE rating must stay official");
        var ordinary = new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.11 };
        var warning = new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.12 };
        var high = new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.35 };
        var rounded = new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.346 };
        var roundedDown = new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.344 };
        Check(!ordinary.IsHighRating && !ordinary.IsWarningRating && ordinary.ThreatLabel == "普通", "1.11 must be ordinary");
        Check(!warning.IsHighRating && warning.IsWarningRating && warning.ThreatLabel == "危险", "1.12 must be warning");
        Check(high.IsHighRating && !high.IsWarningRating && high.ThreatLabel == "极危", "1.35 must be high");
        Check(rounded.RatingLabel == "1.35" && rounded.IsHighRating && rounded.ThreatLabel == "极危", "highlight and label must follow displayed rounding");
        Check(roundedDown.RatingLabel == "1.34" && roundedDown.IsWarningRating, "1.34 must remain warning");
        Check(!new PlayerRow { State = "loading", GameMode = "counterember", Rating = 3 }.IsHighRating, "loading players must not be highlighted");
        Check(!a.IsHighRating && b.OverallRating == 1.0, "neutral BW rating must not be highlighted");
    }

    static void CheckLogs()
    {
        var events = new List<LogEvent>();
        using var watcher = new MinecraftLogWatcher(events.Add, true);
        watcher.ArmScan();
        var path = Path.Combine(Path.GetTempPath(), "imc-overlay-check-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, "[CHAT] Alice, Bo", Encoding.UTF8);
            watcher.ReadNewLines(path, CancellationToken.None);
            Check(events.Count == 0, "partial log lines must wait for completion");
            File.AppendAllText(path, "b\n", Encoding.UTF8);
            watcher.ReadNewLines(path, CancellationToken.None);
            Check(events.Count == 1 && events[0].Kind == LogEventKind.Roster && events[0].Names.SequenceEqual(["Alice", "Bob"]), "roster parsing failed");
            watcher.ReadNewLines(path, CancellationToken.None);
            Check(events.Count == 1, "log lines must not be processed twice");
            watcher.ProcessLine("[CHAT] [+] Charlie");
            Check(events[^1].Kind == LogEventKind.PlayerJoined, "join parsing failed");
            var uiThread = Environment.CurrentManagedThreadId;
            var callbackThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var background = new MinecraftLogWatcher(_ => callbackThread.TrySetResult(Environment.CurrentManagedThreadId), true, () => [path], TimeSpan.FromMilliseconds(20));
            background.ArmScan(); background.Start();
            Check(callbackThread.Task.Wait(TimeSpan.FromSeconds(5)) && callbackThread.Task.Result != uiThread, "log parsing must run off the caller thread");
        }
        finally { File.Delete(path); }
    }

    static void CheckConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imc-overlay-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); var path = Path.Combine(dir, "config.json");
        try
        {
            File.WriteAllText(path, "{\"unrelatedSetting\":42}");
            var config = new ConfigService(path); config.Save(new AppSettings(false, .7, "counterember"));
            var text = File.ReadAllText(path);
            Check(text.Contains("unrelatedSetting") && config.Load() == new AppSettings(false, .7, "counterember"), "config did not round-trip or preserve unknown fields");
        }
        finally { File.Delete(path); Directory.Delete(dir); }
    }

    static async Task CheckApiAsync()
    {
        int calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            calls++;
            if (calls == 1) { var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)); return Task.FromResult(limited); }
            return Task.FromResult(Ok(42));
        });
        var client = new StatsApiClient(new HttpClient(handler));
        var watch = Stopwatch.StartNew(); var row = await client.FetchAsync("Alice", "bedwars", CancellationToken.None);
        Check(row.State == "ok" && row.Matches == 42 && calls == 2 && watch.Elapsed >= TimeSpan.FromMilliseconds(900), "Retry-After was not honored");
        await client.FetchAsync("Alice", "bedwars", CancellationToken.None);
        Check(calls == 2, "successful result was not cached");
        calls = 0;
        var bad = new StatsApiClient(new HttpClient(new FakeHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)); })));
        Check((await bad.FetchAsync("Alice", "bedwars", CancellationToken.None)).State == "error" && calls == 1, "permanent HTTP error should not retry");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new StatsApiClient(new HttpClient(new FakeHandler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok(1); })));
        using var stop = new CancellationTokenSource(); var request = blocked.FetchAsync("Alice", "bedwars", stop.Token); await entered.Task; stop.Cancel();
        try { await request; throw new Exception("cancelled request completed"); } catch (OperationCanceledException) { }
        var retrySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new StatsApiClient(new HttpClient(new FakeHandler((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)); retrySeen.SetResult(); return Task.FromResult(r); })));
        using var cancelDelay = new CancellationTokenSource(); var delayed = waiting.FetchAsync("Alice", "bedwars", cancelDelay.Token); await retrySeen.Task; await Task.Delay(100); cancelDelay.Cancel();
        try { await delayed; throw new Exception("retry delay ignored cancellation"); } catch (OperationCanceledException) { }
    }

    static void CheckStaleRosterRequest(bool switchMode)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var replies = new ConcurrentQueue<TaskCompletionSource<HttpResponseMessage>>();
        var handler = new FakeHandler((_, _) => { var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); replies.Enqueue(reply); return reply.Task; });
        var window = new MainWindow(new StatsApiClient(new HttpClient(handler)), false);
        var cache = (ConcurrentDictionary<string, ImageSource>)typeof(MainWindow).GetField("AvatarCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var avatar = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); avatar.Freeze(); cache["Alice"] = avatar;
        var methods = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("ApplyMode", methods)!.Invoke(window, ["bedwars", false]);
        var setRoster = typeof(MainWindow).GetMethod("SetRoster", methods)!;
        setRoster.Invoke(window, [new[] { "Alice" }]);
        var rows = (System.Collections.ObjectModel.ObservableCollection<PlayerRow>)typeof(MainWindow).GetField("Players", methods)!.GetValue(window)!;
        var old = rows[0]; PumpUntil(() => replies.Count >= 1);
        if (switchMode) typeof(MainWindow).GetMethod("ApplyMode", methods)!.Invoke(window, ["counterember", true]);
        else setRoster.Invoke(window, [new[] { "Alice" }]);
        PumpUntil(() => replies.Count >= 2);
        replies.TryDequeue(out var first); replies.TryDequeue(out var second);
        Check(first != null && second != null, "requests were not started");
        second!.SetResult(Ok(42)); PumpUntil(() => rows[0].State == "ok");
        first!.SetResult(Ok(999)); PumpUntil(() => first.Task.IsCompleted); PumpFor(200);
        Check(rows.Count == 1 && rows[0].Matches == 42 && rows[0].GameMode == (switchMode ? "counterember" : "bedwars") && old.State == "loading", "stale request changed a replaced player");
    }
    static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) PumpFor(20);
        Check(condition(), "timed out waiting for dispatcher");
    }
    static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static HttpResponseMessage Ok(int matches) => new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { summary = new { name = "Alice", matches, wins = 1, winRate = 0.5, metrics = new { fkdr = 1, avgBeds = 0.5 } } }), Encoding.UTF8, "application/json") };
    sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken); }
}
