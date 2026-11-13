using Godot;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace MedievalClient;

/// <summary>
/// How the world looks through the year: snow settles on upward-facing ground, roofs and trees in winter and melts
/// in early spring, and greens turn golden in autumn. All seasonal materials share one shader whose two values
/// are updated as the game clock moves. Purely cosmetic.
/// </summary>
public static class SeasonLook
{
    private const string ShaderCode = @"
shader_type spatial;
render_mode cull_disabled;

uniform vec4 tint = vec4(1.0);
uniform float snow = 0.0;
uniform float autumn = 0.0;

varying float up;

vec3 to_linear(vec3 c) {
    return mix(pow((c + vec3(0.055)) * (1.0 / 1.055), vec3(2.4)), c * (1.0 / 12.92), lessThan(c, vec3(0.04045)));
}

void vertex() {
    up = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz).y;
}

void fragment() {
    vec3 c = COLOR.rgb;
    // Greens (grass, leaves, crops) turn golden and brown in autumn.
    float green = clamp((c.g - max(c.r, c.b)) * 5.0, 0.0, 1.0);
    float lum = dot(c, vec3(0.3, 0.6, 0.1));
    c = mix(c, vec3(lum * 1.45, lum * 1.0, lum * 0.35), green * autumn * 0.65);
    // Snow lies on whatever faces the sky.
    // Flat ground and low roofs are covered; steep roofs keep their colour, so buildings stay readable.
    float lying = snow * smoothstep(0.72, 0.98, up);
    c = mix(c, vec3(0.76, 0.79, 0.85), lying * 0.85);
    // Like Godot's own materials with sRGB vertex colours: convert only when rendering in linear space.
    if (!OUTPUT_IS_SRGB) c = to_linear(c);
    ALBEDO = c * tint.rgb;
    ROUGHNESS = 1.0;
}";

    private static readonly Shader Shader = new() { Code = ShaderCode };
    private static readonly List<ShaderMaterial> All = new();
    private static float _snow = -1, _autumn = -1;

    /// <summary>Plain seasonal material (terrain, trees, rocks).</summary>
    public static readonly ShaderMaterial Default = Make(Colors.White);

    /// <summary>A seasonal material with a colour multiplier (per-building variation).</summary>
    public static ShaderMaterial Make(Color tint)
    {
        var material = new ShaderMaterial { Shader = Shader };
        material.SetShaderParameter("tint", tint);
        material.SetShaderParameter("snow", Math.Max(0, _snow));
        material.SetShaderParameter("autumn", Math.Max(0, _autumn));
        All.Add(material);
        return material;
    }

    /// <summary>Snow cover (0..1) and autumn colour (0..1) at a moment of the year.</summary>
    public static (float Snow, float Autumn) At(GameTime time)
    {
        double dayInSeason = (time.DayOfYear - 1) % TimeScale.DaysPerSeason + time.TickOfDay / (double)TimeScale.TicksPerDay;
        float Ramp(double from, double to) => (float)Math.Clamp((dayInSeason - from) / (to - from), 0, 1);
        return time.Season switch
        {
            Season.Winter => (Ramp(0, 6), 1 - Ramp(0, 10)),           // first snow; the last leaves fall
            Season.Spring => (1 - Ramp(0, 8), 0),                    // the thaw
            Season.Autumn => (0, Ramp(0, 20)),                       // leaves turn
            _ => (0, 0),
        };
    }

    /// <summary>How deep into winter it is (0..1): in through early winter, out with the thaw, a hint in late autumn.</summary>
    public static float Winterness(GameTime time)
    {
        double dayInSeason = (time.DayOfYear - 1) % TimeScale.DaysPerSeason + time.TickOfDay / (double)TimeScale.TicksPerDay;
        float Ramp(double from, double to) => (float)Math.Clamp((dayInSeason - from) / (to - from), 0, 1);
        return time.Season switch
        {
            Season.Winter => Ramp(0, 10),
            Season.Spring => 1 - Ramp(0, 10),
            Season.Autumn => 0.3f * Ramp(60, 90),
            _ => 0,
        };
    }

    /// <summary>Short winter days: a lower, dimmer, cooler sun (long shadows); the summer sun is high and warm.</summary>
    public static void LightSun(DirectionalLight3D sun, GameTime time)
    {
        float w = Winterness(time);
        sun.LightEnergy = Mathf.Lerp(0.85f, 0.68f, w);
        sun.LightColor = new Color(1f, 0.95f, 0.85f).Lerp(new Color(0.88f, 0.92f, 1f), w);
        sun.RotationDegrees = new Vector3(Mathf.Lerp(-50f, -30f, w), 35f, 0f);
    }

    /// <summary>Call every frame; only touches the materials when the look has visibly changed.</summary>
    public static void Update(GameWorld world)
    {
        var (snow, autumn) = At(world.Time);
        if (Math.Abs(snow - _snow) < 0.01f && Math.Abs(autumn - _autumn) < 0.01f) return;
        _snow = snow;
        _autumn = autumn;
        foreach (var m in All)
        {
            m.SetShaderParameter("snow", snow);
            m.SetShaderParameter("autumn", autumn);
        }
    }
}
