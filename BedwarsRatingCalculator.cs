namespace IMCBedwarsOverlay;

// A bounded heuristic, not an official IMC.RE rating. One is the neutral baseline.
internal static class BedwarsRatingCalculator
{
    public static double? Calculate(long matches, long wins, long bedsBroken, double fkdr, double kd)
    {
        if (matches <= 0 || wins < 0 || wins > matches || bedsBroken < 0
            || !double.IsFinite(fkdr) || fkdr < 0
            || !double.IsFinite(kd) || kd < 0)
            return null;

        var n = (double)matches;
        var losses = matches - wins;
        var wlr = wins / (double)Math.Max(1L, losses);
        var bedsPerGame = bedsBroken / n;

        var fkdrRating = Math.Clamp(Math.Log2((fkdr + 1.0) / 2.0), -1.0, 3.0);
        var bedsRating = Math.Clamp(Math.Log2(bedsPerGame + 0.5), -1.0, 2.0);
        var wlrRating = Math.Clamp(Math.Log2((wlr + 1.0) / 2.0), -1.0, 2.0);
        var kdRating = Math.Clamp(Math.Log2((kd + 1.0) / 2.0), -1.0, 2.0);
        var performance = 0.45 * fkdrRating + 0.40 * bedsRating + 0.10 * wlrRating + 0.05 * kdRating;
        var confidence = n / (n + 20.0);
        var rating = 1.0 + 1.25 * confidence * performance;

        return Math.Clamp(rating, 0.10, 3.00);
    }
}
