using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IMCBedwarsOverlay;

public partial class MainWindow : Window
{
    const int VK_SPACE = 0x20, VK_TAB = 0x09, VK_BACKSLASH = 0xDC;
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
    const int WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly HttpClient WallpaperHttp = new() { Timeout = TimeSpan.FromSeconds(45) };
    static readonly SemaphoreSlim ApiSlots = new(6, 6);
    static readonly SemaphoreSlim StartGate = new(1, 1);
    static long LastStartTicks;
    string _sortKey = "threat"; bool _sortDescending = true;
    string _mode = "bedwars";
    int _searchVersion;
    int _cardVersion;
    BitmapSource? _cardImage;
    BitmapSource? _officialWallpaper;
    string _cardName = "";
    double _resizeWidth;
    string _statusBeforeResize = "";
    static readonly ConcurrentDictionary<string, (PlayerRow Row, DateTime Time)> StatsCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, ImageSource> AvatarCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim AvatarSlots = new(4, 4);
    static readonly Regex UserRegex = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    static readonly Regex ColorRegex = new(@"(?i)(?:§|\\u00a7|&)[0-9a-z]", RegexOptions.Compiled);
    static readonly Regex UserListRegex = new(@"[A-Za-z0-9_]{3,16}");
    readonly ObservableCollection<PlayerRow> Players = [];
    readonly Dictionary<string, PlayerRow> ByName = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer KeyTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    readonly DispatcherTimer LogTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Dictionary<string, long> LogOffsets = new(StringComparer.OrdinalIgnoreCase);
    List<string> _logPaths = [];
    DateTime _lastLogDiscovery;
    readonly string ConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IMCBedwarsOverlay", "config.json");
    HwndSource? Source; PlayerRow? LastSearchResult; bool _hidden, _forceImc = true, _connectedImc, _hotkeyRegistered, _wasHotkey, _spaceArmed, _wasTab, _wasSpace;
    DateTime _spaceAt; const double AspectRatio = 980d / 552d;

    public MainWindow()
    {
        InitializeComponent(); PlayersGrid.ItemsSource = Players;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(_sortKey, _sortDescending);
        PlayersGrid.Columns.First(c => c.SortMemberPath == _sortKey).SortDirection = ListSortDirection.Descending;
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("IMC-BedWars-Overlay/2.0");
        LoadConfig();
        ApplyMode(_mode, false);
        KeyTimer.Tick += PollKeys; KeyTimer.Start();
        LogTimer.Tick += PollLogs; LogTimer.Start();
        Players.CollectionChanged += (_, _) => UpdateSummary();
    }

