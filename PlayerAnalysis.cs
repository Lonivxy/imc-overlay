using System.Globalization;

namespace IMCBedwarsOverlay;

internal sealed record PartyMemberResult(
    string Username,
    string Role,
    string Rating,
    string WinRate,
    string Matches,
    string Metrics);

internal sealed record PartyAnalysisResult(
    string Summary,
    IReadOnlyList<PartyMemberResult> Members);

internal sealed record ComparisonMetricResult(
    string Metric,
    string RedValue,
    string BlueValue,
    string Advantage);

internal sealed record PlayerComparisonResult(
    string Summary,
    int RedWins,
    int BlueWins,
    int Ties,
    IReadOnlyList<ComparisonMetricResult> Metrics);

internal static class PlayerAnalysis
{
    sealed record BwMetrics(PlayerRow Player, double BedsPerGame, double Fkdr, double Kd, double WinRate, double Rating);

    public static PartyAnalysisResult AnalyzeParty(IReadOnlyList<PlayerRow> players, string mode)
    {
        if (players.Count is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(players), "Party analysis supports two to four players.");
        if (players.Any(p => p.State != "ok"))
            throw new ArgumentException("All party members must have valid stats.", nameof(players));
        if (players.Any(p => p.Matches <= 0))
            throw new ArgumentException("All party members must have at least one recorded match.", nameof(players));

        var roles = mode == "counterember"
            ? AssignCounterEmberRoles(players)
            : mode == "bedwars"
                ? AssignBedwarsRoles(players)
                : throw new ArgumentException("Party analysis supports BedWars and CounterEmber.", nameof(mode));

        var ratings = players.Select(p => p.OverallRating).Where(r => r is double value && double.IsFinite(value)).Select(r => r!.Value).ToArray();
        var ratedWinRates = players.Where(p => p.Matches > 0).Select(p => p.WinRate).ToArray();
        var spread = ratings.Length > 1 ? ratings.Max() - ratings.Min() : 0;
        var structure = ratings.Length < 2 ? "评分样本不足"
            : spread >= .8 ? $"核心带动（分差 {spread:F2}）"
            : spread <= .35 ? "全员精锐（实力均衡）" : $"梯队协同（分差 {spread:F2}）";
        var ratingName = mode == "counterember" ? "Rating" : "Rating Pro";
        var avgRatingLabel = ratings.Length > 0 ? ratings.Average().ToString("F2", CultureInfo.CurrentCulture) : "未评级";
        var avgWinRateLabel = ratedWinRates.Length > 0 ? $"{ratedWinRates.Average() * 100:F1}%" : "—";
        var summary = $"队伍平均 {ratingName} {avgRatingLabel} · 平均胜率 {avgWinRateLabel} · {structure}";

        var members = players.Select(p => new PartyMemberResult(
            p.Username,
            roles[p],
            p.RatingLabel,
            p.Matches > 0 ? p.WinRate.ToString("P1", CultureInfo.CurrentCulture) : "—",
            p.Matches > 0 ? $"{p.Matches}{(p.Matches < 10 ? "*" : "")}" : "—",
            mode == "counterember"
                ? $"ADR {p.Adr:F1} · K/D {p.KD:F2} · 爆头率 {p.HsRate:P1}"
                : $"场均拆床 {(p.Matches > 0 ? p.BedsBroken / (double)p.Matches : 0):F2} · FKDR {p.Fkdr:F2} · K/D {p.KD:F2}"))
            .ToArray();

        return new PartyAnalysisResult(summary, members);
    }

