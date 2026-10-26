// Simulated hand noise, scaled by stress (0 = calm, 1 = high-stakes): a fine 8-12 Hz physiological
// tremor, a stronger 4-7 Hz stress tremor that comes and goes in waves, and occasional twitches that
// jerk a few pixels and pull back. Offset(t) is a position offset in pixels that always returns to
// zero on average, so callers send its change between steps and the aim never drifts because of it.
sealed class HandNoise
{
    readonly Random random;
    readonly double stress;
    readonly double fineHz, finePhaseX, finePhaseY, fineAmp;
    readonly double stressHz, stressPhaseX, stressPhaseY, stressAmp;
    double wave, waveTarget, nextWaveChange, lastT = double.NaN;
    double twitchAt = double.NaN, twitchX, twitchY, twitchRise, twitchFall, nextTwitch;

    public HandNoise(Random random, double stress)
    {
        this.random = random;
        this.stress = Math.Clamp(stress, 0, 1);
        fineHz = Between(8, 12);
        finePhaseX = Between(0, Math.Tau);
        finePhaseY = Between(0, Math.Tau);
        fineAmp = 0.25 + 1.1 * this.stress;
        stressHz = Between(4, 7);
        stressPhaseX = Between(0, Math.Tau);
        stressPhaseY = Between(0, Math.Tau);
        stressAmp = 4 * this.stress;
        nextTwitch = NextTwitchGap();
    }

    // t is in seconds and must not go backwards.
    public (double X, double Y) Offset(double t)
    {
        if (double.IsNaN(lastT)) { lastT = t; nextTwitch += t; nextWaveChange = t; }

        // Stress tremor strength drifts toward a new random level every 0.2-0.8s (waves of shakiness).
        if (t >= nextWaveChange)
        {
            waveTarget = random.NextDouble();
            nextWaveChange = t + Between(0.2, 0.8);
        }
        wave += (waveTarget - wave) * Math.Min(1, (t - lastT) * 6);
        lastT = t;

        double x = fineAmp * Math.Sin(Math.Tau * fineHz * t + finePhaseX)
                 + stressAmp * wave * Math.Sin(Math.Tau * stressHz * t + stressPhaseX);
        double y = fineAmp * 0.8 * Math.Sin(Math.Tau * fineHz * 1.07 * t + finePhaseY)
                 + stressAmp * wave * 0.7 * Math.Sin(Math.Tau * stressHz * 1.13 * t + stressPhaseY);

        // Twitches: a quick jerk (15-35ms) that pulls back over 60-150ms.
        if (stress > 0 && t >= nextTwitch && double.IsNaN(twitchAt))
        {
            var size = Between(2, 8) * (0.5 + stress);
            var angle = Between(0, Math.Tau);
            (twitchX, twitchY) = (Math.Cos(angle) * size, Math.Sin(angle) * size);
            twitchAt = t;
            twitchRise = Between(0.015, 0.035);
            twitchFall = Between(0.06, 0.15);
        }
        if (!double.IsNaN(twitchAt))
        {
            var age = t - twitchAt;
            var shape = age < twitchRise ? age / twitchRise : Math.Max(0, 1 - (age - twitchRise) / twitchFall);
            x += twitchX * shape;
            y += twitchY * shape;
            if (age >= twitchRise + twitchFall)
            {
                twitchAt = double.NaN;
                nextTwitch = t + NextTwitchGap();
            }
        }
        return (x, y);
    }

    // Twitches come at random, up to about two a second at full stress.
    double NextTwitchGap() => stress <= 0 ? double.PositiveInfinity : -Math.Log(1 - random.NextDouble()) / (2 * stress);

    double Between(double min, double max) => min + (max - min) * random.NextDouble();
}
