using System;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Ses Dengeleme Modu (Loudness Leveler - Özellik 2):
    /// - Uzak katılımcıların konuşma seviyelerini ölçüp hedef desibele doğru dengeler.
    /// - K-ağırlıklı yan zincir (HP 100Hz + HighShelf 1500Hz +4dB).
    /// - 10 ms (480 örnek) bloklarla konuşma kapısı (RMS > -50 dBFS ve vokal tespiti).
    /// - Kapı kapalıyken kazanç dondurulur; sessizlik veya gürültü yükseltilmez.
    /// - Yükselirken en fazla 3 dB/sn, düşerken en fazla 12 dB/sn hız sınırı (zipper gürültüsü yok).
    /// - Kanal içi -1 dBFS yumuşak sınırlayıcı (atak 1 ms, bırakma 80 ms).
    /// </summary>
    public sealed class LoudnessLeveler
    {
        private const int SampleRate = 48000;
        private const int BlockSize = 480; // 10 ms @ 48 kHz
        private const float CeilingLinear = 0.89125f; // -1 dBFS

        // Yan zincir K-ağırlık filtreleri
        private readonly BiquadFilter _sidechainHp = new();
        private readonly BiquadFilter _sidechainShelf = new();

        // Yapılandırma
        private volatile bool _enabled = true;
        private volatile bool _excluded = false;
        private volatile float _targetDb = -22.0f;
        private volatile float _maxBoostDb = 12.0f;
        private volatile float _maxCutDb = 10.0f;

        // Ölçüm ve tahmin durumu
        private int _blockSampleCount = 0;
        private float _blockSumSq = 0f;
        private float _levelDb = -22.0f;
        private float _targetGainDb = 0f;
        private float _currentGainDb = 0f;
        private float _currentLinearGain = 1.0f;

        private int _speechSampleCount = 0; // İlk 1.5 sn hızlı başlangıç için sayaç

        // Kanal limiteri durumu (1 ms atak, 80 ms bırakma)
        private float _limiterEnvelope = 0f;
        private static readonly float LimiterAttackCoeff = MathF.Exp(-1.0f / (0.001f * SampleRate));
        private static readonly float LimiterReleaseCoeff = MathF.Exp(-1.0f / (0.080f * SampleRate));

        // Slew-rate hız sınırları (örnek başına dB)
        private static readonly float MaxRisePerSample = 3.0f / SampleRate;  // +3 dB / sn
        private static readonly float MaxFallPerSample = 12.0f / SampleRate; // -12 dB / sn

        public float CurrentGainDb => _currentGainDb;

        public LoudnessLeveler()
        {
            _sidechainHp.Configure(BiquadType.HighPass, SampleRate, 100f, 0.7071f, 0f);
            _sidechainShelf.Configure(BiquadType.HighShelf, SampleRate, 1500f, 0.7071f, 4.0f);
        }

        public void Configure(string mode, float targetDb, bool excluded)
        {
            _excluded = excluded;
            _targetDb = Math.Clamp(targetDb, -30.0f, -10.0f);

            switch ((mode ?? "normal").ToLowerInvariant())
            {
                case "light":
                    _enabled = true;
                    _maxBoostDb = 6.0f;
                    _maxCutDb = 6.0f;
                    break;

                case "strong":
                    _enabled = true;
                    _maxBoostDb = 18.0f;
                    _maxCutDb = 14.0f;
                    break;

                case "off":
                    _enabled = false;
                    _maxBoostDb = 0f;
                    _maxCutDb = 0f;
                    break;

                case "normal":
                default:
                    _enabled = true;
                    _maxBoostDb = 12.0f;
                    _maxCutDb = 10.0f;
                    break;
            }
        }

        public void Reset()
        {
            _blockSampleCount = 0;
            _blockSumSq = 0f;
            _levelDb = _targetDb;
            _targetGainDb = 0f;
            _currentGainDb = 0f;
            _currentLinearGain = 1.0f;
            _speechSampleCount = 0;
            _limiterEnvelope = 0f;
            _sidechainHp.Reset();
            _sidechainShelf.Reset();
        }

        /// <summary>
        /// Tek bir ses örneğini işler (ses iş parçacığında sıfır bellek ayırma).
        /// </summary>
        public float Process(float sample, bool isChannelSpeaking)
        {
            if (!_enabled || _excluded)
            {
                // Dengeleme kapalıysa veya eş hariç tutulduysa doğrudan döner
                return sample;
            }

            // 1. K-ağırlıklı yan zincir analizi (10 ms blok RMS)
            float filtered = _sidechainShelf.Process(_sidechainHp.Process(sample));
            _blockSumSq += filtered * filtered;
            _blockSampleCount++;

            if (_blockSampleCount >= BlockSize)
            {
                float rms = MathF.Sqrt(_blockSumSq / BlockSize);
                _blockSumSq = 0f;
                _blockSampleCount = 0;

                float blockDb = 20.0f * MathF.Log10(Math.Max(rms, 1e-5f));

                // 2. Konuşma kapısı: blok > -50 dBFS VE kanal konuşuyor
                if (blockDb > -50.0f && isChannelSpeaking)
                {
                    _speechSampleCount += BlockSize;
                    // İlk 1.5 sn hızlı başlangıç (tau = 0.5s), sonrası tau = 3.0s
                    float tau = _speechSampleCount < (SampleRate * 1.5f) ? 0.5f : 3.0f;
                    float dt = (float)BlockSize / SampleRate;
                    float alpha = 1.0f - MathF.Exp(-dt / tau);

                    _levelDb = ((1.0f - alpha) * _levelDb) + (alpha * blockDb);

                    // 4. Hedef kazanç: clamp(TargetDb - levelDb, -MaxCutDb, +MaxBoostDb)
                    _targetGainDb = Math.Clamp(_targetDb - _levelDb, -_maxCutDb, _maxBoostDb);
                }
                // Kapı kapalıyken kazanç donar (_targetGainDb değişmez)
            }

            // 5. Kazanç yumuşatma (örnek başına hız sınırı: +3 dB/sn, -12 dB/sn)
            if (_currentGainDb < _targetGainDb)
            {
                _currentGainDb = Math.Min(_targetGainDb, _currentGainDb + MaxRisePerSample);
                _currentLinearGain = MathF.Pow(10.0f, _currentGainDb / 20.0f);
            }
            else if (_currentGainDb > _targetGainDb)
            {
                _currentGainDb = Math.Max(_targetGainDb, _currentGainDb - MaxFallPerSample);
                _currentLinearGain = MathF.Pow(10.0f, _currentGainDb / 20.0f);
            }

            float amplified = sample * _currentLinearGain;

            // 6. Kanal içi yumuşak sınırlayıcı (tavan: -1 dBFS, atak: 1 ms, bırakma: 80 ms)
            float abs = Math.Abs(amplified);
            float coeff = abs > _limiterEnvelope ? LimiterAttackCoeff : LimiterReleaseCoeff;
            _limiterEnvelope = (coeff * _limiterEnvelope) + ((1.0f - coeff) * abs);
            if (abs > _limiterEnvelope) _limiterEnvelope = abs;

            if (_limiterEnvelope > CeilingLinear && _limiterEnvelope > 1e-5f)
            {
                amplified *= (CeilingLinear / _limiterEnvelope);
            }

            if (amplified > CeilingLinear) amplified = CeilingLinear;
            else if (amplified < -CeilingLinear) amplified = -CeilingLinear;

            return amplified;
        }
    }
}
