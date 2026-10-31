using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IMCBedwarsOverlay;

internal sealed record AppSettings(
    bool ForceImc = true,
    double Opacity = 0.9,
    string GameMode = "bedwars",
    string PlayerName = "",
    int ToggleKey = 0xDC,
    uint ToggleModifiers = 0,
    string ToggleKeyName = "\\",
    double? WindowLeft = null,
    double? WindowTop = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    bool ThreatAlert = true
);

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
            int toggleKey = root.TryGetProperty("toggleKey", out var tk) && tk.TryGetInt32(out var tkVal) && tkVal > 0 ? tkVal : 0xDC;
            uint toggleMods = root.TryGetProperty("toggleModifiers", out var tm) && tm.TryGetUInt32(out var tmVal) ? tmVal : 0;
            string toggleName = root.TryGetProperty("toggleKeyName", out var tkn) ? tkn.GetString() ?? "\\" : "\\";
            double? winLeft = root.TryGetProperty("windowLeft", out var wl) && wl.TryGetDouble(out var wlVal) ? wlVal : null;
            double? winTop = root.TryGetProperty("windowTop", out var wt) && wt.TryGetDouble(out var wtVal) ? wtVal : null;
            double? winWidth = root.TryGetProperty("windowWidth", out var ww) && ww.TryGetDouble(out var wwVal) ? wwVal : null;
            double? winHeight = root.TryGetProperty("windowHeight", out var wh) && wh.TryGetDouble(out var whVal) ? whVal : null;
            bool threatAlert = root.TryGetProperty("threatAlert", out var ta) ? ta.GetBoolean() : true;

            return new(force, opacity, mode, player, toggleKey, toggleMods, toggleName, winLeft, winTop, winWidth, winHeight, threatAlert);
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
            root["forceImc"] = settings.ForceImc;
            root["toggleKey"] = settings.ToggleKey;
            root["toggleModifiers"] = (int)settings.ToggleModifiers;
            root["toggleKeyName"] = settings.ToggleKeyName;
            root["opacity"] = settings.Opacity;
            root["gameMode"] = settings.GameMode;
            root["playerName"] = settings.PlayerName;
            if (settings.WindowLeft.HasValue) root["windowLeft"] = settings.WindowLeft.Value;
            if (settings.WindowTop.HasValue) root["windowTop"] = settings.WindowTop.Value;
            if (settings.WindowWidth.HasValue) root["windowWidth"] = settings.WindowWidth.Value;
            if (settings.WindowHeight.HasValue) root["windowHeight"] = settings.WindowHeight.Value;
            root["threatAlert"] = settings.ThreatAlert;
            File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
