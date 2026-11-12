using Godot;

namespace MedievalClient.Audio;

/// <summary>
/// Generates every sound in the game from code (no audio files), so there are no third-party
/// licences to track. Each sound is a small synthesis recipe: sine partials, filtered noise and
/// envelopes, rendered once at startup into 16-bit mono PCM.
/// </summary>
public static class SoundSynth
{
    public const int SampleRate = 22050;

    /// <summary>Tiny deterministic noise source, so sounds come out the same every run.</summary>
    private sealed class Noise
    {
        private uint _state;
        public Noise(uint seed) => _state = seed == 0 ? 1u : seed;

        public float Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state / (float)uint.MaxValue) * 2f - 1f;
        }
    }

    public static AudioStreamWav Click()
    {
        var s = new float[(int)(SampleRate * 0.035f)];
        var noise = new Noise(7);
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float env = Mathf.Exp(-t * 140f);
            s[i] = (Mathf.Sin(Mathf.Tau * 1150f * t) * 0.6f + noise.Next() * 0.15f) * env;
        }
        return ToStream(s, 0.5f);
    }

    /// <summary>A soft wooden thud for placing a building or road.</summary>
    public static AudioStreamWav Place()
    {
        var s = new float[(int)(SampleRate * 0.25f)];
        var noise = new Noise(11);
        float phase = 0, low = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float freq = 60f + 90f * Mathf.Exp(-t * 30f); // pitch drop
            phase += Mathf.Tau * freq / SampleRate;
            low += (noise.Next() - low) * 0.08f; // low-passed noise
            float env = Mathf.Exp(-t * 18f);
            s[i] = (Mathf.Sin(phase) * 0.8f + low * 0.9f) * env;
        }
        return ToStream(s, 0.8f);
    }

    /// <summary>Rubble falling: a few bursts of rumbling, filtered noise.</summary>
    public static AudioStreamWav Demolish()
    {
        var s = new float[(int)(SampleRate * 0.9f)];
        var noise = new Noise(23);
        float low = 0, mid = 0;
        float[] bursts = { 0f, 0.12f, 0.26f, 0.45f };
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float env = 0;
            foreach (float start in bursts)
                if (t >= start) env += Mathf.Exp(-(t - start) * 9f) * (1f - start);
            float n = noise.Next();
            low += (n - low) * 0.05f;
            mid += (n - mid) * 0.3f;
            s[i] = (low * 1.4f + (mid - low) * 0.35f) * env;
        }
        return ToStream(s, 0.85f);
    }

    /// <summary>A hammer knocking on wood: short knock with a couple of woody partials.</summary>
    public static AudioStreamWav Hammer()
    {
        var s = new float[(int)(SampleRate * 0.12f)];
        var noise = new Noise(31);
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float body = Mathf.Sin(Mathf.Tau * 420f * t) * Mathf.Exp(-t * 45f)
                         + Mathf.Sin(Mathf.Tau * 1130f * t) * 0.5f * Mathf.Exp(-t * 70f);
            float click = noise.Next() * Mathf.Exp(-t * 400f);
            s[i] = body * 0.7f + click * 0.5f;
        }
        return ToStream(s, 0.7f);
    }

    /// <summary>A bell: inharmonic partials with long, separate decays.</summary>
    public static AudioStreamWav Bell(float fundamental, float seconds, float brightness = 1f)
    {
        var s = new float[(int)(SampleRate * seconds)];
        (float Ratio, float Amp, float Decay)[] partials =
        {
            (0.5f, 0.35f, 1.2f), (1f, 1f, 2.0f), (2.0f, 0.55f * brightness, 3.0f),
            (2.76f, 0.4f * brightness, 4.5f), (5.4f, 0.2f * brightness, 7f),
        };
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float attack = Mathf.Min(1f, t * 400f);
            float v = 0;
            foreach (var (ratio, amp, decay) in partials)
                v += Mathf.Sin(Mathf.Tau * fundamental * ratio * t) * amp * Mathf.Exp(-t * decay);
            s[i] = v * attack * 0.4f;
        }
        return ToStream(s, 0.6f);
    }

    /// <summary>
    /// Seamless looping countryside ambience: gusting wind (filtered noise) with occasional birdsong.
    /// The loop is made seamless by crossfading the extra tail back into the start.
    /// </summary>
    public static AudioStreamWav Ambience(float seconds = 16f)
    {
        int length = (int)(SampleRate * seconds);
        int fade = SampleRate; // 1 s crossfade
        var s = new float[length + fade];
        var noise = new Noise(97);

        float low = 0, band = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float n = noise.Next();
            low += (n - low) * 0.012f;
            band += (n - band) * 0.04f;
            float gust = 0.55f + 0.3f * Mathf.Sin(Mathf.Tau * t / 7.3f) + 0.15f * Mathf.Sin(Mathf.Tau * t / 2.9f + 1.3f);
            s[i] = (low * 3.2f + (band - low) * 0.6f) * gust * 0.5f;
        }

        // Birds: short rising/falling chirps in little phrases, away from the loop seam.
        var rng = new Noise(5);
        float time = 0.8f;
        while (time < seconds - 1.5f)
        {
            int notes = 2 + (int)((rng.Next() + 1f) * 2f);
            float pitch = 2600f + rng.Next() * 700f;
            for (int k = 0; k < notes; k++)
            {
                float start = time + k * 0.13f;
                float dur = 0.07f + (rng.Next() + 1f) * 0.03f;
                float sweep = (k % 2 == 0 ? 1f : -1f) * 900f;
                int i0 = (int)(start * SampleRate), n = (int)(dur * SampleRate);
                float phase = 0;
                for (int j = 0; j < n && i0 + j < length; j++)
                {
                    float u = j / (float)n;
                    phase += Mathf.Tau * (pitch + sweep * u) / SampleRate;
                    s[i0 + j] += Mathf.Sin(phase) * Mathf.Sin(Mathf.Pi * u) * 0.12f;
                }
            }
            time += 1.6f + (rng.Next() + 1f) * 1.8f;
        }

        var loop = new float[length];
        for (int i = 0; i < length; i++)
        {
            loop[i] = s[i];
            if (i < fade)
            {
                float w = i / (float)fade;
                loop[i] = s[i] * w + s[length + i] * (1f - w);
            }
        }
        var stream = ToStream(loop, 0.5f, edgeFades: false); // fading the edges would cause a dip at the seam
        stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        stream.LoopBegin = 0;
        stream.LoopEnd = length;
        return stream;
    }

    /// <summary>Normalises to <paramref name="peak"/>, applies tiny fades to avoid clicks, and packs 16-bit PCM.</summary>
    private static AudioStreamWav ToStream(float[] samples, float peak, bool edgeFades = true)
    {
        float max = 1e-6f;
        foreach (float v in samples) max = Mathf.Max(max, Mathf.Abs(v));
        float gain = peak / max;
        int edge = edgeFades ? Math.Min(64, samples.Length / 4) : 0;

        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * gain;
            if (i < edge) v *= i / (float)edge;
            if (i >= samples.Length - edge) v *= (samples.Length - 1 - i) / (float)edge;
            short pcm = (short)Mathf.Clamp(Mathf.RoundToInt(v * short.MaxValue), short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            Data = data,
        };
    }
}
