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
            dc.DrawRoundedRectangle(Brush("#C4172537"), new Pen(Brush("#80536C82"), 1), new Rect(32, 28, 1136, 145), 18, 18);
            dc.DrawRoundedRectangle(Brush("#E51A2D41"), new Pen(Brush("#907F9AB0"), 1), new Rect(54, 47, 106, 106), 12, 12);
            if (player.Avatar != null) dc.DrawImage(player.Avatar, new Rect(62, 55, 90, 90));
            else Text(dc, "?", 91, 60, 50, "#BACBD9", true);
            Text(dc, "IMC.RE  /  " + (player.GameMode == "counterember" ? "COUNTEREMBER" : "BEDWARS"), 184, 47, 19, "#70D9C1", true);
            Text(dc, player.Username, 182, 76, 39, "#FFFFFF", true, 700);
            if (player.GameMode == "bedwars") Text(dc, "估算 Rating " + player.RatingLabel + (player.Matches < 10 ? "  ·  场次较少" : ""), 184, 131, 15, player.IsHighRating ? "#FF8790" : "#B7C9D6", false, 600);
            Text(dc, "PLAYER STATS", 906, 91, 20, "#B7C9D6", true, 230);

            var stats = player.GameMode == "counterember"
                ? new (string Label, string Value, string Color)[] { ("胜率", player.WinRate.ToString("P1"), "#62E0AE"), ("胜场", player.Wins.ToString(), "#E7F2FA"), ("Rating", player.Rating.ToString("F2"), player.IsHighRating ? "#FF8790" : "#E7F2FA"), ("K/D", player.KD.ToString("F2"), "#FF8C9C"), ("ADR", player.Adr.ToString("F1"), "#71D0FF"), ("爆头率", player.HsRate.ToString("P1"), "#FFB97B"), ("击杀", player.Kills.ToString(), "#E7F2FA"), ("比赛场次", player.Matches.ToString(), "#E7F2FA") }
                : new (string Label, string Value, string Color)[] { ("胜率", player.WinRate.ToString("P1"), "#62E0AE"), ("胜场", player.Wins.ToString(), "#E7F2FA"), ("FKDR", player.Fkdr.ToString("F2"), "#FF8C9C"), ("MVP", player.MVPs.ToString(), "#FFD277"), ("拆床", player.BedsBroken.ToString(), "#71D0FF"), ("终杀", player.FinalKills.ToString(), "#FFB97B"), ("击杀", player.Kills.ToString(), "#E7F2FA"), ("比赛场次", player.Matches.ToString(), "#E7F2FA") };
            for (int i = 0; i < stats.Length; i++)
            {
                double x = 32 + (i % 4) * 287, y = 191 + (i / 4) * 185;
                dc.DrawRoundedRectangle(Brush("#D0142233"), new Pen(Brush("#70536C82"), 1), new Rect(x, y, 275, 171), 14, 14);
                Text(dc, stats[i].Label, x + 22, y + 23, 21, "#A8BFCE", true, 230);
                Text(dc, stats[i].Value, x + 20, y + 70, 42, stats[i].Color, true, 232);
            }
            dc.DrawRoundedRectangle(Brush("#D0122030"), null, new Rect(32, 572, 1136, 100), 14, 14);
            Text(dc, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 55, 603, 21, "#CBD9E2", true);
            Text(dc, "IMC.RE  ·  玩家战绩快照", 865, 603, 19, "#AFC4D0", false, 275);
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
