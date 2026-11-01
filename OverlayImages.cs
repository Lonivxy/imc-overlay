using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IMCBedwarsOverlay;

internal interface IOverlayImages
{
    bool TryGetAvatar(string playerName, out ImageSource? avatar);
    Task<ImageSource?> GetAvatarAsync(string playerName);
    Task<BitmapSource> GetOfficialWallpaperAsync();
}

internal sealed class OverlayImages : IOverlayImages
{
    static readonly HttpClient AvatarHttp = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly HttpClient WallpaperHttp = new() { Timeout = TimeSpan.FromSeconds(45) };
    static readonly SemaphoreSlim AvatarSlots = new(6, 6);
    static readonly string[] AvatarUrls =
    [
        "https://crafthead.net/avatar/{0}/32",
        "https://mc-heads.net/avatar/{0}/32",
        "https://minotar.net/avatar/{0}/32",
        "https://cravatar.eu/avatar/{0}/32"
    ];

    readonly ConcurrentDictionary<string, ImageSource> _avatars = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<ImageSource?>> _inFlightAvatars = new(StringComparer.OrdinalIgnoreCase);
    readonly object _avatarSync = new();
    readonly object _wallpaperSync = new();
    Task<BitmapSource>? _wallpaperTask;

    static OverlayImages()
    {
        AvatarHttp.DefaultRequestHeaders.UserAgent.ParseAdd("IMC-Overlay/2.1");
    }

    public bool TryGetAvatar(string playerName, out ImageSource? avatar)
        => _avatars.TryGetValue(playerName, out avatar);

    public Task<ImageSource?> GetAvatarAsync(string playerName)
    {
        if (_avatars.TryGetValue(playerName, out var avatar)) return Task.FromResult<ImageSource?>(avatar);

        lock (_avatarSync)
        {
            if (_avatars.TryGetValue(playerName, out avatar)) return Task.FromResult<ImageSource?>(avatar);
            if (_inFlightAvatars.TryGetValue(playerName, out var pending)) return pending;

            pending = FetchAvatarAsync(playerName);
            _inFlightAvatars[playerName] = pending;
            _ = pending.ContinueWith(
                _ =>
                {
                    lock (_avatarSync)
                    {
                        if (_inFlightAvatars.TryGetValue(playerName, out var current)
                            && ReferenceEquals(current, pending))
                            _inFlightAvatars.Remove(playerName);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return pending;
        }
    }

    async Task<ImageSource?> FetchAvatarAsync(string playerName)
    {
        await AvatarSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_avatars.TryGetValue(playerName, out var cached)) return cached;
            foreach (var template in AvatarUrls)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Uri.EscapeDataString(playerName));
                    var bytes = await AvatarHttp.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
                    var image = Decode(bytes);
                    _avatars[playerName] = image;
                    return image;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                    or NotSupportedException or FormatException or ArgumentException or ExternalException) { }
            }
        }
        finally { AvatarSlots.Release(); }
        return null;
    }

    public Task<BitmapSource> GetOfficialWallpaperAsync()
    {
        lock (_wallpaperSync)
            return _wallpaperTask ??= LoadOfficialWallpaperAsync();
    }

    async Task<BitmapSource> LoadOfficialWallpaperAsync()
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IMCBedwarsOverlay");
        var cacheFile = Path.Combine(cacheDir, "wallpaper_ocean_monument.png");
        if (File.Exists(cacheFile))
        {
            try { return Decode(await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException) { }
        }

        try
        {
            const string url = "https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/software/wallpapers_minecraft_ocean_monument_.zip";
            using var response = await WallpaperHttp.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            using var archiveStream = new MemoryStream(bytes);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("wallpaper_minecraft_ocean_monument_1920x1080.png")
                ?? throw new InvalidDataException("Minecraft wallpaper image was not found in the official archive.");
            await using var entryStream = entry.Open();
            using var imageStream = new MemoryStream();
            await entryStream.CopyToAsync(imageStream).ConfigureAwait(false);
            var imageBytes = imageStream.ToArray();

            try
            {
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllBytesAsync(cacheFile, imageBytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

            return Decode(imageBytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or InvalidDataException or NotSupportedException or FormatException or ArgumentException or ExternalException)
        {
            return CreateFallbackWallpaper();
        }
    }

    static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    static BitmapSource CreateFallbackWallpaper()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var gradient = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#0C1A2E"),
                (Color)ColorConverter.ConvertFromString("#050A14"),
                new Point(0, 0), new Point(1, 1));
            context.DrawRectangle(gradient, null, new Rect(0, 0, 1920, 1080));
        }

        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
