namespace IMCBedwarsOverlay;

// A bounded heuristic, not an official IMC.RE rating. One is the neutral baseline.
internal static class BedwarsRatingCalculator
{
    public static double? Calculate(long matches, long wins, double fkdr, double kd)
    {
        if (matches <= 0 || wins < 0 || wins > matches
            || !double.IsFinite(fkdr) || fkdr < 0
            || !double.IsFinite(kd) || kd < 0)
            return null;

        var n = (double)matches;
        var losses = matches - wins;
        var wlr = wins / (double)Math.Max(1L, losses);

        var fkdrRating = 0.20 + 0.80 * Math.Log2(fkdr + 1.0);
        var wlrRating = 0.20 + 0.80 * Math.Log2(wlr + 1.0);
        var kdRating = 0.25 + 0.75 * Math.Log2(kd + 1.0);
        var performance = 0.45 * fkdrRating + 0.35 * wlrRating + 0.20 * kdRating;
        var confidence = n / (n + 20.0);
        var experienceBonus = 0.04 * Math.Log10(n + 1.0);
        var rating = 1.0 + confidence * (performance - 1.0) * 1.25 + experienceBonus;

        return Math.Clamp(rating, 0.10, 3.00);
    }
}
