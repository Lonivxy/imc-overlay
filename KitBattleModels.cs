using System.Text.Json.Serialization;

namespace IMCBedwarsOverlay;

internal sealed record KitBattleLeaderboardFetchResult(KitBattleLeaderboardResponse Data, bool IsFresh);

internal sealed class KitBattleProfileResponse
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("kd")] public double? Kd { get; set; }
    [JsonPropertyName("rank")] public KitBattleRank Rank { get; set; } = new();
    [JsonPropertyName("totals")] public KitBattleTotals Totals { get; set; } = new();
    [JsonPropertyName("positions")] public KitBattlePositions Positions { get; set; } = new();
    [JsonPropertyName("tracked")] public KitBattleTracked Tracked { get; set; } = new();
}

internal sealed class KitBattleRank
{
    [JsonPropertyName("rank")] public string Title { get; set; } = "";
}

internal sealed class KitBattleTotals
{
    [JsonPropertyName("kills")] public long Kills { get; set; }
    [JsonPropertyName("deaths")] public long Deaths { get; set; }
    [JsonPropertyName("exp")] public long Experience { get; set; }
    [JsonPropertyName("challengeWins")] public long ChallengeWins { get; set; }
}

internal sealed class KitBattleTracked
{
    [JsonPropertyName("bestStreak")] public long BestStreak { get; set; }
}

internal sealed class KitBattlePositions
{
    [JsonPropertyName("kills")] public KitBattlePosition? Kills { get; set; }
    [JsonPropertyName("kd")] public KitBattlePosition? Kd { get; set; }
    [JsonPropertyName("exp")] public KitBattlePosition? Experience { get; set; }
}

internal sealed class KitBattlePosition
{
    [JsonPropertyName("position")] public int? Position { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
}

internal sealed class KitBattleLeaderboardResponse
{
    [JsonPropertyName("metric")] public string Metric { get; set; } = "kills";
    [JsonPropertyName("page")] public int Page { get; set; } = 1;
    [JsonPropertyName("pageSize")] public int PageSize { get; set; } = 50;
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("players")] public List<KitBattleLeaderboardEntry> Players { get; set; } = [];
}

internal sealed class KitBattleLeaderboardEntry
{
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("kills")] public long Kills { get; set; }
    [JsonPropertyName("deaths")] public long Deaths { get; set; }
    [JsonPropertyName("exp")] public long Experience { get; set; }
    [JsonPropertyName("rank")] public string RankTitle { get; set; } = "";
}

internal sealed class KitBattleLeaderboardRow(KitBattleLeaderboardEntry entry, string metric)
{
    public int Position => entry.Position;
    public string Name => entry.Name;
    public string RankTitle => entry.RankTitle;
    public long Kills => entry.Kills;
    public long Deaths => entry.Deaths;
    public double KD => entry.Deaths > 0 ? (double)entry.Kills / entry.Deaths : entry.Kills;
    public long Experience => entry.Experience;
    public string ValueLabel => metric switch
    {
        "kd" => entry.Value.ToString("F2"),
        "exp" => entry.Value.ToString("N0"),
        _ => entry.Value.ToString("N0")
    };
}
