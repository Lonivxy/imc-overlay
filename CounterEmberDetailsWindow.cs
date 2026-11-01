using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IMCBedwarsOverlay;

internal sealed class CounterEmberDetailsWindow : Window
{
    static readonly Brush BackgroundBrush = Brush("#111722");
    static readonly Brush PanelBrush = Brush("#171F2C");
    static readonly Brush CardBorderBrush = Brush("#2B394C");
    static readonly Brush TextBrush = Brush("#E8EEF7");
    static readonly Brush MutedBrush = Brush("#95A4B8");
    static readonly Brush AccentBrush = Brush("#2DD4BF");

    public CounterEmberDetailsWindow(PlayerRow player)
    {
        Title = $"{player.Username} · CounterEmber 近期数据";
        Width = 1040;
        Height = 780;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = BackgroundBrush;
        Topmost = true;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var close = new Button
        {
            Content = "关闭",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = $"{player.Username} · CounterEmber",
            Foreground = TextBrush,
            FontSize = 21,
            FontWeight = FontWeights.Bold
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"Rating {player.RatingLabel}  ·  最高 {(player.Matches > 0 ? player.MaxRating.ToString("F2") : "—")}  ·  {player.Matches} 场",
            Foreground = MutedBrush,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0)
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"UUID：{(string.IsNullOrWhiteSpace(player.Uuid) ? "接口未提供" : player.Uuid)}",
            Foreground = MutedBrush,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Children.Add(titles);
        root.Children.Add(header);

        var summary = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        Grid.SetRow(summary, 1);
        AddMetric(summary, "胜率", player.WinRate.ToString("P1"));
        AddMetric(summary, "胜场 / 场次", $"{player.Wins} / {player.Matches}");
        AddMetric(summary, "K/D", player.KD.ToString("F2"));
        AddMetric(summary, "KAST", player.Kast.ToString("P1"));
        AddMetric(summary, "ADR", player.Adr.ToString("F1"));
        AddMetric(summary, "爆头率", player.HsRate.ToString("P1"));
        AddMetric(summary, "MVP", player.MVPs.ToString());
        AddMetric(summary, "击杀", player.Kills.ToString());
        AddMetric(summary, "下包 / 拆包", $"{player.Plants} / {player.Defuses}");
        root.Children.Add(summary);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var trendBorder = new Border
        {
            Background = PanelBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14)
        };
        var trendPanel = new Grid();
        trendPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        trendPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var trendHeader = new DockPanel();
        trendHeader.Children.Add(new TextBlock
        {
            Text = "近 30 场 Rating（左旧右新）",
            Foreground = TextBrush,
            FontWeight = FontWeights.SemiBold
        });
        var trendNote = new TextBlock
        {
            Text = player.RatingTrend.Length == 0 ? "暂无趋势" : $"{player.RatingTrend.Length} 个数据点",
            Foreground = MutedBrush,
            FontSize = 11
        };
        DockPanel.SetDock(trendNote, Dock.Right);
        trendHeader.Children.Add(trendNote);
        trendPanel.Children.Add(trendHeader);

