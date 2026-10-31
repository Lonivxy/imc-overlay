using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IMCBedwarsOverlay;

internal sealed record AppSettings(bool ForceImc = true, double Opacity = 0.9, string GameMode = "bedwars", string PlayerName = "");

internal sealed class ConfigService
{
    readonly string _path;

    public ConfigService(string? path = null) => _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IMCBedwarsOverlay", "config.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            bool force = root.TryGetProperty("forceImc", out var f) ? f.GetBoolean() : true;
            double opacity = root.TryGetProperty("opacity", out var o) && o.TryGetDouble(out var value) ? Math.Clamp(value, 0.5, 1.0) : 0.9;
            string mode = root.TryGetProperty("gameMode", out var m) && m.GetString() == "counterember" ? "counterember" : "bedwars";
            string player = root.TryGetProperty("playerName", out var p) ? p.GetString() ?? "" : "";
            return new(force, opacity, mode, player);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return new(); }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            JsonObject root;
            try { root = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new() : new(); }
            catch (JsonException) { root = new(); }
            root["forceImc"] = settings.ForceImc; root["toggleKey"] = 0xDC; root["opacity"] = settings.Opacity; root["gameMode"] = settings.GameMode;
            root["playerName"] = settings.PlayerName;
            File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