    void LoadConfig()
    {
        try { if (File.Exists(ConfigFile)) { using var d = JsonDocument.Parse(File.ReadAllText(ConfigFile)); if (d.RootElement.TryGetProperty("forceImc", out var f)) _forceImc = f.GetBoolean(); if (d.RootElement.TryGetProperty("opacity", out var o) && o.TryGetDouble(out var value)) OpacitySlider.Value = Math.Clamp(value, 0.5, 1.0); if (d.RootElement.TryGetProperty("gameMode", out var m) && m.GetString() == "counterember") _mode = "counterember"; } } catch { }
        ForceImcBox.IsChecked = _forceImc;
        ModeCombo.SelectedIndex = _mode == "counterember" ? 1 : 0;
        Opacity = OpacitySlider.Value;
    }
    void SaveConfig()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!); File.WriteAllText(ConfigFile, JsonSerializer.Serialize(new { forceImc = _forceImc, toggleKey = VK_BACKSLASH, opacity = OpacitySlider.Value, gameMode = _mode })); } catch { }
    }
    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle; Source = HwndSource.FromHwnd(hwnd); Source.AddHook(WndProc);
        _hotkeyRegistered = RegisterHotKey(hwnd, 1, 0, VK_BACKSLASH); SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WM_HOTKEY && wp.ToInt32() == 1) { ToggleVisibility(); handled = true; }
        return IntPtr.Zero;
    }
    void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeWidth = ActualWidth;
        _statusBeforeResize = FooterStatus.Text;
        FooterStatus.Text = "拖动调整大小，松开后应用";
    }
    void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _resizeWidth = Math.Clamp(_resizeWidth + e.HorizontalChange * ActualWidth / 980.0, MinWidth, 1600);
        FooterStatus.Text = $"目标宽度 {Math.Round(_resizeWidth)} · 松开后应用";
    }
    void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        FooterStatus.Text = _statusBeforeResize;
        if (e.Canceled) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(_resizeWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(_resizeWidth / AspectRatio * dpi.DpiScaleY);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004 | 0x0010);
    }
    void PlayersGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;
        _sortDescending = _sortKey == key ? !_sortDescending : key != "Username";
        _sortKey = key;
        foreach (var column in PlayersGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(key, _sortDescending);
        e.Handled = true;
    }
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveConfig(); KeyTimer.Stop(); LogTimer.Stop(); var hwnd = new WindowInteropHelper(this).Handle; UnregisterHotKey(hwnd, 1); Source?.RemoveHook(WndProc);
    }
    void ToggleVisibility()
    {
        _hidden = !_hidden; var hwnd = new WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, _hidden ? 0 : 4);
        if (!_hidden) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    void PollKeys(object? sender, EventArgs e)
    {
        bool space = GetAsyncKeyState(VK_SPACE) < 0, tab = GetAsyncKeyState(VK_TAB) < 0;
        if (!_hotkeyRegistered) { bool hotkey = GetAsyncKeyState(VK_BACKSLASH) < 0; if (hotkey && !_wasHotkey) ToggleVisibility(); _wasHotkey = hotkey; }
        if (space && !_wasSpace) _spaceAt = DateTime.UtcNow;
        if (tab && !_wasTab && DateTime.UtcNow - _spaceAt < TimeSpan.FromSeconds(4)) { _spaceArmed = true; FooterStatus.Text = "检测到扫描操作，正在读取大厅玩家…"; }
        _wasSpace = space; _wasTab = tab;
    }
    void PollLogs(object? sender, EventArgs e)
    {
        try
        {
            if (DateTime.UtcNow - _lastLogDiscovery > TimeSpan.FromSeconds(3)) { _logPaths = GetLogPaths().ToList(); _lastLogDiscovery = DateTime.UtcNow; }
            foreach (var path in _logPaths)
            {
                var info = new FileInfo(path); if (!info.Exists) continue;
                if (!LogOffsets.TryGetValue(path, out var offset)) { LogOffsets[path] = offset = Math.Max(0, info.Length - 65536); }
                if (info.Length < offset) offset = 0;
                if (info.Length <= offset) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); fs.Position = offset;
                using var sr = new StreamReader(fs); string? line;
                while ((line = sr.ReadLine()) != null) ProcessLogLine(line);
                LogOffsets[path] = fs.Position;
            }
        }
        catch { }
    }
    IEnumerable<string> GetLogPaths()
    {
        var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string> { Path.Combine(app, ".minecraft", "logs", "latest.log"), Path.Combine(app, ".minecraft", "logs", "blclient", "minecraft", "latest.log"), Path.Combine(home, ".lunarclient", "profiles", "lunar", "1.8", "logs", "latest.log"), Path.Combine(home, ".feather", "player_logs", "latest.log") };
        foreach (var root in new[] { Path.Combine(app, ".minecraft", "logs"), Path.Combine(app, ".minecraft", "versions") }) if (Directory.Exists(root)) candidates.AddRange(Directory.GetFiles(root, "latest.log", SearchOption.AllDirectories).Take(30));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }
    void ProcessLogLine(string line)
    {
        var low = line.ToLowerInvariant();
        if (low.Contains("connecting to")) _connectedImc = low.Contains("imc.re");
        var clean = ColorRegex.Replace(line, ""); var idx = clean.LastIndexOf("[CHAT]", StringComparison.Ordinal); if (idx < 0) return;
        var payload = clean[(idx + 6)..].Trim();
        if (_spaceArmed && (_forceImc || _connectedImc) && payload.Contains(", "))
        {
            var names = payload.Split(", ", StringSplitOptions.TrimEntries);
            if (names.Length is >= 2 and <= 36 && names.All(n => UserRegex.IsMatch(n)) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length)
            { _spaceArmed = false; SetRoster(names); FooterStatus.Text = $"已读取同局玩家（{names.Length} 人）"; }
        }
        var plus = Regex.Match(payload, @"^\[\+\]\s*([A-Za-z0-9_]{3,16})"); var joined = Regex.Match(payload, @"^([A-Za-z0-9_]{3,16}).*(?:joined the game|has joined)");
        if (plus.Success) AddPlayer(plus.Groups[1].Value); else if (joined.Success) AddPlayer(joined.Groups[1].Value);
        var stats = Regex.Match(payload, @"(?:^|\s)[-!/]?stats\s+([A-Za-z0-9_]{3,16})\s*$", RegexOptions.IgnoreCase);
        if (stats.Success) AddPlayer(stats.Groups[1].Value);
    }
    void SetRoster(IEnumerable<string> names) { Dispatcher.Invoke(() => { Players.Clear(); ByName.Clear(); foreach (var n in names) AddPlayer(n); }); }
    void AddPlayer(string name)
    {
        if (!UserRegex.IsMatch(name)) return;
        Dispatcher.Invoke(() => { if (ByName.ContainsKey(name)) return; var mode = _mode; var row = new PlayerRow { Username = name, State = "loading", GameMode = mode }; ByName[name] = row; Players.Add(row); _ = LoadStats(name, row, mode); _ = LoadAvatar(name, row); });
    }
    async Task LoadAvatar(string name, PlayerRow row)
    {
        if (AvatarCache.TryGetValue(name, out var cached)) { row.Avatar = cached; return; }
        await AvatarSlots.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync("https://mc-heads.net/avatar/" + Uri.EscapeDataString(name) + "/32");
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            AvatarCache[name] = image; row.Avatar = image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or NotSupportedException or FormatException or ArgumentException) { }
        finally { AvatarSlots.Release(); }
    }
    async Task<PlayerRow?> FetchStats(string name, string mode)
    {
        var cacheKey = mode + ":" + name;
        if (StatsCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(3)) return cached.Row;
        await ApiSlots.WaitAsync();
        try
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await StartGate.WaitAsync();
                    try {
                        var elapsed = Stopwatch.GetElapsedTime(LastStartTicks);
                        if (LastStartTicks != 0 && elapsed < TimeSpan.FromMilliseconds(50)) await Task.Delay(TimeSpan.FromMilliseconds(50) - elapsed);
                        LastStartTicks = Stopwatch.GetTimestamp();
                    } finally { StartGate.Release(); }
                    using var response = await Http.GetAsync("https://api.imc.re/stats/" + mode + "/players/" + Uri.EscapeDataString(name));
                    if (response.StatusCode == HttpStatusCode.NotFound) return new PlayerRow { Username = name, State = "unregistered", Error = "无记录", GameMode = mode };
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) { last = new HttpRequestException($"服务暂时不可用（HTTP {(int)response.StatusCode}）"); await Task.Delay(450 * (attempt + 1)); continue; }
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(); var data = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (data?.Summary == null) throw new InvalidDataException("服务返回的数据格式不正确");
                    var s = data.Summary; var m = s.Metrics;
                    var row = new PlayerRow { Username = string.IsNullOrWhiteSpace(s.Name) ? name : s.Name, State = "ok", GameMode = mode, Matches = s.Matches, Wins = s.Wins, MVPs = s.Mvps, WinRate = s.WinRate, Fkdr = m.Fkdr, BedsBroken = m.BedsBroken, FinalKills = m.FinalKills, Kills = m.Kills, AvgBeds = m.AvgBeds, KD = m.Kd, Rating = m.Rating, Adr = m.Adr, HsRate = m.HsRate };
                    StatsCache[cacheKey] = (row, DateTime.UtcNow);
                    return row;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException) { last = ex; await Task.Delay(350 * (attempt + 1)); }
            }
            return new PlayerRow { Username = name, State = "error", GameMode = mode, Error = last is TaskCanceledException ? "连接超时" : last?.Message ?? "请求失败" };
        }
        finally { ApiSlots.Release(); }
    }
    async Task LoadStats(string name, PlayerRow target, string mode)
    {
        var result = await FetchStats(name, mode); if (result == null) return;
        await Dispatcher.InvokeAsync(() => { target.CopyFrom(result); CollectionViewSource.GetDefaultView(Players).Refresh(); });
    }
    async void SearchSubmit_Click(object sender, RoutedEventArgs e) => await Search();
    async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Search(); } }
    async Task Search()
    {
        var name = SearchBox.Text.Trim(); if (!UserRegex.IsMatch(name)) { ModalMessage.Text = "请输入 3 到 16 位英文、数字或下划线组成的玩家名。"; return; }
        var mode = _mode; var version = ++_searchVersion;
        ModalMessage.Text = "正在查询，遇到服务限流时会自动重试…"; CopyButton.Visibility = Visibility.Collapsed; CardButton.Visibility = Visibility.Collapsed;
        var result = await FetchStats(name, mode); if (version != _searchVersion || mode != _mode) return; LastSearchResult = result;
        if (result?.State == "ok") { ModalMessage.Text = FormatStats(result); CopyButton.Visibility = Visibility.Visible; CardButton.Visibility = Visibility.Visible; }
        else ModalMessage.Text = result?.State == "unregistered" ? "未找到该玩家在当前模式的记录。" : $"查询失败：{result?.Error ?? "请检查网络后重试。"}";
    }
    void ShowPanel(Border panel)
    {
        var from = panel.Visibility == Visibility.Visible ? panel.Opacity : 0;
        panel.Tag = new object();
        panel.BeginAnimation(OpacityProperty, null);
        panel.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else panel.Opacity = 1;
    }
    void HidePanel(Border panel)
    {
        var from = panel.Opacity;
        var marker = new object(); panel.Tag = marker;
        panel.BeginAnimation(OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation) { panel.Visibility = Visibility.Collapsed; panel.Opacity = 1; return; }
        var animation = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160));
        animation.Completed += (_, _) => { if (ReferenceEquals(panel.Tag, marker)) { panel.Visibility = Visibility.Collapsed; panel.BeginAnimation(OpacityProperty, null); panel.Opacity = 1; } };
        panel.BeginAnimation(OpacityProperty, animation);
    }
    void Search_Click(object sender, RoutedEventArgs e) { _searchVersion++; LastSearchResult = null; ModalTitle.Text = "查询玩家战绩"; SearchBox.Text = ""; ModalMessage.Text = "输入玩家名，最多自动重试 3 次。"; CopyButton.Visibility = Visibility.Collapsed; CardButton.Visibility = Visibility.Collapsed; ShowPanel(ModalShade); SearchBox.Focus(); }
    void Settings_Click(object sender, RoutedEventArgs e) { ShowPanel(SettingsShade); }
    void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Opacity = e.NewValue; }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ModalClose_Click(object sender, RoutedEventArgs e) { _searchVersion++; HidePanel(ModalShade); }
    void SettingsClose_Click(object sender, RoutedEventArgs e) { _forceImc = ForceImcBox.IsChecked == true; ApplyMode(ModeCombo.SelectedIndex == 1 ? "counterember" : "bedwars", true); SaveConfig(); HidePanel(SettingsShade); }
    void ApplyMode(string mode, bool refresh)
    {
        bool changed = _mode != mode; _mode = mode; _searchVersion++;
        ModeTitle.Text = mode == "counterember" ? "CounterEmber" : "起床战争";
        bool ce = mode == "counterember";
        foreach (var col in PlayersGrid.Columns)
        {
            var key = col.SortMemberPath;
            bool bwOnly = key is "threat" or "Fkdr" or "BedsBroken" or "FinalKills";
            bool ceOnly = key is "Rating" or "KD" or "Adr" or "HsRate";
            col.Visibility = (bwOnly && ce) || (ceOnly && !ce) ? Visibility.Collapsed : Visibility.Visible;
        }
        _sortKey = ce ? "Rating" : "threat"; _sortDescending = true;
        foreach (var col in PlayersGrid.Columns) col.SortDirection = col.SortMemberPath == _sortKey ? ListSortDirection.Descending : null;
        ((ListCollectionView)CollectionViewSource.GetDefaultView(Players)).CustomSort = new PlayerComparer(_sortKey, true);
        if (changed && refresh) { var names = Players.Select(p => p.Username).ToArray(); SetRoster(names); FooterStatus.Text = "已切换模式，正在重新查询名单…"; LastSearchResult = null; }
    }
    void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } }
    void PlayersGrid_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && PlayersGrid.SelectedItem is PlayerRow row && row.State == "ok" && e.ClickCount == 1) Copy(row);
    }
    void PlayersGrid_DoubleClick(object sender, MouseButtonEventArgs e) { if (PlayersGrid.SelectedItem is PlayerRow p) { LastSearchResult = p; Copy(p); } }
    void CopySearch_Click(object sender, RoutedEventArgs e) { if (LastSearchResult is { State: "ok" } p) Copy(p); }
    async void CardView_Click(object sender, RoutedEventArgs e)
    {
        if (LastSearchResult is not { State: "ok" } player) return;
        int version = ++_cardVersion; _cardImage = null; _cardName = player.Username;
        CardPreview.Source = null; SaveCardButton.IsEnabled = false; CardSaveStatus.Text = "正在加载 Minecraft 官方壁纸…"; ShowPanel(CardShade);
        try
        {
            var wallpaperTask = LoadOfficialWallpaper();
            var avatarTask = player.Avatar == null ? LoadAvatar(player.Username, player) : Task.CompletedTask;
            await Task.WhenAll(wallpaperTask, avatarTask);
            if (version != _cardVersion) return;
            _cardImage = StatsCardRenderer.Render(player, await wallpaperTask, DateTime.Now);
            CardPreview.Source = _cardImage; SaveCardButton.IsEnabled = true; CardSaveStatus.Text = "保存后将自动复制图片，文件保存在「图片\\IMC.RE」文件夹。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or NotSupportedException or FormatException)
        {
            if (version == _cardVersion) CardSaveStatus.Text = "官方壁纸加载失败，请检查网络后重试。";
        }
    }
    async Task<BitmapSource> LoadOfficialWallpaper()
    {
        if (_officialWallpaper != null) return _officialWallpaper;
        const string url = "https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/software/wallpapers_minecraft_ocean_monument_.zip";
        using var response = await WallpaperHttp.GetAsync(url);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 30_000_000) throw new InvalidDataException("Wallpaper archive is too large.");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 30_000_000) throw new InvalidDataException("Wallpaper archive is too large.");
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("wallpaper_minecraft_ocean_monument_1920x1080.png") ?? throw new InvalidDataException("Wallpaper not found.");
        if (entry.Length > 10_000_000) throw new InvalidDataException("Wallpaper is too large.");
        using var entryStream = entry.Open();
        using var imageStream = new MemoryStream(); entryStream.CopyTo(imageStream); imageStream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = imageStream; image.EndInit(); image.Freeze();
        _officialWallpaper = image; return image;
    }
    void CloseCard_Click(object sender, RoutedEventArgs e) { _cardVersion++; HidePanel(CardShade); }
    void SaveCard_Click(object sender, RoutedEventArgs e)
    {
        if (_cardImage == null) return;
        string path;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "IMC.RE");
            path = SaveCardPng(_cardImage, directory, _cardName, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { CardSaveStatus.Text = "保存失败：" + ex.Message; return; }
        try
        {
            Clipboard.SetImage(_cardImage);
            CardSaveStatus.Text = "已保存并复制图片：" + path;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            CardSaveStatus.Text = "图片已保存，但复制失败，请重试。路径：" + path;
        }
    }
    static string SaveCardPng(BitmapSource image, string directory, string name, DateTime now)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}-{now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
        return path;
    }
    void Copy(PlayerRow p)
    {
        try { Clipboard.SetText(FormatStats(p)); FooterStatus.Text = $"已复制 {p.Username} 的战绩"; } catch { FooterStatus.Text = "复制失败，请稍后重试"; }
    }
    static string FormatStats(PlayerRow p) => p.GameMode == "counterember"
        ? $"[IMC.RE CounterEmber] {p.Username} | 胜率: {p.WinRate:P1} ({p.Wins}胜/{p.Matches}场) | Rating: {p.Rating:F2} | K/D: {p.KD:F2} | ADR: {p.Adr:F1} | 爆头率: {p.HsRate:P1} | MVP: {p.MVPs} | 击杀: {p.Kills}"
        : $"[IMC.RE 起床战争] {p.Username} | 胜率: {p.WinRate:P1} ({p.Wins}胜/{p.Matches}场) | FKDR: {p.Fkdr:F2} | 拆床: {p.BedsBroken} | 终杀: {p.FinalKills} | MVP: {p.MVPs} | 击杀: {p.Kills}";
    void UpdateSummary()
    {
        EmptyPanel.Visibility = Players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

sealed class PlayerComparer(string key, bool descending) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not PlayerRow a || y is not PlayerRow b) return 0;
        if (a.State == "ok" && b.State != "ok") return -1;
        if (a.State != "ok" && b.State == "ok") return 1;
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
            "Rating" => a.Rating.CompareTo(b.Rating),
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
    public string ThreatLabel => State != "ok" ? "—" : Fkdr >= 3 || (WinRate >= .6 && Matches >= 5) || BedsBroken >= 80 ? "极危" : Fkdr >= 1.7 || (WinRate >= .45 && Matches >= 5) || BedsBroken >= 30 ? "危险" : "普通";
    public string WinRateLabel => State == "ok" ? WinRate.ToString("P1") : "—"; public string FkdrLabel => State == "ok" ? Fkdr.ToString("F2") : "—";
    public string WinRateColor => State != "ok" ? "#64748B" : WinRate >= .60 ? "#34D399" : WinRate >= .40 ? "#38BDF8" : "#A8B6C9";
    public string FkdrColor => State != "ok" ? "#64748B" : Fkdr >= 4 ? "#FB7185" : Fkdr >= 2.5 ? "#FB923C" : Fkdr >= 1.5 ? "#FBBF24" : Fkdr >= .8 ? "#34D399" : "#A8B6C9";
    public string MVPColor => State == "ok" && MVPs > 0 ? "#FBBF24" : "#64748B";
    public string RatingLabel => State == "ok" ? Rating.ToString("F2") : "—"; public string KDLabel => State == "ok" ? KD.ToString("F2") : "—"; public string AdrLabel => State == "ok" ? Adr.ToString("F1") : "—"; public string HsRateLabel => State == "ok" ? HsRate.ToString("P1") : "—";
    public string MVPsLabel => State == "ok" ? (MVPs > 0 ? $"★ {MVPs}" : "0") : "—"; public string BedsLabel => State == "ok" ? BedsBroken.ToString() : "—"; public string FinalsLabel => State == "ok" ? FinalKills.ToString() : "—"; public string KillsLabel => State == "ok" ? Kills.ToString() : "—";
    public string MatchesLabel => State == "ok" ? $"{Matches} 场" : State == "loading" ? "查询中…" : State == "unregistered" ? "无记录" : "查询失败";
    public void CopyFrom(PlayerRow x) { Username=x.Username; GameMode=x.GameMode; Matches=x.Matches; Wins=x.Wins; MVPs=x.MVPs; BedsBroken=x.BedsBroken; FinalKills=x.FinalKills; Kills=x.Kills; WinRate=x.WinRate; Fkdr=x.Fkdr; AvgBeds=x.AvgBeds; KD=x.KD; Rating=x.Rating; Adr=x.Adr; HsRate=x.HsRate; Error=x.Error; State=x.State; Changed(string.Empty); }
    void Changed(string? name = null) { if (name == null) return; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); foreach (var p in new[] { nameof(ThreatLabel), nameof(WinRateLabel), nameof(FkdrLabel), nameof(MatchesLabel), nameof(MVPsLabel), nameof(BedsLabel), nameof(FinalsLabel), nameof(KillsLabel) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p)); }
}

public sealed class ApiResponse { public ApiSummary? Summary { get; set; } }
public sealed class ApiSummary { public string Name { get; set; } = ""; public long Matches { get; set; } public long Wins { get; set; } public long Mvps { get; set; } public double WinRate { get; set; } public ApiMetrics Metrics { get; set; } = new(); }
public sealed class ApiMetrics { public double Fkdr { get; set; } public double Kd { get; set; } public double AvgBeds { get; set; } public double Rating { get; set; } public double Adr { get; set; } public double HsRate { get; set; } public long FinalKills { get; set; } public long BedsBroken { get; set; } public long Kills { get; set; } }
