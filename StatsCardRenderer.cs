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
            // Background
            var bg = new ImageBrush(background) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            dc.DrawRectangle(bg, null, new Rect(0, 0, Width, Height));
            dc.DrawRectangle(Brush("#A508101C"), null, new Rect(0, 0, Width, Height));

            const double marginX = 32;
            const double contentW = 1136; // 1200 - 32 * 2
            const double gap = 16;

            // ================= HEADER CONTAINER =================
            const double headerY = 44;
            const double headerH = 142;
            dc.DrawRoundedRectangle(Brush("#D2142132"), new Pen(Brush("#505F7D96"), 1), new Rect(marginX, headerY, contentW, headerH), 16, 16);

            // Avatar Container (centered vertically inside header: (142 - 106)/2 = 18px offset)
            const double avatarBoxY = headerY + 18;
            dc.DrawRoundedRectangle(Brush("#E519283A"), new Pen(Brush("#806D8CA5"), 1.2), new Rect(marginX + 20, avatarBoxY, 106, 106), 14, 14);
            if (player.Avatar != null)
            {
                dc.DrawImage(player.Avatar, new Rect(marginX + 28, avatarBoxY + 8, 90, 90));
            }
            else
            {
                Text(dc, "?", marginX + 57, avatarBoxY + 22, 52, "#BACBD9", true);
            }

            // Mode Label
            string modeName = player.GameMode == "counterember" ? "COUNTEREMBER" : "BEDWARS";
            Text(dc, "IMC.RE  /  " + modeName, 180, headerY + 18, 16, "#5EEAD4", true);

            // Username
            var nameFormatted = new FormattedText(player.Username, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 36, Brush("#FFFFFF"), 1.0)
            { MaxTextWidth = 420, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(nameFormatted, new Point(180, headerY + 44));
            double nameWidth = nameFormatted.Width;

            // Rating next to username
            string ratingStr = player.RatingLabel;
            string ratingColor = player.IsHighRating ? "#FB7185" : player.IsWarningRating ? "#FBBF24" : "#38BDF8";

            double ratingX = 180 + nameWidth + 24;
            Text(dc, "RATING", ratingX, headerY + 36, 11, "#94A3B8", true);

            var ratingFormatted = new FormattedText(ratingStr, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 42, Brush(ratingColor), 1.0)
            { MaxLineCount = 1 };
            dc.DrawText(ratingFormatted, new Point(ratingX, headerY + 46));
            double ratingNumWidth = ratingFormatted.Width;

            // Rating rank tag right next to Rating number (NO BOX FRAME)
            var ratingRank = player.RatingRank;
            string ratingRankTag = string.IsNullOrEmpty(ratingRank.Tag) ? "#-" : ratingRank.Tag;
            string ratingRankColor = RankVisuals.GetRankColorHex(ratingRank.Rank);

            double rankTextX = ratingX + ratingNumWidth + 8;
            double rankTextY = headerY + 68;
            Text(dc, ratingRankTag, rankTextX, rankTextY, 20, ratingRankColor, true);

            // Subtitle under username / rating
            string subNote = player.GameMode == "bedwars"
                ? ("估算 Rating  ·  全服天梯 " + ratingRankTag + (player.Matches < 10 ? "  ·  场次较少" : ""))
                : ("官方天梯 Rating  ·  全服排名 " + ratingRankTag);
            Text(dc, subNote, 180, headerY + 104, 14, "#94A3B8", false, 580);

            // Right header label: PLAYER STATS (vertically centered)
            var statsHeaderFormatted = new FormattedText("PLAYER STATS", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 20, Brush("#94A3B8"), 1.0);
            dc.DrawText(statsHeaderFormatted, new Point(marginX + contentW - statsHeaderFormatted.Width - 36, headerY + (headerH - statsHeaderFormatted.Height) / 2));

            // ================= 8 STAT CARDS (2 ROWS x 4 COLS) =================
            var stats = player.GameMode == "counterember"
                ? new (string Label, string Value, string Color, StatRankInfo Rank)[]
                {
                    ("胜率", player.WinRate.ToString("P1"), "#34D399", player.WinRateRank),
                    ("胜场", player.Wins.ToString(), "#F1F5F9", player.WinsRank),
                    ("K/D", player.KD.ToString("F2"), "#FB7185", player.KDRank),
                    ("MVP", player.MVPs.ToString(), "#FBBF24", player.MVPRank),
                    ("KAST", player.Kast.ToString("P1"), "#A78BFA", player.KastRank),
                    ("ADR", player.Adr.ToString("F1"), "#38BDF8", player.AdrRank),
                    ("爆头率", player.HsRate.ToString("P1"), "#FB923C", player.HsRateRank),
                    ("击杀", player.Kills.ToString(), "#F1F5F9", player.KillsRank),
                    ("下包", player.Plants.ToString(), "#F472B6", player.PlantsRank),
                    ("拆包", player.Defuses.ToString(), "#60A5FA", player.DefusesRank),
                    ("最高 Rating", player.Matches > 0 ? player.MaxRating.ToString("F2") : "—", "#FACC15", player.MaxRatingRank),
                    ("比赛场次", player.Matches.ToString(), "#F1F5F9", player.MatchesRank)
                }
                : new (string Label, string Value, string Color, StatRankInfo Rank)[]
                {
                    ("胜率", player.WinRate.ToString("P1"), "#34D399", player.WinRateRank),
                    ("胜场", player.Wins.ToString(), "#F1F5F9", player.WinsRank),
                    ("FKDR", player.Fkdr.ToString("F2"), "#FB7185", player.FkdrRank),
                    ("MVP", player.MVPs.ToString(), "#FBBF24", player.MVPRank),
                    ("拆床", player.BedsBroken.ToString(), "#38BDF8", player.BedsRank),
                    ("终杀", player.FinalKills.ToString(), "#FB923C", player.FinalsRank),
                    ("击杀", player.Kills.ToString(), "#F1F5F9", player.KillsRank),
                    ("比赛场次", player.Matches.ToString(), "#F1F5F9", player.MatchesRank)
                };

            const double cardW = 272; // (1136 - 16 * 3) / 4 = 1088 / 4 = 272 exactly!
            double cardH = player.GameMode == "counterember" ? 115 : 170;
            double row1Y = headerY + headerH + gap;
            double row2Y = row1Y + cardH + gap;
            double row3Y = row2Y + cardH + gap;
            bool hasThirdRow = stats.Length > 8;
            double footerY = (hasThirdRow ? row3Y : row2Y) + cardH + gap;
            double footerH = hasThirdRow ? 66 : 92;

            for (int i = 0; i < stats.Length; i++)
            {
                int col = i % 4;
                int row = i / 4;
                double x = marginX + col * (cardW + gap);
                double y = row switch { 0 => row1Y, 1 => row2Y, _ => row3Y };

                // Card background and border
                dc.DrawRoundedRectangle(Brush("#D2142233"), new Pen(Brush("#5058728A"), 1), new Rect(x, y, cardW, cardH), 14, 14);

                // Label
                Text(dc, stats[i].Label, x + 24, y + 14, 17, "#94A3B8", true, cardW - 48);

                // Value (large, bold)
                var valFormatted = new FormattedText(stats[i].Value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, hasThirdRow ? 32 : 42, Brush(stats[i].Color), 1.0)
                { MaxTextWidth = cardW - 48, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(valFormatted, new Point(x + 24, y + 40));

                // Individual metric rank info (NO BOX FRAME - clean colored text directly under value)
                var rank = stats[i].Rank;
                string rankTag = string.IsNullOrEmpty(rank.Tag) ? "#-" : rank.Tag;
                string rankColor = RankVisuals.GetRankColorHex(rank.Rank);
                Text(dc, "全服 " + rankTag, x + 24, y + (hasThirdRow ? 82 : 112), hasThirdRow ? 13 : 16, rankColor, true, cardW - 48);
            }

            // ================= FOOTER CONTAINER =================
            dc.DrawRoundedRectangle(Brush("#CC122030"), new Pen(Brush("#404F6980"), 1), new Rect(marginX, footerY, contentW, footerH), 14, 14);

            Text(dc, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), marginX + 28, footerY + (hasThirdRow ? 22 : 34), 21, "#E2E8F0", true);

            var footerSubFormatted = new FormattedText("IMC.RE  ·  玩家战绩快照", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Regular, 17, Brush("#94A3B8"), 1.0);
            dc.DrawText(footerSubFormatted, new Point(marginX + contentW - footerSubFormatted.Width - 28, footerY + (hasThirdRow ? 24 : 36)));
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
