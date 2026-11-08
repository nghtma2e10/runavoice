using System;

namespace RunaVoice_WinUI.Services
{
    public enum BiquadType
    {
        LowPass,
        HighPass,
        PeakingEQ,
        LowShelf,
        HighShelf
    }

    /// <summary>
    /// Gerçek 2. Derece IIR Audio-EQ-Cookbook Biquad Filtresi.
    /// 48kHz gerçek zamanlı ses işleme için sıfır bellek tahsisiyle çalışır.
    /// </summary>
    public sealed class BiquadFilter
    {
        private float _b0 = 1f, _b1 = 0f, _b2 = 0f, _a1 = 0f, _a2 = 0f;
        private float _z1 = 0f, _z2 = 0f;

        public void Configure(BiquadType type, float sampleRate, float frequency, float q, float gainDb)
        {
            frequency = Math.Clamp(frequency, 20f, sampleRate * 0.48f);
            q = Math.Max(0.1f, q);

            double w0 = 2.0 * Math.PI * frequency / sampleRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            double alpha = sinW0 / (2.0 * q);
            double A = Math.Pow(10.0, gainDb / 40.0);

            double b0 = 1, b1 = 0, b2 = 0, a0 = 1, a1 = 0, a2 = 0;

            switch (type)
            {
                case BiquadType.HighPass:
                    b0 = (1.0 + cosW0) * 0.5;
                    b1 = -(1.0 + cosW0);
                    b2 = (1.0 + cosW0) * 0.5;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.LowPass:
                    b0 = (1.0 - cosW0) * 0.5;
                    b1 = 1.0 - cosW0;
                    b2 = (1.0 - cosW0) * 0.5;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;

                case BiquadType.PeakingEQ:
                    b0 = 1.0 + alpha * A;
                    b1 = -2.0 * cosW0;
                    b2 = 1.0 - alpha * A;
                    a0 = 1.0 + alpha / A;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha / A;
                    break;

                case BiquadType.LowShelf:
                {
                    double twoSqrtAAlpha = 2.0 * Math.Sqrt(A) * alpha;
                    b0 = A * ((A + 1.0) - (A - 1.0) * cosW0 + twoSqrtAAlpha);
                    b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosW0);
                    b2 = A * ((A + 1.0) - (A - 1.0) * cosW0 - twoSqrtAAlpha);
                    a0 = (A + 1.0) + (A - 1.0) * cosW0 + twoSqrtAAlpha;
                    a1 = -2.0 * ((A - 1.0) + (A + 1.0) * cosW0);
                    a2 = (A + 1.0) + (A - 1.0) * cosW0 - twoSqrtAAlpha;
                    break;
                }

                case BiquadType.HighShelf:
                {
                    double twoSqrtAAlpha = 2.0 * Math.Sqrt(A) * alpha;
                    b0 = A * ((A + 1.0) + (A - 1.0) * cosW0 + twoSqrtAAlpha);
                    b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosW0);
                    b2 = A * ((A + 1.0) + (A - 1.0) * cosW0 - twoSqrtAAlpha);
                    a0 = (A + 1.0) - (A - 1.0) * cosW0 + twoSqrtAAlpha;
                    a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cosW0);
                    a2 = (A + 1.0) - (A - 1.0) * cosW0 - twoSqrtAAlpha;
                    break;
                }
            }

            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
        }

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;

            if (!float.IsFinite(y))
            {
                _z1 = 0f;
                _z2 = 0f;
                return 0f;
            }
            return y;
        }

        public void Reset()
        {
            _z1 = 0f;
            _z2 = 0f;
        }

        public static (float eq80, float eq300, float eq1k, float eq35k, float eq8k) GetPresetBands(string profile)
        {
            return (profile ?? "clean").ToLowerInvariant() switch
            {
                "clean"   => (-3.0f, -1.5f, +1.5f, +3.0f, +2.0f), // Gürültüsüz Temiz
                "podcast" => (+3.5f, +1.0f, -1.0f, +3.5f, +2.5f), // Podcast / Radyo
                "clarity" => (-4.0f, -2.0f, +2.0f, +5.5f, +4.5f), // Net Tiz
                "deep"    => (+5.5f, +2.5f, -0.5f, +1.0f, +0.5f), // Derin Bas
                "flat"    => (0.0f,  0.0f,  0.0f,  0.0f,  0.0f),  // Doğal Saf
                _         => (-2.0f, -1.0f, +1.5f, +3.0f, +2.0f)
            };
        }
    }

    /// <summary>
    /// Kulak Koruyucu Dinamik Tavan Sınırlayıcı (Max dB Brickwall Soft-Knee Limiter).
    /// Ani çığlık, mikrofon düşmesi veya %200 ses artırmada işitme sağlığını korur.
    /// </summary>
    public sealed class EarProtectionLimiter
    {
        private float _envelope = 0f;
        private float _ceilingLinear = 0.794f; // -2 dB default

        public void SetCeilingDb(float ceilingDb)
        {
            ceilingDb = Math.Clamp(ceilingDb, -24.0f, 0.0f);
            _ceilingLinear = (float)Math.Pow(10.0, ceilingDb / 20.0);
        }

        public float Process(float sample)
        {
            float abs = Math.Abs(sample);
            // Hızlı atak (0.2ms), yumuşak bırakma (60ms) @ 48kHz
            float coeff = abs > _envelope ? 0.90f : 0.9993f;
            _envelope = coeff * _envelope + (1f - coeff) * abs;
            if (abs > _envelope) _envelope = abs;

            if (_envelope > _ceilingLinear && _envelope > 1e-6f)
            {
                float gain = _ceilingLinear / _envelope;
                sample *= gain;
            }

            // Son güvenlik soft-clip
            if (sample > _ceilingLinear) sample = _ceilingLinear;
            else if (sample < -_ceilingLinear) sample = -_ceilingLinear;

            return sample;
        }

        /// <summary>
        /// Stereo tavan sınırlayıcı: tepe değer max(|l|, |r|) üzerinden tek zarf ile hesaplanır;
        /// her iki kanala eşit kazanç uygulanarak stereo ses sahnesi (stereo image) kayması önlenir.
        /// </summary>
        public void ProcessStereo(ref float l, ref float r)
        {
            float peak = Math.Max(Math.Abs(l), Math.Abs(r));
            float coeff = peak > _envelope ? 0.90f : 0.9993f;
            _envelope = (coeff * _envelope) + ((1f - coeff) * peak);
            if (peak > _envelope) _envelope = peak;

            if (_envelope > _ceilingLinear && _envelope > 1e-6f)
            {
                float gain = _ceilingLinear / _envelope;
                l *= gain;
                r *= gain;
            }

            if (l > _ceilingLinear) l = _ceilingLinear;
            else if (l < -_ceilingLinear) l = -_ceilingLinear;

            if (r > _ceilingLinear) r = _ceilingLinear;
            else if (r < -_ceilingLinear) r = -_ceilingLinear;
        }
    }
}
