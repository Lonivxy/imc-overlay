using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace IMCBedwarsOverlay;

internal static class RankVisuals
{
    public static readonly SolidColorBrush NoneBrush = CreateFrozenBrush(Color.FromRgb(100, 116, 139));   // #64748B
    public static readonly SolidColorBrush GoldBrush = CreateFrozenBrush(Color.FromRgb(250, 204, 21));    // #FACC15 (Gold)
    public static readonly SolidColorBrush SilverBrush = CreateFrozenBrush(Color.FromRgb(226, 232, 240)); // #E2E8F0 (Silver)
    public static readonly SolidColorBrush BronzeBrush = CreateFrozenBrush(Color.FromRgb(217, 119, 6));   // #D97706 (Bronze)
    public static readonly SolidColorBrush Top10Brush = CreateFrozenBrush(Color.FromRgb(244, 63, 94));    // #F43F5E (Red)
    public static readonly SolidColorBrush Top50Brush = CreateFrozenBrush(Color.FromRgb(251, 146, 60));   // #FB923C (Orange)
    public static readonly SolidColorBrush OtherBrush = CreateFrozenBrush(Color.FromRgb(148, 163, 184));  // #94A3B8 (Gray)

    public static readonly DropShadowEffect GoldGlow = CreateFrozenGlow(Color.FromRgb(250, 204, 21));
    public static readonly DropShadowEffect SilverGlow = CreateFrozenGlow(Color.FromRgb(255, 255, 255));
    public static readonly DropShadowEffect BronzeGlow = CreateFrozenGlow(Color.FromRgb(245, 158, 11));

    public static string GetRankColorHex(int? rank) => rank switch
    {
        1 => "#FACC15",
        2 => "#E2E8F0",
        3 => "#D97706",
        <= 10 => "#F43F5E",
        <= 50 => "#FB923C",
        null or <= 0 => "#64748B",
        _ => "#94A3B8"
    };

    static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static DropShadowEffect CreateFrozenGlow(Color color)
    {
        var effect = new DropShadowEffect
        {
            Color = color,
            BlurRadius = 8,
            ShadowDepth = 0,
            Opacity = 0.9
        };
        effect.Freeze();
        return effect;
    }
}

public sealed class StatRankInfo
{
    public static readonly StatRankInfo Empty = new(null, "");
    public static readonly StatRankInfo None = new(null, "#-");

    public int? Rank { get; }
    public string Tag { get; }
    public SolidColorBrush Brush { get; }
    public DropShadowEffect? Effect { get; }

    public StatRankInfo(int? rank, string defaultTag = "#-")
    {
        Rank = rank;
        if (rank is null or <= 0)
        {
            Tag = defaultTag;
            Brush = RankVisuals.NoneBrush;
            Effect = null;
        }
        else
        {
            Tag = $"#{rank}";
            if (rank == 1)
            {
                Brush = RankVisuals.GoldBrush;
                Effect = RankVisuals.GoldGlow;
            }
            else if (rank == 2)
            {
                Brush = RankVisuals.SilverBrush;
                Effect = RankVisuals.SilverGlow;
            }
            else if (rank == 3)
            {
                Brush = RankVisuals.BronzeBrush;
                Effect = RankVisuals.BronzeGlow;
            }
            else if (rank <= 10)
            {
                Brush = RankVisuals.Top10Brush;
                Effect = null;
            }
            else if (rank <= 50)
            {
                Brush = RankVisuals.Top50Brush;
                Effect = null;
            }
            else
            {
                Brush = RankVisuals.OtherBrush;
                Effect = null;
            }
        }
    }
}

public sealed class LeaderboardResponse
{
    [JsonPropertyName("players")]
    public List<LeaderboardPlayer> Players { get; set; } = [];
}

public sealed class LeaderboardPlayer
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("matches")]
    public long Matches { get; set; }

    [JsonPropertyName("wins")]
    public long Wins { get; set; }

    [JsonPropertyName("mvps")]
    public long Mvps { get; set; }

    [JsonPropertyName("winRate")]
    public double WinRate { get; set; }

    [JsonPropertyName("metrics")]
    public ApiMetrics Metrics { get; set; } = new();
}

public sealed class LeaderboardData
{
    public static readonly LeaderboardData Empty = new([], "bedwars", DateTime.MinValue);

    public string Mode { get; }
    public DateTime FetchedAt { get; }
    public List<LeaderboardPlayer> Players { get; }

