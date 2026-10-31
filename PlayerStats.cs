using System.Collections;
using System.ComponentModel;
using System.Windows.Media;

namespace IMCBedwarsOverlay;

sealed class PlayerComparer(string key, bool descending) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not PlayerRow a || y is not PlayerRow b) return 0;
        if (a.State == "ok" && b.State != "ok") return -1;
        if (a.State != "ok" && b.State == "ok") return 1;
        if (key == "Rating" && a.OverallRating.HasValue != b.OverallRating.HasValue) return a.OverallRating.HasValue ? -1 : 1;
        int result = key switch
        {
            "Username" => StringComparer.OrdinalIgnoreCase.Compare(a.Username, b.Username),
            "threat" => ThreatRank(a).CompareTo(ThreatRank(b)),
            "WinRate" => a.WinRate.CompareTo(b.WinRate),
            "MVPs" => a.MVPs.CompareTo(b.MVPs),
            "Fkdr" => a.Fkdr.CompareTo(b.Fkdr),
            "BedsBroken" => a.BedsBroken.CompareTo(b.BedsBroken),
            "FinalKills" => a.FinalKills.CompareTo(b.FinalKills),
            "Kills" => a.Kills.CompareTo(b.Kills),
            "Matches" => a.Matches.CompareTo(b.Matches),
            "Rating" => Nullable.Compare(a.OverallRating, b.OverallRating),
            "KD" => a.KD.CompareTo(b.KD),
            "Adr" => a.Adr.CompareTo(b.Adr),
            "HsRate" => a.HsRate.CompareTo(b.HsRate),
            _ => 0
        };
        if (result != 0) return descending ? -result : result;
        return StringComparer.OrdinalIgnoreCase.Compare(a.Username, b.Username);
    }
    static int ThreatRank(PlayerRow player) => player.ThreatLabel switch { "极危" => 2, "危险" => 1, _ => 0 };
}

public sealed class PlayerRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    string _state = "loading", _error = ""; public string Username { get; set; } = ""; public string GameMode { get; set; } = "bedwars";
    ImageSource? _avatar;
    public ImageSource? Avatar { get => _avatar; set { _avatar = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar))); } }
    public long Matches { get; set; } public long Wins { get; set; } public long MVPs { get; set; } public long BedsBroken { get; set; } public long FinalKills { get; set; } public long Kills { get; set; }
    public double WinRate { get; set; } public double Fkdr { get; set; } public double AvgBeds { get; set; } public double KD { get; set; } public double Rating { get; set; } public double Adr { get; set; } public double HsRate { get; set; }
    public string State { get => _state; set { _state = value; Changed(); Changed(nameof(MatchesLabel)); } }
    public string Error { get => _error; set { _error = value; Changed(); Changed(nameof(MatchesLabel)); } }
    public double? OverallRating => State != "ok" ? null : GameMode == "counterember" ? Rating : BedwarsRatingCalculator.Calculate(Matches, WinRate, Fkdr, AvgBeds);
    double? DisplayedRating => OverallRating is double rating && double.IsFinite(rating) ? Math.Round(rating, 2, MidpointRounding.ToEven) : null;
    public bool IsHighRating => DisplayedRating >= 1.35;
    public bool IsWarningRating => DisplayedRating >= 1.12 && DisplayedRating < 1.35;
    public string ThreatLabel => DisplayedRating is null ? "—" : IsHighRating ? "极危" : IsWarningRating ? "危险" : "普通";
    public string WinRateLabel => State == "ok" ? WinRate.ToString("P1") : "—"; public string FkdrLabel => State == "ok" ? Fkdr.ToString("F2") : "—";
    public string WinRateColor => State != "ok" ? "#64748B" : WinRate >= .60 ? "#34D399" : WinRate >= .40 ? "#38BDF8" : "#A8B6C9";
    public string FkdrColor => State != "ok" ? "#64748B" : Fkdr >= 4 ? "#FB7185" : Fkdr >= 2.5 ? "#FB923C" : Fkdr >= 1.5 ? "#FBBF24" : Fkdr >= .8 ? "#34D399" : "#A8B6C9";
    public string MVPColor => State == "ok" && MVPs > 0 ? "#FBBF24" : "#64748B";
    public string RatingLabel => OverallRating is double value ? value.ToString("F2") + (GameMode == "bedwars" && Matches < 10 ? "*" : "") : "—";
    public string RatingInfo => GameMode == "bedwars" ? "项目估算值，非官方 Rating；场次越少越接近 1.00。* 表示少于 10 场。" : "IMC.RE 提供的 CounterEmber Rating。";
    public string KDLabel => State == "ok" ? KD.ToString("F2") : "—"; public string AdrLabel => State == "ok" ? Adr.ToString("F1") : "—"; public string HsRateLabel => State == "ok" ? HsRate.ToString("P1") : "—";
    public string MVPsLabel => State == "ok" ? (MVPs > 0 ? $"★ {MVPs}" : "0") : "—"; public string BedsLabel => State == "ok" ? BedsBroken.ToString() : "—"; public string FinalsLabel => State == "ok" ? FinalKills.ToString() : "—"; public string KillsLabel => State == "ok" ? Kills.ToString() : "—";
    public string MatchesLabel => State == "ok" ? $"{Matches} 场" : State == "loading" ? "查询中…" : State == "unregistered" ? "无记录" : "查询失败";
    public void CopyFrom(PlayerRow x) { Username=x.Username; GameMode=x.GameMode; Matches=x.Matches; Wins=x.Wins; MVPs=x.MVPs; BedsBroken=x.BedsBroken; FinalKills=x.FinalKills; Kills=x.Kills; WinRate=x.WinRate; Fkdr=x.Fkdr; AvgBeds=x.AvgBeds; KD=x.KD; Rating=x.Rating; Adr=x.Adr; HsRate=x.HsRate; Error=x.Error; State=x.State; Changed(string.Empty); }
    void Changed(string? name = null) { if (name == null) return; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); foreach (var p in new[] { nameof(OverallRating), nameof(IsHighRating), nameof(IsWarningRating), nameof(RatingLabel), nameof(RatingInfo), nameof(ThreatLabel), nameof(WinRateLabel), nameof(FkdrLabel), nameof(MatchesLabel), nameof(MVPsLabel), nameof(BedsLabel), nameof(FinalsLabel), nameof(KillsLabel) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p)); }
}

public sealed class ApiResponse { public ApiSummary? Summary { get; set; } }
public sealed class ApiSummary { public string Name { get; set; } = ""; public long Matches { get; set; } public long Wins { get; set; } public long Mvps { get; set; } public double WinRate { get; set; } public ApiMetrics Metrics { get; set; } = new(); }
public sealed class ApiMetrics { public double Fkdr { get; set; } public double Kd { get; set; } public double AvgBeds { get; set; } public double Rating { get; set; } public double Adr { get; set; } public double HsRate { get; set; } public long FinalKills { get; set; } public long BedsBroken { get; set; } public long Kills { get; set; } }