    public static PlayerComparisonResult Compare(PlayerRow red, PlayerRow blue, string mode)
    {
        if (red.State != "ok" || blue.State != "ok")
            throw new ArgumentException("Both players must have valid stats.");

        var metrics = new List<ComparisonMetricResult>();
        var redWins = 0;
        var blueWins = 0;
        var ties = 0;

        void Add(string label, double? redValue, double? blueValue, string format, bool lowerIsBetter = false)
        {
            var redValid = redValue is double rv && double.IsFinite(rv);
            var blueValid = blueValue is double bv && double.IsFinite(bv);
            var redText = redValid ? string.Format(CultureInfo.CurrentCulture, format, redValue!.Value) : "—";
            var blueText = blueValid ? string.Format(CultureInfo.CurrentCulture, format, blueValue!.Value) : "—";
            if (!redValid || !blueValid)
            {
                metrics.Add(new ComparisonMetricResult(label, redText, blueText, "不可比"));
                return;
            }

            int result = redValue!.Value.CompareTo(blueValue!.Value);
            if (lowerIsBetter) result *= -1;
            if (result > 0) { redWins++; metrics.Add(new(label, redText, blueText, red.Username)); }
            else if (result < 0) { blueWins++; metrics.Add(new(label, redText, blueText, blue.Username)); }
            else { ties++; metrics.Add(new(label, redText, blueText, "平")); }
        }

        if (mode == "bedwars")
        {
            Add("Rating Pro", red.OverallRating, blue.OverallRating, "{0:F2}");
            Add("FKDR", red.Matches > 0 ? red.Fkdr : null, blue.Matches > 0 ? blue.Fkdr : null, "{0:F2}");
            Add("胜率", red.Matches > 0 ? red.WinRate * 100 : null, blue.Matches > 0 ? blue.WinRate * 100 : null, "{0:F1}%");
            Add("场均拆床", red.Matches > 0 ? red.BedsBroken / (double)red.Matches : null, blue.Matches > 0 ? blue.BedsBroken / (double)blue.Matches : null, "{0:F2}");
            Add("K/D", red.Matches > 0 ? red.KD : null, blue.Matches > 0 ? blue.KD : null, "{0:F2}");
            Add("胜场", red.Matches > 0 ? red.Wins : null, blue.Matches > 0 ? blue.Wins : null, "{0:N0}");
            Add("MVP", red.Matches > 0 ? red.MVPs : null, blue.Matches > 0 ? blue.MVPs : null, "{0:N0}");
        }
        else if (mode == "counterember")
        {
            Add("官方 Rating", red.OverallRating, blue.OverallRating, "{0:F2}");
            Add("胜率", red.Matches > 0 ? red.WinRate * 100 : null, blue.Matches > 0 ? blue.WinRate * 100 : null, "{0:F1}%");
            Add("K/D", red.Matches > 0 ? red.KD : null, blue.Matches > 0 ? blue.KD : null, "{0:F2}");
            Add("ADR", red.Matches > 0 ? red.Adr : null, blue.Matches > 0 ? blue.Adr : null, "{0:F1}");
            Add("爆头率", red.Matches > 0 ? red.HsRate * 100 : null, blue.Matches > 0 ? blue.HsRate * 100 : null, "{0:F1}%");
            Add("胜场", red.Matches > 0 ? red.Wins : null, blue.Matches > 0 ? blue.Wins : null, "{0:N0}");
        }
        else if (mode == "kitbattle")
        {
            Add("K/D", red.KD, blue.KD, "{0:F2}");
            Add("总击杀", red.Kills, blue.Kills, "{0:N0}");
            Add("经验", red.KbExperience, blue.KbExperience, "{0:N0}");
            Add("挑战胜场", red.KbChallengeWins, blue.KbChallengeWins, "{0:N0}");
            Add("最佳连杀", red.KbBestStreak, blue.KbBestStreak, "{0:N0}");
            Add("击杀榜名次", red.KbKillsRank, blue.KbKillsRank, "#{0:N0}", lowerIsBetter: true);
        }
        else
        {
            throw new ArgumentException("Unknown game mode.", nameof(mode));
        }

        var comparable = metrics.Count(m => m.Advantage != "不可比");
        var summary = comparable == 0
            ? "没有足够的共同数据可供比较。"
            : redWins > blueWins
                ? $"{red.Username} 优势 · {redWins}:{blueWins}（{comparable} 项可比，{ties} 项持平）"
                : blueWins > redWins
                    ? $"{blue.Username} 优势 · {blueWins}:{redWins}（{comparable} 项可比，{ties} 项持平）"
                    : $"双方势均力敌 · {redWins}:{blueWins}（{comparable} 项可比，{ties} 项持平）";
        return new PlayerComparisonResult(summary, redWins, blueWins, ties, metrics);
    }

