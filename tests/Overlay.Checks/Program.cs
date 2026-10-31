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
        CheckLeaderboard();
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
        var alex = new PlayerRow { Username = "Example_Alex", State = "ok", GameMode = "bedwars", Matches = 120, Wins = 80, WinRate = .667, Fkdr = 3.2, AvgBeds = 1.3, BedsBroken = 156, FinalKills = 410, Kills = 625, MVPs = 30 };
        alex.FkdrRank = new StatRankInfo(1); alex.WinRateRank = new StatRankInfo(2); alex.MVPRank = new StatRankInfo(3); alex.FinalsRank = new StatRankInfo(8); alex.BedsRank = new StatRankInfo(25); alex.KillsRank = new StatRankInfo(88); alex.MatchesRank = new StatRankInfo(12);
        rows.Add(alex);

        var steve = new PlayerRow { Username = "Example_Steve", State = "ok", GameMode = "bedwars", Matches = 75, Wins = 39, WinRate = .52, Fkdr = 1.8, AvgBeds = .8, BedsBroken = 60, FinalKills = 165, Kills = 240, MVPs = 12 };
        steve.FkdrRank = new StatRankInfo(15); steve.WinRateRank = new StatRankInfo(35); steve.MVPRank = new StatRankInfo(9); steve.FinalsRank = new StatRankInfo(42); steve.BedsRank = new StatRankInfo(65); steve.KillsRank = new StatRankInfo(70); steve.MatchesRank = new StatRankInfo(55);
        rows.Add(steve);

        var player = new PlayerRow { Username = "Example_Player", State = "ok", GameMode = "bedwars", Matches = 36, Wins = 15, WinRate = .417, Fkdr = .9, AvgBeds = .4, BedsBroken = 14, FinalKills = 43, Kills = 90, MVPs = 4 };
        player.FkdrRank = StatRankInfo.None; player.WinRateRank = StatRankInfo.None; player.MVPRank = StatRankInfo.None; player.FinalsRank = StatRankInfo.None; player.BedsRank = StatRankInfo.None; player.KillsRank = StatRankInfo.None; player.MatchesRank = StatRankInfo.None;
        rows.Add(player);
        window.Show(); window.UpdateLayout(); PumpFor(200);
        var bitmap = new RenderTargetBitmap(980, 552, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = File.Create(output)) encoder.Save(file);
        window.Close();
        Console.WriteLine("Saved screenshot with sample data.");
    }

    static void CheckLeaderboard()
    {
        // 1. StatRankInfo visual checks
        var r1 = new StatRankInfo(1);
        Check(r1.Tag == "#1" && r1.Effect != null && r1.Brush.Color == RankVisuals.GoldBrush.Color, "Rank 1 must be gold glowing");

        var r2 = new StatRankInfo(2);
        Check(r2.Tag == "#2" && r2.Effect != null && r2.Brush.Color == RankVisuals.SilverBrush.Color, "Rank 2 must be silver glowing");

        var r3 = new StatRankInfo(3);
        Check(r3.Tag == "#3" && r3.Effect != null && r3.Brush.Color == RankVisuals.BronzeBrush.Color, "Rank 3 must be bronze glowing");

        var r5 = new StatRankInfo(5);
        Check(r5.Tag == "#5" && r5.Effect == null && r5.Brush.Color == RankVisuals.Top10Brush.Color, "Rank 5 must be red");

        var r25 = new StatRankInfo(25);
        Check(r25.Tag == "#25" && r25.Effect == null && r25.Brush.Color == RankVisuals.Top50Brush.Color, "Rank 25 must be orange");

        var r88 = new StatRankInfo(88);
        Check(r88.Tag == "#88" && r88.Effect == null && r88.Brush.Color == RankVisuals.OtherBrush.Color, "Rank 88 must be gray");

        var rNone = new StatRankInfo(null);
        Check(rNone.Tag == "#-" && rNone.Effect == null && rNone.Brush.Color == RankVisuals.NoneBrush.Color, "Null rank must be #-");

        var rEmpty = StatRankInfo.Empty;
        Check(rEmpty.Tag == "", "Empty rank must have empty tag for non-ok states");

        // 2. LeaderboardData calculation checks
        var p1 = new LeaderboardPlayer { Name = "P1", Matches = 11, Wins = 9, Mvps = 9, WinRate = 0.818, Metrics = new ApiMetrics { Fkdr = 53, FinalKills = 53, BedsBroken = 25, Kills = 180, Kd = 20 } };
        var p2 = new LeaderboardPlayer { Name = "P2", Matches = 16, Wins = 15, Mvps = 5, WinRate = 0.938, Metrics = new ApiMetrics { Fkdr = 18, FinalKills = 18, BedsBroken = 7, Kills = 44, Kd = 2.1 } };
        var p3 = new LeaderboardPlayer { Name = "P3", Matches = 14, Wins = 11, Mvps = 11, WinRate = 0.786, Metrics = new ApiMetrics { Fkdr = 17, FinalKills = 34, BedsBroken = 22, Kills = 54, Kd = 2.0 } };

        var lb = new LeaderboardData([p1, p2, p3], "bedwars", DateTime.UtcNow);
        Check(lb.GetRankFkdr(53) == 1, "P1 FKDR must be #1");
        Check(lb.GetRankFkdr(18) == 2, "P2 FKDR must be #2");
        Check(lb.GetRankFkdr(17) == 3, "P3 FKDR must be #3");
        Check(lb.GetRankWinRate(0.938) == 1, "P2 WinRate must be #1");
        Check(lb.GetRankWinRate(0.818) == 2, "P1 WinRate must be #2");
        Check(lb.GetRankMVPs(11) == 1, "P3 MVPs must be #1");
        Check(lb.GetRankMVPs(0) == null, "0 MVPs must have no rank");
        Check(lb.GetRankFkdr(0) == null, "0 FKDR must have no rank");
        Check(lb.GetRankFkdr(5) == null, "FKDR worse than all players must return null (#-)");

        // 3. PlayerRow application checks
        var row = new PlayerRow { State = "ok", Username = "TestPlayer", Matches = 20, WinRate = 0.85, Fkdr = 20, MVPs = 10, BedsBroken = 15, FinalKills = 30, Kills = 100 };
        row.ApplyLeaderboard(lb);
        Check(row.WinRateRank.Tag == "#2" && row.WinRateRank.Brush.Color == RankVisuals.SilverBrush.Color, "Row WinRate rank 2 must be silver");
        Check(row.FkdrRank.Tag == "#2" && row.FkdrRank.Brush.Color == RankVisuals.SilverBrush.Color, "Row FKDR rank 2 must be silver");

        var loadingRow = new PlayerRow { State = "loading", Username = "TestLoading" };
        loadingRow.ApplyLeaderboard(lb);
        Check(loadingRow.MatchesRank.Tag == "", "Loading row must not display rank tag");
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

            // Device path normalization check
            Check(MinecraftLogWatcher.NormalizeDevicePath(@"\\?\D:\games\minecraft\logs\latest.log") == @"D:\games\minecraft\logs\latest.log", "device path prefix must be normalized");
            Check(MinecraftLogWatcher.NormalizeDevicePath(@"\\?\UNC\server\share\latest.log") == @"\\server\share\latest.log", "UNC device path must be normalized");

            // Active process log discovery check
            var activeLogs = MinecraftLogWatcher.FindActiveProcessLogs();
            foreach (var l in activeLogs)
            {
                Check(l.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase), $"discovered log must end with latest.log: {l}");
                Check(File.Exists(l), $"discovered log file must exist: {l}");
            }

            // Verify active log path event
            string? detected = null;
            using var activeWatcher = new MinecraftLogWatcher(_ => { }, true);
            activeWatcher.ActiveLogDetected += p => detected = p;
            activeWatcher.ReadNewLines(path, CancellationToken.None);
            Check(detected == path, "active log path must be updated on new lines");
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
