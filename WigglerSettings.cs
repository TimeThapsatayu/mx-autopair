// Wiggler settings: everything the command center can choose, and this PC's own limits for each value
// (Normalize). FromJson reads only the known fields; a missing field or one of the wrong type keeps its
// default, and an unknown scenario name falls back to "random". Defaults match the original behaviour.
using System.Text.Json;

sealed record WigglerSettings
{
    public static readonly string[] Scenarios = ["random", "flick", "track", "spray", "peek"];
    public static readonly WigglerSettings Default = new();

    public bool Enabled { get; init; } = true;
    public double MinInterval { get; init; } = 30;     // seconds between bursts
    public double MaxInterval { get; init; } = 90;
    public double MinBurst { get; init; } = 0.5;       // burst length, seconds
    public double MaxBurst { get; init; } = 3;
    public double MinActionDelay { get; init; } = 0;   // pause between mouse actions in a burst, ms
    public double MaxActionDelay { get; init; } = 0;
    public string Scenario { get; init; } = "random";  // one of Scenarios, or "mixed"
    public IReadOnlyList<string> Mixed { get; init; } = Scenarios;  // what "mixed" picks from
    public double Stress { get; init; } = 0.5;         // hand tremor and twitchiness, 0 = calm, 1 = high-stakes

    // random: chance of each action after a move (they share one roll)
    public double LeftClick { get; init; } = 0.25;
    public double RightClick { get; init; } = 0.15;
    public double Scroll { get; init; } = 0.25;

    // flick: distance (px), duration (ms), chance of overshooting and correcting back
    public double MinFlickDistance { get; init; } = 150;
    public double MaxFlickDistance { get; init; } = 900;
    public double MinFlickTime { get; init; } = 80;
    public double MaxFlickTime { get; init; } = 250;
    public double FlickOvershoot { get; init; } = 0.3;

    // track: strafing target speed (px/s), optionally firing the whole time
    public double MinTrackSpeed { get; init; } = 200;
    public double MaxTrackSpeed { get; init; } = 800;
    public bool TrackHoldFire { get; init; }

    // spray: hold time (ms), downward pull (px/s), sideways wobble (px)
    public double MinSprayTime { get; init; } = 600;
    public double MaxSprayTime { get; init; } = 1500;
    public double SprayPull { get; init; } = 150;
    public double SprayWobble { get; init; } = 3;

    // shooting (flick and peek shots): burst fire - taps per trigger pull, and the gap between taps (ms)
    public double MinShots { get; init; } = 3;
    public double MaxShots { get; init; } = 4;
    public double MinShotGap { get; init; } = 70;
    public double MaxShotGap { get; init; } = 110;

    // peek: jiggle-peeking - quick uneven left-right swings (px, ms each), then micro-adjust and maybe shoot
    public double MinPeekAmplitude { get; init; } = 15;
    public double MaxPeekAmplitude { get; init; } = 90;
    public double MinPeekSwing { get; init; } = 60;
    public double MaxPeekSwing { get; init; } = 160;