    static Dictionary<PlayerRow, string> AssignBedwarsRoles(IReadOnlyList<PlayerRow> players)
    {
        var assigned = new Dictionary<PlayerRow, string>();
        var metrics = players.Select(p => new BwMetrics(p, p.Matches > 0 ? p.BedsBroken / (double)p.Matches : 0,
            p.Fkdr, p.KD, p.WinRate, p.OverallRating ?? 0)).ToArray();

        if (metrics.Length == 1)
        {
            var m = metrics[0];
            assigned[m.Player] = m.BedsPerGame >= .6 || m.Player.BedsBroken >= 30 ? "优先拆床 · Rusher"
                : m.Fkdr >= 3.5 ? "优先击杀 · Slayer"
                : m.WinRate >= .70 ? "稳定核心 · Anchor" : "全能游走 · Flex";
            return assigned;
        }

        var bestSlayer = metrics.OrderByDescending(x => x.Fkdr).ThenByDescending(x => x.Rating).First();
        var bestRusher = metrics.OrderByDescending(x => x.BedsPerGame).ThenByDescending(x => x.Player.BedsBroken).First();
        if (!ReferenceEquals(bestSlayer.Player, bestRusher.Player))
        {
            assigned[bestSlayer.Player] = "优先击杀 · Slayer";
            assigned[bestRusher.Player] = bestRusher.BedsPerGame >= .25 || bestRusher.Player.BedsBroken >= 5
                ? "优先拆床 · Rusher" : "全能游走 · Flex";
        }
        else
        {
            var others = metrics.Where(x => !ReferenceEquals(x.Player, bestSlayer.Player)).ToArray();
            var secondSlayer = others.OrderByDescending(x => x.Fkdr).First();
            var secondRusher = others.OrderByDescending(x => x.BedsPerGame).ThenByDescending(x => x.Player.BedsBroken).First();
            if (bestSlayer.BedsPerGame >= .75 && secondSlayer.Fkdr >= 3.0)
            {
                assigned[bestSlayer.Player] = "优先拆床 · Rusher";
                assigned[secondSlayer.Player] = "优先击杀 · Slayer";
            }
            else
            {
                assigned[bestSlayer.Player] = "优先击杀 · Slayer";
                if (secondRusher.BedsPerGame >= .25 || secondRusher.Player.BedsBroken >= 5)
                    assigned[secondRusher.Player] = "优先拆床 · Rusher";
            }
        }

        var unassigned = metrics.Where(x => !assigned.ContainsKey(x.Player)).ToArray();
        if (unassigned.Length > 0)
        {
            var anchor = unassigned.OrderByDescending(x => x.WinRate).ThenByDescending(x => x.Player.Matches).First();
            if (anchor.WinRate >= .65) assigned[anchor.Player] = "稳定核心 · Anchor";
        }

        foreach (var m in metrics)
        {
            if (assigned.ContainsKey(m.Player)) continue;
            assigned[m.Player] = m.BedsPerGame >= .45 ? "侧翼突破 · Flanker"
                : m.Fkdr >= 3.5 ? "强力收割 · Fragger"
                : m.BedsPerGame < .20 && m.Kd >= 1.0 ? "基地防守 · Defender"
                : m.Rating >= 1.4 ? "全能游走 · Flex" : "战术支援 · Support";
        }
        return assigned;
    }

    static Dictionary<PlayerRow, string> AssignCounterEmberRoles(IReadOnlyList<PlayerRow> players)
    {
        var assigned = new Dictionary<PlayerRow, string>();
        var entry = players.OrderByDescending(p => p.Adr).ThenByDescending(p => p.KD).First();
        assigned[entry] = "突破破点 · Entry";
        var remaining = players.Where(p => !ReferenceEquals(p, entry)).ToArray();
        if (remaining.Length > 0)
        {
            var sniper = remaining.OrderByDescending(p => p.KD).First();
            assigned[sniper] = "狙击架枪 · Sniper";
        }
        remaining = players.Where(p => !assigned.ContainsKey(p)).ToArray();
        if (remaining.Length > 0)
        {
            var clutch = remaining.OrderByDescending(p => p.WinRate).First();
            assigned[clutch] = "残局大师 · Clutch";
        }
        foreach (var player in players)
            assigned.TryAdd(player, "战术支援 · Support");
        return assigned;
    }
}
