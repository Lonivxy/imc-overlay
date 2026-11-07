using System.Collections;
using System.ComponentModel;
using System.Windows.Media;

namespace IMCBedwarsOverlay;

sealed class PlayerComparer(string key, bool descending) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not PlayerRow a || y is not PlayerRow b) return 0;
        if (a.IsPinnedSelf != b.IsPinnedSelf) return a.IsPinnedSelf ? -1 : 1;
        if (a.State == "ok" && b.State != "ok") return -1;
        if (a.State != "ok" && b.State == "ok") return 1;
        if (key == "Rating" && a.OverallRating.HasValue != b.OverallRating.HasValue) return a.OverallRating.HasValue ? -1 : 1;
        int result = key switch
        {
            "Username" => StringComparer.OrdinalIgnoreCase.Compare(a.Username, b.Username),
            "threat" => ThreatRank(a).CompareTo(ThreatRank(b)),
            "WinRate" => a.WinRate != b.WinRate ? a.WinRate.CompareTo(b.WinRate) : a.Matches.CompareTo(b.Matches),
            "MVPs" => a.MVPs != b.MVPs ? a.MVPs.CompareTo(b.MVPs) : a.Matches.CompareTo(b.Matches),
            "Fkdr" => a.Fkdr != b.Fkdr ? a.Fkdr.CompareTo(b.Fkdr) : a.FinalKills.CompareTo(b.FinalKills),
            "BedsBroken" => a.BedsBroken != b.BedsBroken ? a.BedsBroken.CompareTo(b.BedsBroken) : a.Matches.CompareTo(b.Matches),
            "FinalKills" => a.FinalKills != b.FinalKills ? a.FinalKills.CompareTo(b.FinalKills) : a.Matches.CompareTo(b.Matches),
            "Kills" => a.Kills != b.Kills ? a.Kills.CompareTo(b.Kills) : a.Matches.CompareTo(b.Matches),
            "Matches" => a.Matches != b.Matches ? a.Matches.CompareTo(b.Matches) : a.Wins.CompareTo(b.Wins),
            "Rating" => Nullable.Compare(a.OverallRating, b.OverallRating) != 0 ? Nullable.Compare(a.OverallRating, b.OverallRating) : a.Matches.CompareTo(b.Matches),
            "KD" => a.KD != b.KD ? a.KD.CompareTo(b.KD) : a.Kills.CompareTo(b.Kills),
            "Adr" => a.Adr != b.Adr ? a.Adr.CompareTo(b.Adr) : a.Matches.CompareTo(b.Matches),
            "HsRate" => a.HsRate != b.HsRate ? a.HsRate.CompareTo(b.HsRate) : a.Matches.CompareTo(b.Matches),
            "Kast" => a.Kast != b.Kast ? a.Kast.CompareTo(b.Kast) : a.Matches.CompareTo(b.Matches),
            "Plants" => a.Plants != b.Plants ? a.Plants.CompareTo(b.Plants) : a.Matches.CompareTo(b.Matches),
            "Defuses" => a.Defuses != b.Defuses ? a.Defuses.CompareTo(b.Defuses) : a.Matches.CompareTo(b.Matches),
            "MaxRating" => a.MaxRating != b.MaxRating ? a.MaxRating.CompareTo(b.MaxRating) : a.Matches.CompareTo(b.Matches),
            "名次" => Nullable.Compare(a.LeaderboardDisplayRank.Rank, b.LeaderboardDisplayRank.Rank),
            _ => 0
        };
        if (result != 0) return descending ? -result : result;
        return StringComparer.OrdinalIgnoreCase.Compare(a.Username, b.Username);
    }
    static int ThreatRank(PlayerRow player) => player.ThreatLabel switch
    {
        "极其危险" => 4,
        "危险" => 3,
        "普通" => 2,
        "低威胁" => 1,
        _ => 0
    };
}