    public WigglerSettings Normalize()
    {
        static double Fit(double v, double min, double max, double fallback) =>
            double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        static (double, double) Range(double a, double b, double min, double max, double fa, double fb)
        {
            var (x, y) = (Fit(a, min, max, fa), Fit(b, min, max, fb));
            return (Math.Min(x, y), Math.Max(x, y));
        }
        var d = Default;
        var (minInterval, maxInterval) = Range(MinInterval, MaxInterval, 1, 86400, d.MinInterval, d.MaxInterval);
        var (minBurst, maxBurst) = Range(MinBurst, MaxBurst, 0.1, 60, d.MinBurst, d.MaxBurst);
        var (minDelay, maxDelay) = Range(MinActionDelay, MaxActionDelay, 0, 10000, d.MinActionDelay, d.MaxActionDelay);
        var (minDistance, maxDistance) = Range(MinFlickDistance, MaxFlickDistance, 1, 10000, d.MinFlickDistance, d.MaxFlickDistance);
        var (minFlick, maxFlick) = Range(MinFlickTime, MaxFlickTime, 10, 5000, d.MinFlickTime, d.MaxFlickTime);
        var (minSpeed, maxSpeed) = Range(MinTrackSpeed, MaxTrackSpeed, 1, 20000, d.MinTrackSpeed, d.MaxTrackSpeed);
        var (minSpray, maxSpray) = Range(MinSprayTime, MaxSprayTime, 50, 10000, d.MinSprayTime, d.MaxSprayTime);
        var (minPeek, maxPeek) = Range(MinPeekAmplitude, MaxPeekAmplitude, 1, 2000, d.MinPeekAmplitude, d.MaxPeekAmplitude);
        var (minSwing, maxSwing) = Range(MinPeekSwing, MaxPeekSwing, 10, 2000, d.MinPeekSwing, d.MaxPeekSwing);
        var (minShots, maxShots) = Range(Math.Round(MinShots), Math.Round(MaxShots), 1, 20, d.MinShots, d.MaxShots);
        var (minGap, maxGap) = Range(MinShotGap, MaxShotGap, 20, 1000, d.MinShotGap, d.MaxShotGap);
        double left = Fit(LeftClick, 0, 1, d.LeftClick), right = Fit(RightClick, 0, 1, d.RightClick), scroll = Fit(Scroll, 0, 1, d.Scroll);
        var total = left + right + scroll;
        if (total > 1) { left /= total; right /= total; scroll /= total; }
        var mixed = Mixed.Where(Scenarios.Contains).Distinct().ToArray();
        return this with
        {
            MinInterval = minInterval, MaxInterval = maxInterval,
            MinBurst = minBurst, MaxBurst = maxBurst,
            MinActionDelay = minDelay, MaxActionDelay = maxDelay,
            Scenario = Scenarios.Contains(Scenario) || Scenario == "mixed" ? Scenario : "random",
            Mixed = mixed.Length > 0 ? mixed : ["random"],
            Stress = Fit(Stress, 0, 1, d.Stress),
            LeftClick = left, RightClick = right, Scroll = scroll,
            MinFlickDistance = minDistance, MaxFlickDistance = maxDistance,
            MinFlickTime = minFlick, MaxFlickTime = maxFlick,
            FlickOvershoot = Fit(FlickOvershoot, 0, 1, d.FlickOvershoot),
            MinTrackSpeed = minSpeed, MaxTrackSpeed = maxSpeed,
            MinSprayTime = minSpray, MaxSprayTime = maxSpray,
            SprayPull = Fit(SprayPull, 0, 5000, d.SprayPull),
            SprayWobble = Fit(SprayWobble, 0, 500, d.SprayWobble),
            MinPeekAmplitude = minPeek, MaxPeekAmplitude = maxPeek,
            MinPeekSwing = minSwing, MaxPeekSwing = maxSwing,
            MinShots = minShots, MaxShots = maxShots,
            MinShotGap = minGap, MaxShotGap = maxGap,
        };
    }

