namespace PGW.Core;

public enum ScalingMode { None, Linear, Sqrt }

public sealed record ScalingConfig(ScalingMode Mode, double RawLo, double RawHi, double EuLo, double EuHi, bool Clamp = true);

/// <summary>raw (device units) &lt;-&gt; engineering units conversion, shared by every driver and the write path.</summary>
public static class Scaling
{
    public static object? Apply(ScalingConfig? cfg, object? raw)
    {
        if (cfg is null || cfg.Mode == ScalingMode.None || raw is null || raw is bool || raw is string) return raw;
        double x = Convert.ToDouble(raw);
        double rawSpan = cfg.RawHi - cfg.RawLo;
        double euSpan = cfg.EuHi - cfg.EuLo;
        double eu = cfg.Mode switch
        {
            ScalingMode.Linear => rawSpan == 0 ? cfg.EuLo : cfg.EuLo + (x - cfg.RawLo) * euSpan / rawSpan,
            ScalingMode.Sqrt => cfg.EuLo + Math.Sqrt(Math.Max(0, rawSpan == 0 ? 0 : (x - cfg.RawLo) / rawSpan)) * euSpan,
            _ => x,
        };
        return cfg.Clamp ? Math.Clamp(eu, Math.Min(cfg.EuLo, cfg.EuHi), Math.Max(cfg.EuLo, cfg.EuHi)) : eu;
    }

    public static object? Reverse(ScalingConfig? cfg, object? eu)
    {
        if (cfg is null || cfg.Mode == ScalingMode.None || eu is null || eu is bool || eu is string) return eu;
        double y = Convert.ToDouble(eu);
        double rawSpan = cfg.RawHi - cfg.RawLo;
        double euSpan = cfg.EuHi - cfg.EuLo;
        double raw = cfg.Mode switch
        {
            ScalingMode.Linear => euSpan == 0 ? cfg.RawLo : cfg.RawLo + (y - cfg.EuLo) * rawSpan / euSpan,
            ScalingMode.Sqrt => cfg.RawLo + Math.Pow(euSpan == 0 ? 0 : (y - cfg.EuLo) / euSpan, 2) * rawSpan,
            _ => y,
        };
        return cfg.Clamp ? Math.Clamp(raw, Math.Min(cfg.RawLo, cfg.RawHi), Math.Max(cfg.RawLo, cfg.RawHi)) : raw;
    }
}