public sealed class PlayerRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    string _state = "loading", _error = ""; public string Username { get; set; } = ""; public string GameMode { get; set; } = "bedwars";
    bool _isPinnedSelf;
    public bool IsPinnedSelf { get => _isPinnedSelf; set { _isPinnedSelf = value; Changed(nameof(IsPinnedSelf)); } }
    ImageSource? _avatar;
    public ImageSource? Avatar { get => _avatar; set { _avatar = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar))); } }
    public string Uuid { get; set; } = "";
    public long Matches { get; set; } public long Wins { get; set; } public long MVPs { get; set; } public long BedsBroken { get; set; } public long FinalKills { get; set; } public long Kills { get; set; }
    public double WinRate { get; set; } public double Fkdr { get; set; } public double AvgBeds { get; set; } public double KD { get; set; } public double Rating { get; set; } public double Adr { get; set; } public double HsRate { get; set; }
    public double Kast { get; set; } public long Plants { get; set; } public long Defuses { get; set; } public double MaxRating { get; set; }
    public long KbExperience { get; set; } public long KbChallengeWins { get; set; } public long KbBestStreak { get; set; }
    public int? KbKillsRank { get; set; } public string KbRankTitle { get; set; } = "";
    public double[] RatingTrend { get; set; } = [];
    public List<ApiRecentMatch> RecentMatches { get; set; } = [];
    public string State { get => _state; set { _state = value; Changed(); Changed(nameof(MatchesLabel)); } }
    public string Error { get => _error; set { _error = value; Changed(); Changed(nameof(MatchesLabel)); } }
    public double? OverallRating => State != "ok" || Matches <= 0 || GameMode == "kitbattle" ? null : GameMode == "counterember" ? Rating : BedwarsRatingCalculator.Calculate(Matches, Wins, BedsBroken, Fkdr, KD);
    public bool IsHighRating => OverallRating is double rating && double.IsFinite(rating) && rating >= 2.00;
    public bool IsWarningRating => OverallRating is double rating && double.IsFinite(rating) && rating >= 1.40 && rating < 2.00;
    public bool IsLowRating => OverallRating is double rating && double.IsFinite(rating) && rating < 0.85;
    public string ThreatLabel => OverallRating is not double rating || !double.IsFinite(rating) ? "—"
        : rating >= 2.00 ? "极其危险"
        : rating >= 1.40 ? "危险"
        : rating >= 0.85 ? "普通"
        : "低威胁";
    public string WinRateLabel => State == "ok" ? WinRate.ToString("P1") : "—"; public string FkdrLabel => State == "ok" ? Fkdr.ToString("F2") : "—";
    public string WinRateColor => State != "ok" ? "#64748B" : WinRate >= .60 ? "#34D399" : WinRate >= .40 ? "#38BDF8" : "#A8B6C9";
    public string FkdrColor => State != "ok" ? "#64748B" : Fkdr >= 4 ? "#FB7185" : Fkdr >= 2.5 ? "#FB923C" : Fkdr >= 1.5 ? "#FBBF24" : Fkdr >= .8 ? "#34D399" : "#A8B6C9";
    public string MVPColor => State == "ok" && MVPs > 0 ? "#FBBF24" : "#64748B";
    public string RatingLabel => OverallRating is double value && double.IsFinite(value) ? value.ToString("F2") + (Matches < 10 ? "*" : "") : "—";
    public string RatingInfo => GameMode == "bedwars"
        ? "Rating Pro：项目估算值，非 IMC.RE 官方评分；结合 FKDR、场均拆床、WLR、K/D 和样本量修正。* 表示少于 10 场。"
        : "IMC.RE 提供的 CounterEmber Rating。* 表示少于 10 场，样本较少。";
    public string KDLabel => State == "ok" ? KD.ToString("F2") : "—"; public string AdrLabel => State == "ok" ? Adr.ToString("F1") : "—"; public string HsRateLabel => State == "ok" ? HsRate.ToString("P1") : "—";
    public string KastLabel => State == "ok" ? Kast.ToString("P1") : "—";
    public string PlantsLabel => State == "ok" ? Plants.ToString() : "—";
    public string DefusesLabel => State == "ok" ? Defuses.ToString() : "—";
    public string MaxRatingLabel => State == "ok" && Matches > 0 ? MaxRating.ToString("F2") : "—";
    public string MVPsLabel => State == "ok" ? (MVPs > 0 ? $"★ {MVPs}" : "0") : "—"; public string BedsLabel => State == "ok" ? BedsBroken.ToString() : "—"; public string FinalsLabel => State == "ok" ? FinalKills.ToString() : "—"; public string KillsLabel => State == "ok" ? Kills.ToString() : "—";
    public string MatchesLabel => State == "ok" ? $"{Matches} 场" : State == "loading" ? "查询中…" : State == "unregistered" ? "无记录" : "查询失败";

    StatRankInfo _winRateRank = StatRankInfo.Empty;
    StatRankInfo _winsRank = StatRankInfo.Empty;
    StatRankInfo _mvpRank = StatRankInfo.Empty;
    StatRankInfo _ratingRank = StatRankInfo.Empty;
    StatRankInfo _fkdrRank = StatRankInfo.Empty;
    StatRankInfo _bedsRank = StatRankInfo.Empty;
    StatRankInfo _finalsRank = StatRankInfo.Empty;
    StatRankInfo _killsRank = StatRankInfo.Empty;
    StatRankInfo _matchesRank = StatRankInfo.Empty;
    StatRankInfo _kdRank = StatRankInfo.Empty;
    StatRankInfo _adrRank = StatRankInfo.Empty;
    StatRankInfo _hsRateRank = StatRankInfo.Empty;
    StatRankInfo _kastRank = StatRankInfo.Empty;
    StatRankInfo _plantsRank = StatRankInfo.Empty;
    StatRankInfo _defusesRank = StatRankInfo.Empty;
    StatRankInfo _maxRatingRank = StatRankInfo.Empty;
    StatRankInfo _leaderboardDisplayRank = StatRankInfo.Empty;

    public StatRankInfo WinRateRank { get => _winRateRank; set { _winRateRank = value; Changed(nameof(WinRateRank)); } }
    public StatRankInfo WinsRank { get => _winsRank; set { _winsRank = value; Changed(nameof(WinsRank)); } }
    public StatRankInfo MVPRank { get => _mvpRank; set { _mvpRank = value; Changed(nameof(MVPRank)); } }
    public StatRankInfo RatingRank { get => _ratingRank; set { _ratingRank = value; Changed(nameof(RatingRank)); } }
    public StatRankInfo FkdrRank { get => _fkdrRank; set { _fkdrRank = value; Changed(nameof(FkdrRank)); } }
    public StatRankInfo BedsRank { get => _bedsRank; set { _bedsRank = value; Changed(nameof(BedsRank)); } }
    public StatRankInfo FinalsRank { get => _finalsRank; set { _finalsRank = value; Changed(nameof(FinalsRank)); } }
    public StatRankInfo KillsRank { get => _killsRank; set { _killsRank = value; Changed(nameof(KillsRank)); } }
    public StatRankInfo MatchesRank { get => _matchesRank; set { _matchesRank = value; Changed(nameof(MatchesRank)); } }
    public StatRankInfo KDRank { get => _kdRank; set { _kdRank = value; Changed(nameof(KDRank)); } }
    public StatRankInfo AdrRank { get => _adrRank; set { _adrRank = value; Changed(nameof(AdrRank)); } }
    public StatRankInfo HsRateRank { get => _hsRateRank; set { _hsRateRank = value; Changed(nameof(HsRateRank)); } }
    public StatRankInfo KastRank { get => _kastRank; set { _kastRank = value; Changed(nameof(KastRank)); } }
    public StatRankInfo PlantsRank { get => _plantsRank; set { _plantsRank = value; Changed(nameof(PlantsRank)); } }
    public StatRankInfo DefusesRank { get => _defusesRank; set { _defusesRank = value; Changed(nameof(DefusesRank)); } }
    public StatRankInfo MaxRatingRank { get => _maxRatingRank; set { _maxRatingRank = value; Changed(nameof(MaxRatingRank)); } }
    public StatRankInfo LeaderboardDisplayRank { get => _leaderboardDisplayRank; set { _leaderboardDisplayRank = value; Changed(nameof(LeaderboardDisplayRank)); } }

    public void ApplyLeaderboard(LeaderboardData? lb)
    {
        if (State != "ok")
        {
            ResetRanks();
            return;
        }

        if (lb == null || lb.Players.Count == 0)
        {
            WinRateRank = StatRankInfo.None;
            WinsRank = StatRankInfo.None;
            MVPRank = StatRankInfo.None;
            RatingRank = StatRankInfo.None;
            FkdrRank = StatRankInfo.None;
            BedsRank = StatRankInfo.None;
            FinalsRank = StatRankInfo.None;
            KillsRank = StatRankInfo.None;
            MatchesRank = StatRankInfo.None;
            KDRank = StatRankInfo.None;
            AdrRank = StatRankInfo.None;
            HsRateRank = StatRankInfo.None;
            KastRank = StatRankInfo.None;
            PlantsRank = StatRankInfo.None;
            DefusesRank = StatRankInfo.None;
            MaxRatingRank = StatRankInfo.None;
            return;
        }

        WinRateRank = new StatRankInfo(WinRate > 0 ? lb.GetRankWinRate(WinRate) : null);
        WinsRank = new StatRankInfo(lb.GetRankWins(Wins));
        MVPRank = new StatRankInfo(lb.GetRankMVPs(MVPs));
        RatingRank = new StatRankInfo(lb.GetRankRating(OverallRating));
        FkdrRank = new StatRankInfo(lb.GetRankFkdr(Fkdr));
        BedsRank = new StatRankInfo(lb.GetRankBeds(BedsBroken));
        FinalsRank = new StatRankInfo(lb.GetRankFinals(FinalKills));
        KillsRank = new StatRankInfo(lb.GetRankKills(Kills));
        MatchesRank = new StatRankInfo(lb.GetRankMatches(Matches));
        KDRank = new StatRankInfo(lb.GetRankKD(KD));
        AdrRank = new StatRankInfo(lb.GetRankAdr(Adr));
        HsRateRank = new StatRankInfo(lb.GetRankHsRate(HsRate));
        KastRank = new StatRankInfo(lb.GetRankKast(Kast));
        PlantsRank = new StatRankInfo(lb.GetRankPlants(Plants));
        DefusesRank = new StatRankInfo(lb.GetRankDefuses(Defuses));
        MaxRatingRank = new StatRankInfo(lb.GetRankMaxRating(MaxRating));
    }

    public void ResetRanks()
    {
        WinRateRank = StatRankInfo.Empty;
        WinsRank = StatRankInfo.Empty;
        MVPRank = StatRankInfo.Empty;
        RatingRank = StatRankInfo.Empty;
        FkdrRank = StatRankInfo.Empty;
        BedsRank = StatRankInfo.Empty;
        FinalsRank = StatRankInfo.Empty;
        KillsRank = StatRankInfo.Empty;
        MatchesRank = StatRankInfo.Empty;
        KDRank = StatRankInfo.Empty;
        AdrRank = StatRankInfo.Empty;
        HsRateRank = StatRankInfo.Empty;
        KastRank = StatRankInfo.Empty;
        PlantsRank = StatRankInfo.Empty;
        DefusesRank = StatRankInfo.Empty;
        MaxRatingRank = StatRankInfo.Empty;
        LeaderboardDisplayRank = StatRankInfo.Empty;
    }

    public void CopyFrom(PlayerRow x)
    {
        Username = x.Username; Uuid = x.Uuid; GameMode = x.GameMode; Matches = x.Matches; Wins = x.Wins; MVPs = x.MVPs;
        BedsBroken = x.BedsBroken; FinalKills = x.FinalKills; Kills = x.Kills; WinRate = x.WinRate;
        Fkdr = x.Fkdr; AvgBeds = x.AvgBeds; KD = x.KD; Rating = x.Rating; Adr = x.Adr; HsRate = x.HsRate;
        Kast = x.Kast; Plants = x.Plants; Defuses = x.Defuses; MaxRating = x.MaxRating;
        KbExperience = x.KbExperience; KbChallengeWins = x.KbChallengeWins; KbBestStreak = x.KbBestStreak;
        KbKillsRank = x.KbKillsRank; KbRankTitle = x.KbRankTitle;
        RatingTrend = x.RatingTrend.ToArray(); RecentMatches = x.RecentMatches.ToList();
        Error = x.Error; State = x.State;
        WinRateRank = x.WinRateRank; WinsRank = x.WinsRank; MVPRank = x.MVPRank; RatingRank = x.RatingRank; FkdrRank = x.FkdrRank;
        BedsRank = x.BedsRank; FinalsRank = x.FinalsRank; KillsRank = x.KillsRank; MatchesRank = x.MatchesRank;
        KDRank = x.KDRank; AdrRank = x.AdrRank; HsRateRank = x.HsRateRank;
        KastRank = x.KastRank; PlantsRank = x.PlantsRank; DefusesRank = x.DefusesRank; MaxRatingRank = x.MaxRatingRank;
        IsPinnedSelf = x.IsPinnedSelf;
        Avatar = x.Avatar;
        LeaderboardDisplayRank = x.LeaderboardDisplayRank;
        Changed(string.Empty);
    }

    void Changed(string? name = null)
    {
        if (name == null) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var p in new[]
        {
            nameof(OverallRating), nameof(IsHighRating), nameof(IsWarningRating), nameof(IsLowRating), nameof(RatingLabel),
            nameof(RatingInfo), nameof(ThreatLabel), nameof(WinRateLabel), nameof(FkdrLabel),
            nameof(MatchesLabel), nameof(MVPsLabel), nameof(BedsLabel), nameof(FinalsLabel), nameof(KastLabel),
            nameof(PlantsLabel), nameof(DefusesLabel), nameof(MaxRatingLabel),
            nameof(KillsLabel), nameof(WinRateRank), nameof(WinsRank), nameof(MVPRank), nameof(RatingRank),
            nameof(FkdrRank), nameof(BedsRank), nameof(FinalsRank), nameof(KillsRank),
            nameof(MatchesRank), nameof(KDRank), nameof(AdrRank), nameof(HsRateRank), nameof(KastRank),
            nameof(PlantsRank), nameof(DefusesRank), nameof(MaxRatingRank), nameof(IsPinnedSelf),
            nameof(LeaderboardDisplayRank)
        })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}

