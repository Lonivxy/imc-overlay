using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IMCBedwarsOverlay;

internal static class StatsCardRenderer
{
    const int Width = 1200, Height = 720;
    static readonly Typeface Regular = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    static void Text(DrawingContext dc, string text, double x, double y, double size, string color, bool bold = false, double maxWidth = 1100)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, Brush(color), 1.0)
        { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, new Point(x, y));
    }

    public static BitmapSource Render(PlayerRow player, ImageSource background, DateTime date)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new ImageBrush(background) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            dc.DrawRectangle(bg, null, new Rect(0, 0, Width, Height));
            dc.DrawRectangle(Brush("#950B1220"), null, new Rect(0, 0, Width, Height));

            // Header Container
            dc.DrawRoundedRectangle(Brush("#C8172537"), new Pen(Brush("#80536C82"), 1), new Rect(32, 28, 1136, 145), 18, 18);

            // Avatar Container
            dc.DrawRoundedRectangle(Brush("#E51A2D41"), new Pen(Brush("#907F9AB0"), 1), new Rect(54, 47, 106, 106), 12, 12);
            if (player.Avatar != null) dc.DrawImage(player.Avatar, new Rect(62, 55, 90, 90));
            else Text(dc, "?", 91, 60, 50, "#BACBD9", true);

            // Mode Label
            Text(dc, "IMC.RE  /  " + (player.GameMode == "counterember" ? "COUNTEREMBER" : "BEDWARS"), 184, 46, 17, "#70D9C1", true);

            // Username
            var nameFormatted = new FormattedText(player.Username, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 36, Brush("#FFFFFF"), 1.0)
            { MaxTextWidth = 420, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(nameFormatted, new Point(182, 74));
            double nameWidth = nameFormatted.Width;

            // Rating next to username, large, with dynamic color
            double ratingX = 184 + nameWidth + 24;
            double? ratingVal = player.OverallRating;
            string ratingStr = ratingVal.HasValue ? ratingVal.Value.ToString("F2") : "—";
            string ratingColor = player.IsHighRating ? "#FF8790" : player.IsWarningRating ? "#FFD277" : "#71D0FF";

            Text(dc, "RATING", ratingX, 66, 12, "#94A3B8", true);

            var ratingFormatted = new FormattedText(ratingStr, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 46, Brush(ratingColor), 1.0)
            { MaxLineCount = 1 };
            dc.DrawText(ratingFormatted, new Point(ratingX, 80));
            double ratingNumWidth = ratingFormatted.Width;

            // Rating rank tag at bottom-right of Rating number
            var ratingRank = player.RatingRank;
            string ratingRankTag = string.IsNullOrEmpty(ratingRank.Tag) ? "#-" : ratingRank.Tag;
            string ratingRankColor = RankVisuals.GetRankColorHex(ratingRank.Rank);

            double rankBadgeX = ratingX + ratingNumWidth + 8;
            double rankBadgeY = 104;

            if (ratingRank.Rank is >= 1 and <= 3)
            {
                dc.DrawRoundedRectangle(Brush("#28" + ratingRankColor.TrimStart('#')), new Pen(Brush(ratingRankColor), 1.5), new Rect(rankBadgeX - 4, rankBadgeY - 2, 60, 24), 5, 5);
            }
            else if (ratingRank.Rank is <= 50)
            {
                dc.DrawRoundedRectangle(Brush("#20" + ratingRankColor.TrimStart('#')), new Pen(Brush("#50" + ratingRankColor.TrimStart('#')), 1), new Rect(rankBadgeX - 4, rankBadgeY - 2, 60, 24), 5, 5);
            }
            Text(dc, ratingRankTag, rankBadgeX, rankBadgeY, 17, ratingRankColor, true);

            // Subtitle
            string subNote = player.GameMode == "bedwars"
                ? ("估算 Rating · 全服天梯 " + ratingRankTag + (player.Matches < 10 ? "  ·  场次较少" : ""))
                : ("官方天梯 Rating · 全服排名 " + ratingRankTag);
            Text(dc, subNote, 184, 134, 13.5, "#94A3B8", false, 550);

            Text(dc, "PLAYER STATS", 920, 88, 20, "#B7C9D6", true, 220);

            // 8 Stat Boxes with Rank info underneath
            var stats = player.GameMode == "counterember"
                ? new (string Label, string Value, string Color, StatRankInfo Rank)[]
                {
                    ("胜率", player.WinRate.ToString("P1"), "#62E0AE", player.WinRateRank),
                    ("胜场", player.Wins.ToString(), "#E7F2FA", player.WinsRank),
                    ("K/D", player.KD.ToString("F2"), "#FF8C9C", player.KDRank),
                    ("MVP", player.MVPs.ToString(), "#FFD277", player.MVPRank),
                    ("ADR", player.Adr.ToString("F1"), "#71D0FF", player.AdrRank),
                    ("爆头率", player.HsRate.ToString("P1"), "#FFB97B", player.HsRateRank),
                    ("击杀", player.Kills.ToString(), "#E7F2FA", player.KillsRank),
                    ("比赛场次", player.Matches.ToString(), "#E7F2FA", player.MatchesRank)
                }
                : new (string Label, string Value, string Color, StatRankInfo Rank)[]
                {
                    ("胜率", player.WinRate.ToString("P1"), "#62E0AE", player.WinRateRank),
                    ("胜场", player.Wins.ToString(), "#E7F2FA", player.WinsRank),
                    ("FKDR", player.Fkdr.ToString("F2"), "#FF8C9C", player.FkdrRank),
                    ("MVP", player.MVPs.ToString(), "#FFD277", player.MVPRank),
                    ("拆床", player.BedsBroken.ToString(), "#71D0FF", player.BedsRank),
                    ("终杀", player.FinalKills.ToString(), "#FFB97B", player.FinalsRank),
                    ("击杀", player.Kills.ToString(), "#E7F2FA", player.KillsRank),
                    ("比赛场次", player.Matches.ToString(), "#E7F2FA", player.MatchesRank)
                };

            for (int i = 0; i < stats.Length; i++)
            {
                double x = 32 + (i % 4) * 287, y = 191 + (i / 4) * 185;
                dc.DrawRoundedRectangle(Brush("#D0142233"), new Pen(Brush("#70536C82"), 1), new Rect(x, y, 275, 171), 14, 14);

                // Label
                Text(dc, stats[i].Label, x + 22, y + 18, 19, "#A8BFCE", true, 230);

                // Value
                Text(dc, stats[i].Value, x + 20, y + 55, 38, stats[i].Color, true, 232);

                // Leaderboard rank info under the value
                var rank = stats[i].Rank;
                string rankTag = string.IsNullOrEmpty(rank.Tag) ? "#-" : rank.Tag;
                string rankColor = RankVisuals.GetRankColorHex(rank.Rank);

                double pillW = 122, pillH = 32;
                double pillX = x + 20, pillY = y + 116;
                dc.DrawRoundedRectangle(Brush("#A50C1623"), new Pen(Brush(rank.Rank is >= 1 and <= 3 ? rankColor : "#354A62"), 1), new Rect(pillX, pillY, pillW, pillH), 7, 7);
                Text(dc, "全服 " + rankTag, pillX + 12, pillY + 5, 15, rankColor, true, pillW - 20);
            }

            // Footer
            dc.DrawRoundedRectangle(Brush("#D0122030"), null, new Rect(32, 572, 1136, 100), 14, 14);
            Text(dc, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 55, 603, 21, "#CBD9E2", true);
            Text(dc, "IMC.RE  ·  玩家战绩快照  ·  全服天梯数据同步", 770, 603, 18, "#AFC4D0", false, 370);
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