    public static WigglerSettings FromJson(JsonElement w)
    {
        var d = Default;
        double Num(string name, double fallback) =>
            w.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : fallback;
        bool Flag(string name, bool fallback) =>
            w.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
        var scenario = w.TryGetProperty("scenario", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : d.Scenario;
        var mixed = w.TryGetProperty("mixed", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(16).ToArray()
            : d.Mixed;
        return new WigglerSettings
        {
            Enabled = Flag("enabled", d.Enabled),
            MinInterval = Num("minInterval", d.MinInterval), MaxInterval = Num("maxInterval", d.MaxInterval),
            MinBurst = Num("minBurst", d.MinBurst), MaxBurst = Num("maxBurst", d.MaxBurst),
            MinActionDelay = Num("minActionDelay", d.MinActionDelay), MaxActionDelay = Num("maxActionDelay", d.MaxActionDelay),
            Scenario = scenario, Mixed = mixed, Stress = Num("stress", d.Stress),
            LeftClick = Num("leftClick", d.LeftClick), RightClick = Num("rightClick", d.RightClick), Scroll = Num("scroll", d.Scroll),
            MinFlickDistance = Num("minFlickDistance", d.MinFlickDistance), MaxFlickDistance = Num("maxFlickDistance", d.MaxFlickDistance),
            MinFlickTime = Num("minFlickTime", d.MinFlickTime), MaxFlickTime = Num("maxFlickTime", d.MaxFlickTime),
            FlickOvershoot = Num("flickOvershoot", d.FlickOvershoot),
            MinTrackSpeed = Num("minTrackSpeed", d.MinTrackSpeed), MaxTrackSpeed = Num("maxTrackSpeed", d.MaxTrackSpeed),
            TrackHoldFire = Flag("trackHoldFire", d.TrackHoldFire),
            MinSprayTime = Num("minSprayTime", d.MinSprayTime), MaxSprayTime = Num("maxSprayTime", d.MaxSprayTime),
            SprayPull = Num("sprayPull", d.SprayPull), SprayWobble = Num("sprayWobble", d.SprayWobble),
            MinPeekAmplitude = Num("minPeekAmplitude", d.MinPeekAmplitude), MaxPeekAmplitude = Num("maxPeekAmplitude", d.MaxPeekAmplitude),
            MinPeekSwing = Num("minPeekSwing", d.MinPeekSwing), MaxPeekSwing = Num("maxPeekSwing", d.MaxPeekSwing),
            MinShots = Num("minShots", d.MinShots), MaxShots = Num("maxShots", d.MaxShots),
            MinShotGap = Num("minShotGap", d.MinShotGap), MaxShotGap = Num("maxShotGap", d.MaxShotGap),
        }.Normalize();
    }

    public void WriteJson(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteBoolean("enabled", Enabled);
        json.WriteNumber("minInterval", MinInterval);
        json.WriteNumber("maxInterval", MaxInterval);
        json.WriteNumber("minBurst", MinBurst);
        json.WriteNumber("maxBurst", MaxBurst);
        json.WriteNumber("minActionDelay", MinActionDelay);
        json.WriteNumber("maxActionDelay", MaxActionDelay);
        json.WriteString("scenario", Scenario);
        json.WriteStartArray("mixed");
        foreach (var name in Mixed) json.WriteStringValue(name);
        json.WriteEndArray();
        json.WriteNumber("stress", Stress);
        json.WriteNumber("leftClick", LeftClick);
        json.WriteNumber("rightClick", RightClick);
        json.WriteNumber("scroll", Scroll);
        json.WriteNumber("minFlickDistance", MinFlickDistance);
        json.WriteNumber("maxFlickDistance", MaxFlickDistance);
        json.WriteNumber("minFlickTime", MinFlickTime);
        json.WriteNumber("maxFlickTime", MaxFlickTime);
        json.WriteNumber("flickOvershoot", FlickOvershoot);
        json.WriteNumber("minTrackSpeed", MinTrackSpeed);
        json.WriteNumber("maxTrackSpeed", MaxTrackSpeed);
        json.WriteBoolean("trackHoldFire", TrackHoldFire);
        json.WriteNumber("minSprayTime", MinSprayTime);
        json.WriteNumber("maxSprayTime", MaxSprayTime);
        json.WriteNumber("sprayPull", SprayPull);
        json.WriteNumber("sprayWobble", SprayWobble);
        json.WriteNumber("minPeekAmplitude", MinPeekAmplitude);
        json.WriteNumber("maxPeekAmplitude", MaxPeekAmplitude);
        json.WriteNumber("minPeekSwing", MinPeekSwing);
        json.WriteNumber("maxPeekSwing", MaxPeekSwing);
        json.WriteNumber("minShots", MinShots);
        json.WriteNumber("maxShots", MaxShots);
        json.WriteNumber("minShotGap", MinShotGap);
        json.WriteNumber("maxShotGap", MaxShotGap);
        json.WriteEndObject();
    }

    public override string ToString()
    {
        var scenario = Scenario == "mixed" ? $"mixed ({string.Join("/", Mixed)})" : Scenario;
        var delay = MaxActionDelay > 0 ? $", action delay {MinActionDelay:0}-{MaxActionDelay:0}ms" : "";
        return $"{(Enabled ? "on" : "off")}, {scenario}, stress {Stress:0%}, every {MinInterval:0.#}-{MaxInterval:0.#}s, bursts {MinBurst:0.#}-{MaxBurst:0.#}s{delay}";
    }
}