public sealed class ApiResponse
{
    public ApiSummary? Summary { get; set; }
    public double[] Trend { get; set; } = [];
    public List<ApiRecentMatch> Recent { get; set; } = [];
}

public sealed class ApiSummary
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public long Matches { get; set; }
    public long Wins { get; set; }
    public long Mvps { get; set; }
    public double WinRate { get; set; }
    public ApiMetrics Metrics { get; set; } = new();
}

public sealed class ApiMetrics
{
    public double Fkdr { get; set; }
    public double Kd { get; set; }
    public double AvgBeds { get; set; }
    public double Rating { get; set; }
    public double Adr { get; set; }
    public double HsRate { get; set; }
    public long FinalKills { get; set; }
    public long BedsBroken { get; set; }
    public long Kills { get; set; }
    public double Kast { get; set; }
    public long Plants { get; set; }
    public long Defuses { get; set; }
    public double MaxRating { get; set; }
}

public sealed class ApiRecentMatch
{
    public string MatchId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Map { get; set; } = "";
    public string Team { get; set; } = "";
    public bool? Won { get; set; }
    public bool? Mvp { get; set; }
    public string Winner { get; set; } = "";
    public List<ApiTeamScore> Teams { get; set; } = [];
    public DateTimeOffset? EndedAt { get; set; }
    public ApiRecentStats Stats { get; set; } = new();
    public ApiRecentMetrics Metrics { get; set; } = new();
    public bool? Counted { get; set; }
}

public sealed class ApiTeamScore
{
    public string Id { get; set; } = "";
    public int Score { get; set; }
}

public sealed class ApiRecentStats
{
    public double? Kills { get; set; }
    public double? Damage { get; set; }
    public double? Deaths { get; set; }
    public double? Plants { get; set; }
    public double? Rating { get; set; }
    public double? Assists { get; set; }
    public double? Defuses { get; set; }
    public double? Headshots { get; set; }
    public double? KastRounds { get; set; }
    public double? MvpEligible { get; set; }
    public double? RoundsPlayed { get; set; }
    public double? RatingQualified { get; set; }
}

public sealed class ApiRecentMetrics
{
    public double? Adr { get; set; }
    public double? Defuses { get; set; }
    public double? HsRate { get; set; }
    public double? Kast { get; set; }
    public double? Kd { get; set; }
    public double? Kills { get; set; }
    public double? MaxRating { get; set; }
    public double? Plants { get; set; }
    public double? Rating { get; set; }
}
