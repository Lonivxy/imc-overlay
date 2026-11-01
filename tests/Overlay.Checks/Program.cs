using System.Windows;
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
        if (args.Length == 2 && args[0] == "--ce-details") { CaptureCounterEmberDetails(args[1]); return; }
        if (args.Length == 2 && args[0] == "--card") { RenderCardSample(args[1]); return; }
        if (args.Length == 1 && args[0] == "--live") { CheckLiveAsync().GetAwaiter().GetResult(); return; }
        if (args.Length == 1 && args[0] == "--log-source")
        {
            var source = MinecraftLogWatcher.FindActiveClientLog();
            Console.WriteLine(source is null
                ? "No active Minecraft client log was identified."
                : $"{(source.ConfirmedByOpenHandle ? "Confirmed from an open Java file handle" : "Inferred from the Java game directory")}: {source.Path}");
            return;
        }
        CheckRating();
        CheckLeaderboard();
        CheckCardRenderer();
        CheckLogs();
        CheckConfig();
        CheckApiAsync().GetAwaiter().GetResult();
        CheckStaleRosterRequest(false);
        CheckStaleRosterRequest(true);
        Console.WriteLine("All overlay checks passed.");
    }

    static void RenderCardSample(string outputPath)
    {
        var player = new PlayerRow
        {
            Username = "Pk1m_",
            State = "ok",
            GameMode = "bedwars",
            Matches = 49,
            Wins = 20,
            WinRate = 0.408,
            Fkdr = 1.62,
            AvgBeds = 0.33,
            BedsBroken = 16,
            FinalKills = 47,
            Kills = 201,
            MVPs = 12,
            Rating = 1.03
        };
        player.RatingRank = new StatRankInfo(86);
        player.WinRateRank = new StatRankInfo(91);
        player.WinsRank = new StatRankInfo(38);
        player.FkdrRank = new StatRankInfo(71);
        player.MVPRank = new StatRankInfo(26);
        player.BedsRank = new StatRankInfo(66);
        player.FinalsRank = new StatRankInfo(26);
        player.KillsRank = new StatRankInfo(13);
        player.MatchesRank = new StatRankInfo(42);

        // Fetch avatar for Pk1m_
        try
        {
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var bytes = http.GetByteArrayAsync("https://crafthead.net/avatar/Pk1m_/32").GetAwaiter().GetResult();
            using var ms = new MemoryStream(bytes);
            var av = new BitmapImage();
            av.BeginInit();
            av.CacheOption = BitmapCacheOption.OnLoad;
            av.StreamSource = ms;
            av.EndInit();
            av.Freeze();
            player.Avatar = av;
        }
        catch { }

        // Load wallpaper or fallback gradient
        BitmapSource bg;
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IMCBedwarsOverlay", "wallpaper_ocean_monument.png");
        if (File.Exists(cachePath))
        {
            using var fs = File.OpenRead(cachePath);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = fs;
            img.EndInit();
            img.Freeze();
            bg = img;
        }
        else
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var grad = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#0C1A2E"), (Color)ColorConverter.ConvertFromString("#050A14"), new Point(0, 0), new Point(1, 1));
                dc.DrawRectangle(grad, null, new Rect(0, 0, 1920, 1080));
            }
            var fallback = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            fallback.Render(visual);
            fallback.Freeze();
            bg = fallback;
        }

        var bitmap = StatsCardRenderer.Render(player, bg, DateTime.Now);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine("Saved stats card sample to: " + outputPath);
    }

    static void CaptureCounterEmberDetails(string outputPath)
    {
        var player = new PlayerRow
        {
            Username = "Example_CE",
            Uuid = "7ba72be0-3a2e-4bba-9d32-6c79458e681a",
            State = "ok",
            GameMode = "counterember",
            Matches = 120,
            Wins = 73,
            MVPs = 14,
            WinRate = .608,
            Rating = 1.63,
            MaxRating = 1.91,
            KD = 1.48,
            Kast = .72,
            Adr = 96.4,
            HsRate = .38,
            Kills = 860,
            Plants = 38,
            Defuses = 21,
            RatingTrend = [1.15, 1.21, 1.19, 1.34, 1.27, 1.48, 1.44, 1.63]
        };
        player.RecentMatches = Enumerable.Range(0, 6).Select(i => new ApiRecentMatch
        {
            MatchId = $"match-{i + 1}",
            Mode = "COUNTEREMBER_BOMB_DEFUSAL",
            Map = i % 2 == 0 ? "Foundry" : "Ruins",
            Team = i % 2 == 0 ? "RED" : "BLUE",
            Won = i % 3 != 0,
            Mvp = i == 1,
            Winner = "RED",
            Teams = [new ApiTeamScore { Id = "RED", Score = 13 }, new ApiTeamScore { Id = "BLUE", Score = 9 }],
            EndedAt = DateTimeOffset.Now.AddHours(-i * 4),
            Counted = true,
            Stats = new ApiRecentStats { Kills = 18 - i, Deaths = 9 + i, Assists = 6, Headshots = 8, Damage = 1450, Plants = 1, Defuses = 2, KastRounds = 17, RoundsPlayed = 25, RatingQualified = 20, MvpEligible = 1 },
            Metrics = new ApiRecentMetrics { Rating = 1.45 + i * .03, MaxRating = 1.52, Kd = 1.7, Adr = 90.3, Kast = .72, HsRate = .41, Kills = 18 - i }
        }).ToList();
        var window = new CounterEmberDetailsWindow(player)
        {
            Left = -4000,
            Top = -4000,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        window.UpdateLayout();
        PumpFor(100);
        var bitmap = new RenderTargetBitmap(1040, 780, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var file = File.Create(outputPath)) encoder.Save(file);
        window.Close();
        Console.WriteLine("Saved CounterEmber details preview.");
    }

    static void CheckCardRenderer()
    {
        var dummyBg = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        var player = new PlayerRow
        {
            Username = "TestPlayer",
            State = "ok",
            GameMode = "bedwars",
            Matches = 150,
            Wins = 100,
            WinRate = 0.667,
            Fkdr = 4.5,
            AvgBeds = 1.2,
            BedsBroken = 180,
            FinalKills = 450,
            Kills = 800,
            MVPs = 35
        };
        player.RatingRank = new StatRankInfo(1);
        player.WinRateRank = new StatRankInfo(2);
        player.WinsRank = new StatRankInfo(3);
        player.FkdrRank = new StatRankInfo(5);
        player.MVPRank = new StatRankInfo(8);
        player.BedsRank = new StatRankInfo(15);
        player.FinalsRank = new StatRankInfo(25);
        player.KillsRank = new StatRankInfo(88);
        player.MatchesRank = StatRankInfo.None;

        var bitmap = StatsCardRenderer.Render(player, dummyBg, DateTime.Now);
        Check(bitmap != null && bitmap.PixelWidth == 1200 && bitmap.PixelHeight == 720, "card must render to 1200x720 bitmap");
    }

    static async Task CheckLiveAsync()
    {
        var api = new StatsApiClient();
        var bw = await api.FetchAsync("NewbieL1314", "bedwars", CancellationToken.None);
        var ce = await api.FetchAsync("player_ypm", "counterember", CancellationToken.None);
        var newestCeMatch = ce.RecentMatches.Where(match => match.EndedAt.HasValue).MaxBy(match => match.EndedAt);
        Check(bw.State == "ok" && bw.Matches > 0 && bw.OverallRating.HasValue, "live BW query failed");
        Check(ce.State == "ok" && ce.Matches > 0 && ce.Rating > 0 && ce.Uuid.Length > 0
            && ce.Kast > 0 && ce.Adr > 0 && ce.KD > 0 && ce.MaxRating > 0
            && ce.RatingTrend.Length == 30 && ce.RecentMatches.Count == 15
            && newestCeMatch?.Metrics.Rating is double latestRating
            && Math.Abs(ce.RatingTrend[^1] - latestRating) < 0.000001,
            "live CounterEmber summary, trend or recent match data is missing");
        Console.WriteLine("Live BedWars and full CounterEmber query data passed.");
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

        var ceLeaderboard = new LeaderboardData(
        [
            new LeaderboardPlayer { Name = "CE1", Matches = 20, Metrics = new ApiMetrics { Rating = 2.1, Kast = .8, Plants = 12, Defuses = 4, MaxRating = 2.3 } },
            new LeaderboardPlayer { Name = "CE2", Matches = 10, Metrics = new ApiMetrics { Rating = 1.5, Kast = .7, Plants = 8, Defuses = 2, MaxRating = 1.8 } }
        ], "counterember", DateTime.UtcNow);
        Check(ceLeaderboard.GetRankKast(.8) == 1 && ceLeaderboard.GetRankPlants(8) == 2
            && ceLeaderboard.GetRankDefuses(2) == 2 && ceLeaderboard.GetRankMaxRating(1.8) == 2,
            "CounterEmber leaderboard ranks must cover KAST, plants, defuses and max Rating");

        // 3. PlayerRow application checks
        var row = new PlayerRow { State = "ok", Username = "TestPlayer", Matches = 20, WinRate = 0.85, Fkdr = 20, MVPs = 10, BedsBroken = 15, FinalKills = 30, Kills = 100 };
        row.ApplyLeaderboard(lb);
        Check(row.WinRateRank.Tag == "#2" && row.WinRateRank.Brush.Color == RankVisuals.SilverBrush.Color, "Row WinRate rank 2 must be silver");
        Check(row.FkdrRank.Tag == "#2" && row.FkdrRank.Brush.Color == RankVisuals.SilverBrush.Color, "Row FKDR rank 2 must be silver");

        var loadingRow = new PlayerRow { State = "loading", Username = "TestLoading" };
        loadingRow.ApplyLeaderboard(lb);
        Check(loadingRow.MatchesRank.Tag == "", "Loading row must not display rank tag");

        // 4. Pinned self player & sorting checks
        var pSelf = new PlayerRow { Username = "Self", State = "ok", IsPinnedSelf = true, Matches = 5, WinRate = 0.2, Fkdr = 1.0 };
        pSelf.ApplyLeaderboard(lb);
        var pOther = new PlayerRow { Username = "Other", State = "ok", IsPinnedSelf = false, Matches = 100, WinRate = 0.99, Fkdr = 50.0 };
        pOther.ApplyLeaderboard(lb);
        var comparer = new PlayerComparer("Rating", true);
        Check(comparer.Compare(pSelf, pOther) < 0, "IsPinnedSelf row must always sort before non-pinned players");
        Check(comparer.Compare(pOther, pSelf) > 0, "Non-pinned player must sort after IsPinnedSelf row");

        // 5. LeaderboardDisplayRank & metric sorting
        pSelf.LeaderboardDisplayRank = pSelf.FkdrRank;
        pOther.LeaderboardDisplayRank = pOther.FkdrRank;
        Check(pOther.LeaderboardDisplayRank.Tag == "#2", "pOther with 50 FKDR must have #2 rank tag");
        Check(pSelf.LeaderboardDisplayRank.Tag == "#-", "pSelf with 1 FKDR must have #- rank tag");

        var rankComparer = new PlayerComparer("名次", false);
        Check(rankComparer.Compare(pSelf, pOther) < 0, "Pinned self must still be first when sorted by 名次");

        // CopyFrom should preserve Avatar and LeaderboardDisplayRank
        var pCopy = new PlayerRow();
        pCopy.CopyFrom(pOther);
        Check(pCopy.LeaderboardDisplayRank.Tag == "#2", "CopyFrom must preserve LeaderboardDisplayRank");
    }

    static void CheckRating()
    {
        var sampleRating = BedwarsRatingCalculator.Calculate(100, 60, 3, 2)!.Value;
        Check(Math.Abs(sampleRating - 1.64046894) < 0.000001, "rating should follow the new weighted log formula");
        var lowerConfidence = BedwarsRatingCalculator.Calculate(1, 1, 3, 2)!.Value;
        var higherConfidence = BedwarsRatingCalculator.Calculate(100, 60, 3, 2)!.Value;
        Check(lowerConfidence < higherConfidence, "sample confidence should reduce low-sample performance influence");
        var baseline = BedwarsRatingCalculator.Calculate(20, 10, 1, 1)!.Value;
        Check(baseline > 1.0, "the experience bonus should place a neutral 20-game sample slightly above 1.00");
        Check(BedwarsRatingCalculator.Calculate(0, 0, 1, 1) is null, "zero games should not have a rating");
        Check(BedwarsRatingCalculator.Calculate(20, 21, 1, 1) is null, "wins cannot exceed matches");
        Check(BedwarsRatingCalculator.Calculate(20, 10, double.NaN, 1) is null, "invalid ratios should not produce a rating");
        Check(BedwarsRatingCalculator.Calculate(20, 10, -1, 1) is null, "negative ratios should not produce a rating");
        Check(BedwarsRatingCalculator.Calculate(1, 0, double.MaxValue, double.MaxValue) == 3.0,
            "extreme valid ratios should be capped at the maximum rating");

        var a = new PlayerRow { State = "ok", Matches = 20, Wins = 10, WinRate = .5, Fkdr = 1, KD = 1, AvgBeds = .5, BedsBroken = 10 };
        var b = new PlayerRow { State = "ok", Matches = 20, Wins = 10, WinRate = .5, Fkdr = 1, KD = 1, AvgBeds = 100, BedsBroken = 1000 };
        Check(a.OverallRating == b.OverallRating, "average and total beds must not affect rating");
        var differentWinRate = new PlayerRow { State = "ok", Matches = 20, Wins = 15, WinRate = .25, Fkdr = 1, KD = 1 };
        Check(differentWinRate.OverallRating > a.OverallRating, "WLR should use wins and losses rather than the API winRate field");
        var lowSampleCe = new PlayerRow { State = "ok", GameMode = "counterember", Matches = 1, Rating = 1.77 };
        Check(lowSampleCe.OverallRating == 1.77 && lowSampleCe.RatingLabel == "1.77*"
            && lowSampleCe.RatingInfo.Contains("少于 10 场", StringComparison.Ordinal),
            "low-sample CE Rating must remain official and carry the sample warning");
        var lowSampleBw = new PlayerRow { State = "ok", GameMode = "bedwars", Matches = 1, Wins = 1, WinRate = .6, Fkdr = 2, KD = 2, AvgBeds = 1 };
        var establishedBw = new PlayerRow { State = "ok", GameMode = "bedwars", Matches = 10, Wins = 6, WinRate = .6, Fkdr = 2, KD = 2, AvgBeds = 1 };
        Check(lowSampleBw.RatingLabel.EndsWith('*') && !establishedBw.RatingLabel.EndsWith('*'),
            "BedWars Rating must show the sample marker only below ten matches");
        var noGamesCe = new PlayerRow { State = "ok", GameMode = "counterember", Rating = 1.77, MaxRating = 1.91 };
        Check(noGamesCe.OverallRating is null && noGamesCe.RatingLabel == "—" && noGamesCe.MaxRatingLabel == "—",
            "CE players with no matches must not display current or max Rating");
        var formatStats = typeof(MainWindow).GetMethod("FormatStats", BindingFlags.NonPublic | BindingFlags.Static)!;
        var noGamesText = (string)formatStats.Invoke(null, [noGamesCe])!;
        Check(noGamesText.Contains("Rating: — (最高 —)", StringComparison.Ordinal),
            "copied CE stats must not show a rating when there are no matches");
        var ordinary = new PlayerRow { State = "ok", GameMode = "counterember", Matches = 1, Rating = 1.11 };
        var warning = new PlayerRow { State = "ok", GameMode = "counterember", Matches = 1, Rating = 1.12 };
        var high = new PlayerRow { State = "ok", GameMode = "counterember", Matches = 1, Rating = 1.35 };
        var rounded = new PlayerRow { State = "ok", GameMode = "counterember", Matches = 1, Rating = 1.346 };
        var roundedDown = new PlayerRow { State = "ok", GameMode = "counterember", Matches = 1, Rating = 1.344 };
        Check(!ordinary.IsHighRating && !ordinary.IsWarningRating && ordinary.ThreatLabel == "普通", "1.11 must be ordinary");
        Check(!warning.IsHighRating && warning.IsWarningRating && warning.ThreatLabel == "危险", "1.12 must be warning");
        Check(high.IsHighRating && !high.IsWarningRating && high.ThreatLabel == "极危", "1.35 must be high");
        Check(rounded.RatingLabel == "1.35*" && rounded.IsHighRating && rounded.ThreatLabel == "极危", "highlight and label must follow displayed rounding");
        Check(roundedDown.RatingLabel == "1.34*" && roundedDown.IsWarningRating, "1.34 must remain warning");
        Check(!new PlayerRow { State = "loading", GameMode = "counterember", Rating = 3 }.IsHighRating, "loading players must not be highlighted");
        Check(!a.IsHighRating && !a.IsWarningRating && b.OverallRating > 1.0 && b.OverallRating < 1.12,
            "neutral BW rating must include the experience bonus and remain unhighlighted");
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

            // Prefer the game's main log over BLC's separate chat log.
            var sameClient = new[]
            {
                new LogCandidate(@"D:\instance\logs\blclient\chat\latest.log", 10, true, false, DateTime.UtcNow),
                new LogCandidate(@"D:\instance\logs\blclient\minecraft\latest.log", 10, true, false, DateTime.UtcNow.AddMinutes(-1))
            };
            Check(MinecraftLogWatcher.ChooseBestCandidate(sameClient)?.Path.EndsWith(
                @"logs\blclient\minecraft\latest.log", StringComparison.OrdinalIgnoreCase) == true,
                "the Minecraft log must be preferred over the BLC chat-only log");

            var gameDirectory = Path.Combine(Path.GetTempPath(), "imc-overlay-game-dir-" + Guid.NewGuid().ToString("N"));
            var gameLog = Path.Combine(gameDirectory, "logs", "blclient", "minecraft", "latest.log");
            Directory.CreateDirectory(Path.GetDirectoryName(gameLog)!);
            File.WriteAllText(gameLog, "game log");
            try
            {
                var gameLogs = MinecraftLogWatcher.FindGameDirectoryLogs(gameDirectory).ToArray();
                Check(gameLogs.Length == 1 && string.Equals(gameLogs[0], gameLog, StringComparison.OrdinalIgnoreCase),
                    "fallback discovery must stay inside the Java process game directory");
                Check(!MinecraftLogWatcher.FindGameDirectoryLogs(null).Any(),
                    "fallback discovery must not guess a global or stale log when the game directory is unknown");
            }
            finally { Directory.Delete(gameDirectory, true); }

            // Prefer a foreground client when more than one Java game is open.
            var multipleClients = new[]
            {
                new LogCandidate(@"C:\old\logs\latest.log", 11, true, false, DateTime.UtcNow),
                new LogCandidate(@"D:\active\logs\latest.log", 12, false, true, DateTime.UtcNow.AddHours(-1))
            };
            Check(MinecraftLogWatcher.ChooseBestCandidate(multipleClients)?.ProcessId == 12,
                "the foreground Java client must win over a different, more recently written client");

            var commandLine = @"javaw net.minecraft.client.main.Main --gameDir ""C:\Games\Bed Wars"" --accessToken never-display-this";
            Check(MinecraftLogWatcher.IsMinecraftClientCommandLine(commandLine, out var gameDir)
                && gameDir == @"C:\Games\Bed Wars", "client command line should yield only the game directory");
            Check(!MinecraftLogWatcher.IsMinecraftClientCommandLine(
                @"java -jar server.jar --nogui", out _), "a Minecraft server process must not be mistaken for a client");

            // Verify active log path event
            var detected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var activeWatcher = new MinecraftLogWatcher(_ => { }, true, () => [path], TimeSpan.FromMilliseconds(20));
            activeWatcher.ActiveLogDetected += source => { if (source is not null) detected.TrySetResult(source.Path); };
            activeWatcher.Start();
            Check(detected.Task.Wait(TimeSpan.FromSeconds(5)) && detected.Task.Result == path,
                "active log path must be published when discovery selects it");
        }
        finally { File.Delete(path); }
    }

    static void CheckConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imc-overlay-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); var path = Path.Combine(dir, "config.json");
        try
        {
            File.WriteAllText(path, "{\"unrelatedSetting\":42,\"playerName\":\"Alice\"}");
            var config = new ConfigService(path);
            Check(config.Load().PlayerName == "Alice", "playerName must load from config");
            config.Save(new AppSettings(false, .7, "counterember", "Bob"));
            var text = File.ReadAllText(path);
            Check(text.Contains("unrelatedSetting") && config.Load() == new AppSettings(false, .7, "counterember", "Bob"), "config did not round-trip or preserve unknown fields");

            var custom = new AppSettings(true, 0.85, "bedwars", "CustomPro", 0x77, 2, "Ctrl + F8", 120.0, 240.0, 1024.0, 600.0, false);
            config.Save(custom);
            Check(config.Load() == custom, "custom hotkey and window bounds config did not round-trip");
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

        // Connectivity test checks
        var connectGood = new StatsApiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        var resGood = await connectGood.CheckConnectivityAsync();
        Check(resGood.Success && resGood.ElapsedMs >= 0, "healthy connectivity test must succeed");

        var connectBad = new StatsApiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))));
        var resBad = await connectBad.CheckConnectivityAsync();
        Check(!resBad.Success, "500 response must fail connectivity test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new StatsApiClient(new HttpClient(new FakeHandler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok(1); })));
        using var stop = new CancellationTokenSource(); var request = blocked.FetchAsync("Alice", "bedwars", stop.Token); await entered.Task; stop.Cancel();
        try { await request; throw new Exception("cancelled request completed"); } catch (OperationCanceledException) { }
        var retrySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new StatsApiClient(new HttpClient(new FakeHandler((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)); retrySeen.SetResult(); return Task.FromResult(r); })));
        using var cancelDelay = new CancellationTokenSource(); var delayed = waiting.FetchAsync("Alice", "bedwars", cancelDelay.Token); await retrySeen.Task; await Task.Delay(100); cancelDelay.Cancel();
        try { await delayed; throw new Exception("retry delay ignored cancellation"); } catch (OperationCanceledException) { }

        const string ceJson = """
        {
          "summary": { "uuid": "uuid-ce", "name": "CEPlayer", "matches": 15, "wins": 8, "mvps": 2, "winRate": 0.533,
            "metrics": { "rating": 1.23, "kast": 0.65, "adr": 88.8, "kd": 1.5, "hsRate": 0.42, "kills": 120, "plants": 5, "defuses": 3, "maxRating": 1.61 } },
          "trend": [1.01, 1.10, 1.23],
          "recent": [{ "matchId": "match-1", "mode": "COUNTEREMBER_BOMB_DEFUSAL", "map": "Foundry", "team": "RED", "won": true, "mvp": false,
            "winner": "RED", "teams": [{ "id": "RED", "score": 13 }, { "id": "BLUE", "score": 9 }], "endedAt": "2026-10-31T12:30:00Z", "counted": true,
            "stats": { "kills": 8, "damage": 640, "deaths": 4, "defuses": 2, "headshots": 3, "kastRounds": 20, "plants": 1, "rating": 1.18, "assists": 5, "mvpEligible": 1, "roundsPlayed": 30, "ratingQualified": 24 },
            "metrics": { "adr": 90, "defuses": 2, "hsRate": 0.33, "kast": 0.7, "kd": 2, "kills": 8, "maxRating": 1.25, "plants": 1, "rating": 1.18 } }]
        }
        """;
        var ceClient = new StatsApiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(OkJson(ceJson)))));
        var ce = await ceClient.FetchAsync("CEPlayer", "counterember", CancellationToken.None);
        var match = ce.RecentMatches.Single();
        Check(ce.State == "ok" && ce.Uuid == "uuid-ce" && ce.Username == "CEPlayer" && ce.Matches == 15
            && ce.Wins == 8 && ce.MVPs == 2 && ce.WinRate == .533 && ce.Rating == 1.23
            && ce.Kast == .65 && ce.Adr == 88.8 && ce.KD == 1.5 && ce.HsRate == .42
            && ce.Kills == 120 && ce.Plants == 5 && ce.Defuses == 3 && ce.MaxRating == 1.61,
            "CounterEmber summary metrics must all be parsed");
        var stats = match.Stats;
        var metrics = match.Metrics;
        Check(ce.RatingTrend.SequenceEqual(new[] { 1.01, 1.10, 1.23 }) && match.MatchId == "match-1"
            && match.Mode == "COUNTEREMBER_BOMB_DEFUSAL" && match.Map == "Foundry" && match.Team == "RED"
            && match.Won == true && match.Mvp == false && match.Winner == "RED" && match.EndedAt.HasValue && match.Counted == true
            && match.Teams.Count == 2 && match.Teams[0].Id == "RED" && match.Teams[0].Score == 13 && match.Teams[1].Score == 9,
            "CounterEmber rating trend and recent match details must all be parsed");
        Check(stats.Kills == 8 && stats.Damage == 640 && stats.Deaths == 4 && stats.Plants == 1
            && stats.Rating == 1.18 && stats.Assists == 5 && stats.Defuses == 2 && stats.Headshots == 3
            && stats.KastRounds == 20 && stats.MvpEligible == 1 && stats.RoundsPlayed == 30 && stats.RatingQualified == 24,
            "CounterEmber per-match stats fields must all be parsed");
        Check(metrics.Rating == 1.18 && metrics.Kast == .7 && metrics.Adr == 90 && metrics.Kd == 2
            && metrics.HsRate == .33 && metrics.Kills == 8 && metrics.Plants == 1 && metrics.Defuses == 2
            && metrics.MaxRating == 1.25,
            "CounterEmber per-match metrics fields must all be parsed");

        var lbCalls = 0;
        var lbClient = new StatsApiClient(new HttpClient(new FakeHandler((_, _) =>
        {
            lbCalls++;
            return Task.FromResult(lbCalls == 1
                ? OkJson("""{"players":[{"name":"CEPlayer","matches":15,"wins":8,"mvps":2,"winRate":0.533,"metrics":{"rating":1.23,"kast":0.65,"adr":88.8,"kd":1.5,"hsRate":0.42,"kills":120,"plants":5,"defuses":3,"maxRating":1.61}}]}""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        })));
        var freshLb = await lbClient.GetLeaderboardWithStatusAsync("counterember", force: true);
        var cachedLb = await lbClient.GetLeaderboardWithStatusAsync("counterember", force: true);
        Check(freshLb.IsFresh && !cachedLb.IsFresh && cachedLb.Data.Players.Count == 1,
            "leaderboard refresh status must distinguish a new response from stale cache fallback");
    }

    static void CheckStaleRosterRequest(bool switchMode)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var replies = new ConcurrentQueue<TaskCompletionSource<HttpResponseMessage>>();
        var handler = new FakeHandler((_, _) => { var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); replies.Enqueue(reply); return reply.Task; });
        var fakeImages = new FakeOverlayImages();
        var avatar = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); avatar.Freeze();
        fakeImages.SetAvatar("Alice", avatar);
        var window = new MainWindow(new StatsApiClient(new HttpClient(handler)), false, fakeImages);
        var methods = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("ApplyMode", methods)!.Invoke(window, ["bedwars", false]);
        var setRoster = typeof(MainWindow).GetMethod("SetRoster", methods)!;
        setRoster.Invoke(window, [new[] { "Alice" }]);
        var rows = (System.Collections.ObjectModel.ObservableCollection<PlayerRow>)typeof(MainWindow).GetField("Players", methods)!.GetValue(window)!;
        var old = rows[0]; PumpUntil(() => replies.Count >= 1);
        if (switchMode)
        {
            typeof(MainWindow).GetField("_lbSortKey", methods)!.SetValue(window, "Kast");
            typeof(MainWindow).GetField("_lbSortDescending", methods)!.SetValue(window, false);
            typeof(MainWindow).GetMethod("ApplyMode", methods)!.Invoke(window, ["counterember", true]);
            Check((string)typeof(MainWindow).GetField("_lbSortKey", methods)!.GetValue(window)! == "Rating"
                && (bool)typeof(MainWindow).GetField("_lbSortDescending", methods)!.GetValue(window)!,
                "switching game mode must reset the leaderboard category and sort direction");
        }
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
    static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    sealed class FakeOverlayImages : IOverlayImages
    {
        readonly Dictionary<string, ImageSource> _avatars = new(StringComparer.OrdinalIgnoreCase);
        public void SetAvatar(string name, ImageSource avatar) => _avatars[name] = avatar;
        public bool TryGetAvatar(string playerName, out ImageSource? avatar) => _avatars.TryGetValue(playerName, out avatar);
        public Task<ImageSource?> GetAvatarAsync(string playerName) => Task.FromResult(_avatars.GetValueOrDefault(playerName));
        public Task<BitmapSource> GetOfficialWallpaperAsync() => Task.FromResult(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4));
    }
    sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken); }
}