        var chart = new Canvas { ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(chart, 1);
        trendPanel.Children.Add(chart);
        trendBorder.Child = trendPanel;
        body.Children.Add(trendBorder);
        Loaded += (_, _) => DrawTrend(chart, player.RatingTrend);
        SizeChanged += (_, _) => DrawTrend(chart, player.RatingTrend);

        var recentHeader = new DockPanel { Margin = new Thickness(2, 15, 2, 9) };
        Grid.SetRow(recentHeader, 1);
        recentHeader.Children.Add(new TextBlock
        {
            Text = "最近比赛",
            Foreground = TextBrush,
            FontSize = 16,
            FontWeight = FontWeights.Bold
        });
        var recentCount = new TextBlock
        {
            Text = $"显示 {player.RecentMatches.Count} 场",
            Foreground = MutedBrush,
            FontSize = 11
        };
        DockPanel.SetDock(recentCount, Dock.Right);
        recentHeader.Children.Add(recentCount);
        body.Children.Add(recentHeader);

        var recentPanel = new StackPanel();
        foreach (var match in player.RecentMatches.OrderByDescending(m => m.EndedAt))
            recentPanel.Children.Add(BuildMatchCard(match));
        if (player.RecentMatches.Count == 0)
        {
            recentPanel.Children.Add(new TextBlock
            {
                Text = "接口没有返回近期比赛记录。",
                Foreground = MutedBrush,
                Margin = new Thickness(4, 12, 4, 0)
            });
        }
        var recentScroll = new ScrollViewer
        {
            Content = recentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(recentScroll, 2);
        body.Children.Add(recentScroll);
    }

    static void AddMetric(Panel parent, string label, string value)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush, FontSize = 10 });
        content.Children.Add(new TextBlock { Text = value, Foreground = TextBrush, FontSize = 14, FontWeight = FontWeights.SemiBold });
        parent.Children.Add(new Border
        {
            Child = content,
            Background = PanelBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 7, 7)
        });
    }

    static Border BuildMatchCard(ApiRecentMatch match)
    {
        string date = match.EndedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "时间未知";
        var teams = match.Teams ?? [];
        string score = teams.Count == 0 ? "比分未知" : string.Join("   ", teams.Select(t => $"{t.Id} {t.Score}"));
        string result = match.Won switch { true => "胜", false => "负", _ => "结果未知" };
        string counted = match.Counted switch { true => "计入总数据", false => "未计入总数据", _ => "计入状态未知" };
        var card = new Border
        {
            Background = PanelBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var content = new StackPanel();
        card.Child = content;

        var header = new DockPanel();
        var status = new TextBlock
        {
            Text = $"{result}{(match.Mvp == true ? " · MVP" : "")} · {counted}",
            Foreground = match.Won == true ? AccentBrush : match.Won == false ? Brush("#FB7185") : MutedBrush,
            FontWeight = FontWeights.SemiBold
        };
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        header.Children.Add(new TextBlock
        {
            Text = $"{date}  ·  {match.Map}  ·  {ModeLabel(match.Mode)}  ·  {match.Team}",
            Foreground = TextBrush,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = $"比赛 ID：{Display(match.MatchId)}    比分：{score}    获胜方：{Display(match.Winner)}",
            Foreground = MutedBrush,
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0)
        });

        var stats = match.Stats;
        var metrics = match.Metrics;
        string kastRounds = stats.KastRounds.HasValue || stats.RoundsPlayed.HasValue
            ? $"{Display(stats.KastRounds)} / {Display(stats.RoundsPlayed)} 回合"
            : "—";
        content.Children.Add(new TextBlock
        {
            Text = $"metrics：Rating {Display(metrics.Rating, "F2")}  ·  最高 {Display(metrics.MaxRating, "F2")}  ·  K/D {Display(metrics.Kd, "F2")}  ·  ADR {Display(metrics.Adr, "F1")}  ·  KAST {Display(metrics.Kast, "P1")}  ·  爆头率 {Display(metrics.HsRate, "P1")}",
            Foreground = TextBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"stats：Rating {Display(stats.Rating, "F2")}  ·  KAST 回合 {kastRounds}  ·  击杀 {Display(stats.Kills)}  ·  死亡 {Display(stats.Deaths)}  ·  助攻 {Display(stats.Assists)}  ·  爆头 {Display(stats.Headshots)}  ·  伤害 {Display(stats.Damage)}  ·  下包 {Display(stats.Plants)}  ·  拆包 {Display(stats.Defuses)}",
            Foreground = MutedBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"metrics 计数：击杀 {Display(metrics.Kills)}  ·  下包 {Display(metrics.Plants)}  ·  拆包 {Display(metrics.Defuses)}",
            Foreground = MutedBrush,
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"比赛回合 {Display(stats.RoundsPlayed)}  ·  MVP 资格 {Display(stats.MvpEligible)}  ·  Rating 合格回合 {Display(stats.RatingQualified)}",
            Foreground = MutedBrush,
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0)
        });
        return card;
    }

    static void DrawTrend(Canvas canvas, IReadOnlyList<double> values)
    {
        canvas.Children.Clear();
        double width = Math.Max(10, canvas.ActualWidth);
        double height = Math.Max(10, canvas.ActualHeight);
        if (values.Count == 0)
        {
            canvas.Children.Add(new TextBlock { Text = "暂无趋势数据", Foreground = MutedBrush, Margin = new Thickness(4, 12, 0, 0) });
            return;
        }

        double min = values.Min();
        double max = values.Max();
        double spread = Math.Max(0.01, max - min);
        var points = new PointCollection();
        for (int i = 0; i < values.Count; i++)
        {
            double x = values.Count == 1 ? width / 2 : i * width / (values.Count - 1);
            double y = height - ((values[i] - min) / spread * (height - 14)) - 7;
            points.Add(new Point(x, y));
        }

        canvas.Children.Add(new Polyline { Points = points, Stroke = AccentBrush, StrokeThickness = 2.2, SnapsToDevicePixels = true });
        var maxLabel = new TextBlock { Text = $"{max:F2}", Foreground = MutedBrush, FontSize = 10 };
        Canvas.SetRight(maxLabel, 2); Canvas.SetTop(maxLabel, 0); canvas.Children.Add(maxLabel);
        var minLabel = new TextBlock { Text = $"{min:F2}", Foreground = MutedBrush, FontSize = 10 };
        Canvas.SetRight(minLabel, 2); Canvas.SetBottom(minLabel, 0); canvas.Children.Add(minLabel);
    }

    static string ModeLabel(string mode) => mode switch
    {
        "COUNTEREMBER_BOMB_DEFUSAL" => "爆破拆弹",
        "COUNTEREMBER_BOMB_PLANTING" => "爆破安放",
        _ => string.IsNullOrWhiteSpace(mode) ? "模式未知" : mode
    };

    static string Display(double? value, string format = "0") => value?.ToString(format, CultureInfo.CurrentCulture) ?? "—";
    static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;
    static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}
