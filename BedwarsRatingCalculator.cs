namespace IMCBedwarsOverlay;

// A bounded heuristic, not an official IMC or CS2 rating. One is the neutral baseline.
internal static class BedwarsRatingCalculator
{
    public static double? Calculate(long matches, double winRate, double fkdr, double avgBeds)
    {
        if (matches <= 0 || !double.IsFinite(winRate) || !double.IsFinite(fkdr) || !double.IsFinite(avgBeds)) return null;
        var win = Math.Clamp((winRate - 0.5) * 2, -1, 1);
        var finals = Math.Clamp(Math.Log2((Math.Max(0, fkdr) + 1) / 2), -1, 2);
        var beds = Math.Clamp((avgBeds - 0.5) / 0.5, -1, 2);
        var confidence = matches / (matches + 20.0);
        return Math.Clamp(1.0 + confidence * (0.45 * finals + 0.35 * win + 0.20 * beds), 0.25, 2.5);
    }
}