    readonly double[] _winRates;
    readonly long[] _wins;
    readonly long[] _mvps;
    readonly double[] _fkdrs;
    readonly long[] _beds;
    readonly long[] _finals;
    readonly long[] _kills;
    readonly long[] _matches;
    readonly double[] _kds;
    readonly double[] _adrs;
    readonly double[] _hsRates;
    readonly double[] _kasts;
    readonly long[] _plants;
    readonly long[] _defuses;
    readonly double[] _maxRatings;
    readonly double[] _ratings;

    public LeaderboardData(List<LeaderboardPlayer> players, string mode, DateTime fetchedAt)
    {
        Mode = mode;
        FetchedAt = fetchedAt;
        Players = players ?? [];

        _winRates = Players.Select(p => p.WinRate).OrderByDescending(x => x).ToArray();
        _wins = Players.Select(p => p.Wins).OrderByDescending(x => x).ToArray();
        _mvps = Players.Select(p => p.Mvps).OrderByDescending(x => x).ToArray();
        _fkdrs = Players.Select(p => p.Metrics.Fkdr).OrderByDescending(x => x).ToArray();
        _beds = Players.Select(p => p.Metrics.BedsBroken).OrderByDescending(x => x).ToArray();
        _finals = Players.Select(p => p.Metrics.FinalKills).OrderByDescending(x => x).ToArray();
        _kills = Players.Select(p => p.Metrics.Kills).OrderByDescending(x => x).ToArray();
        _matches = Players.Select(p => p.Matches).OrderByDescending(x => x).ToArray();
        _kds = Players.Select(p => p.Metrics.Kd).OrderByDescending(x => x).ToArray();
        _adrs = Players.Select(p => p.Metrics.Adr).OrderByDescending(x => x).ToArray();
        _hsRates = Players.Select(p => p.Metrics.HsRate).OrderByDescending(x => x).ToArray();
        _kasts = Players.Select(p => p.Metrics.Kast).OrderByDescending(x => x).ToArray();
        _plants = Players.Select(p => p.Metrics.Plants).OrderByDescending(x => x).ToArray();
        _defuses = Players.Select(p => p.Metrics.Defuses).OrderByDescending(x => x).ToArray();
        _maxRatings = Players.Select(p => p.Metrics.MaxRating).OrderByDescending(x => x).ToArray();

        if (mode == "counterember")
        {
            _ratings = Players.Select(p => p.Metrics.Rating).OrderByDescending(x => x).ToArray();
        }
        else
        {
            _ratings = Players.Select(p => BedwarsRatingCalculator.Calculate(p.Matches, p.Wins, p.Metrics.BedsBroken, p.Metrics.Fkdr, p.Metrics.Kd) ?? 0.0)
                .OrderByDescending(x => x).ToArray();
        }
    }

    public int? GetRankWinRate(double val) => val <= 0 ? null : CalculateRank(_winRates, val);
    public int? GetRankWins(long val) => val <= 0 ? null : CalculateRank(_wins, val);
    public int? GetRankMVPs(long val) => val <= 0 ? null : CalculateRank(_mvps, val);
    public int? GetRankFkdr(double val) => val <= 0 ? null : CalculateRank(_fkdrs, val);
    public int? GetRankBeds(long val) => val <= 0 ? null : CalculateRank(_beds, val);
    public int? GetRankFinals(long val) => val <= 0 ? null : CalculateRank(_finals, val);
    public int? GetRankKills(long val) => val <= 0 ? null : CalculateRank(_kills, val);
    public int? GetRankMatches(long val) => val <= 0 ? null : CalculateRank(_matches, val);
    public int? GetRankKD(double val) => val <= 0 ? null : CalculateRank(_kds, val);
    public int? GetRankAdr(double val) => val <= 0 ? null : CalculateRank(_adrs, val);
    public int? GetRankHsRate(double val) => val <= 0 ? null : CalculateRank(_hsRates, val);
    public int? GetRankKast(double val) => val <= 0 ? null : CalculateRank(_kasts, val);
    public int? GetRankPlants(long val) => val <= 0 ? null : CalculateRank(_plants, val);
    public int? GetRankDefuses(long val) => val <= 0 ? null : CalculateRank(_defuses, val);
    public int? GetRankMaxRating(double val) => val <= 0 ? null : CalculateRank(_maxRatings, val);
    public int? GetRankRating(double? val) => val is null or <= 0 ? null : CalculateRank(_ratings, val.Value);

    static int? CalculateRank<T>(T[] sortedDesc, T value) where T : IComparable<T>
    {
        if (sortedDesc.Length == 0) return null;
        int count = 0;
        for (int i = 0; i < sortedDesc.Length; i++)
        {
            if (sortedDesc[i].CompareTo(value) > 0)
                count++;
            else
                break;
        }
        int rank = count + 1;
        if (rank > sortedDesc.Length) return null;
        return rank;
    }
}
