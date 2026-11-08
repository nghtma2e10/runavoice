using System;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// 3B Uzamsal Ses İşleyici (Spatial Panner - Özellik 3):
    /// - Yatay düzlemde azimut θ (-180°..+180°) ve uzaklık d (0..1) parametreleri.
    /// - Sabit güçlü ILD (seviye farkı) + merkez için √2 güç telafisi.
    /// - Woodworth yaklaşımı ile ITD (zaman farkı) + kesirli gecikme halka tamponu.
    /// - Uzak kulak için kafa gölgesi tek kutuplu alçak geçiren filtre (LPF).
    /// - Arka sesler için 4 kHz -4 dB HighShelf filtresi.
    /// - Uzaklık sönümlemesi: gD = 1/(1+1.5d) ve 20 kHz -> 8 kHz mesafe LPF.
    /// - 150 ms yumuşak parametre geçişi (tıkırtısız).
    /// - Hoparlör modu: %50 genişlik ve ITD kapalı.
    /// - Ses iş parçacığında sıfır bellek ayırma (zero-allocation).
    /// </summary>
    public sealed class SpatialPanner
    {
        private const int SampleRate = 48000;
        private const float TwoPi = 6.283185307179586f;
        private const float Pi = 3.141592653589793f;
        private const float DegToRad = Pi / 180.0f;
        private const float Sqrt2 = 1.414213562373095f;
        private const float WoodworthCoeff = 0.0875f / 343.0f; // a / c (sn)

        private const int DelayBufferSize = 64; // Max ITD ~ 32 örnek @ 48kHz
        private const int SmoothingSamples = SampleRate * 150 / 1000; // 150 ms = 7200 örnek
        private const float SlewAzimuthPerSample = 360.0f / SmoothingSamples; // ~0.05° / örnek
        private const float SlewDistancePerSample = 1.0f / SmoothingSamples;

        // Yapılandırma hedefleri
        private volatile bool _enabled = false;
        private volatile bool _speakerMode = false;
        private volatile float _targetAzimuthDeg = 0f;
        private volatile float _targetDistance = 0.2f;

        // Anlık yumuşatılmış durum
        private float _currentAzimuthDeg = 0f;
        private float _currentDistance = 0.2f;

        // ITD gecikme tamponları (L ve R için ayrı 64 örneklik halka)
        private readonly float[] _delayRingL = new float[DelayBufferSize];
        private readonly float[] _delayRingR = new float[DelayBufferSize];
        private int _delayWritePos = 0;

        // Kafa gölgesi tek kutuplu LPF durumları
        private float _shadowLpfStateL = 0f;
        private float _shadowLpfStateR = 0f;

        // Mesafe tek kutuplu LPF durumları
        private float _distLpfStateL = 0f;
        private float _distLpfStateR = 0f;

        // Arka yön HighShelf filtreleri (BiquadFilter)
        private readonly BiquadFilter _backShelfL = new();
        private readonly BiquadFilter _backShelfR = new();

        public bool IsEnabled => _enabled;
        public float TargetAzimuthDeg => _targetAzimuthDeg;
        public float TargetDistance => _targetDistance;

        public SpatialPanner()
        {
            _backShelfL.Configure(BiquadType.HighShelf, SampleRate, 4000f, 0.707f, -4.0f);
            _backShelfR.Configure(BiquadType.HighShelf, SampleRate, 4000f, 0.707f, -4.0f);
        }

        public void Configure(bool enabled, float azimuthDeg, float distance, bool speakerMode)
        {
            _enabled = enabled;
            _speakerMode = speakerMode;
            _targetAzimuthDeg = Math.Clamp(azimuthDeg, -180.0f, 180.0f);
            _targetDistance = Math.Clamp(distance, 0.0f, 1.0f);
        }

        public void Reset()
        {
            _currentAzimuthDeg = _targetAzimuthDeg;
            _currentDistance = _targetDistance;
            Array.Clear(_delayRingL, 0, DelayBufferSize);
            Array.Clear(_delayRingR, 0, DelayBufferSize);
            _delayWritePos = 0;
            _shadowLpfStateL = 0f;
            _shadowLpfStateR = 0f;
            _distLpfStateL = 0f;
            _distLpfStateR = 0f;
            _backShelfL.Reset();
            _backShelfR.Reset();
        }

        /// <summary>
        /// Tek bir mono ses örneğini işleyerek stereo (outL, outR) üretir.
        /// Ses iş parçacığında çağrılır; sıfır bellek ayırma.
        /// </summary>
        public void Process(float sample, out float outL, out float outR)
        {
            if (!_enabled)
            {
                outL = sample;
                outR = sample;
                return;
            }

            // 1. Parametre yumuşatma (150 ms doğrusal geçiş)
            float azDiff = _targetAzimuthDeg - _currentAzimuthDeg;
            if (Math.Abs(azDiff) <= SlewAzimuthPerSample)
            {
                _currentAzimuthDeg = _targetAzimuthDeg;
            }
            else
            {
                _currentAzimuthDeg += Math.Sign(azDiff) * SlewAzimuthPerSample;
            }

            float distDiff = _targetDistance - _currentDistance;
            if (Math.Abs(distDiff) <= SlewDistancePerSample)
            {
                _currentDistance = _targetDistance;
            }
            else
            {
                _currentDistance += Math.Sign(distDiff) * SlewDistancePerSample;
            }

            // Hoparlör modunda uzamsal genişlik %50'ye düşer
            float effectiveAzimuth = _speakerMode ? (_currentAzimuthDeg * 0.5f) : _currentAzimuthDeg;
            float azRad = effectiveAzimuth * DegToRad;

            // 2. ILD (Seviye farkı - Sabit güçlü kaydırma)
            // p = sin(θ) ∈ [-1, 1], gL = cos((p+1)·π/4), gR = sin((p+1)·π/4)
            // Merkezde gL=gR=cos(π/4)=1/√2. Merkez telafisi ×√2 ile merkezde L=R=sample olur.
            float p = MathF.Sin(azRad);
            float panAngle = (p + 1.0f) * (Pi * 0.25f); // 0 .. π/2
            float gL = MathF.Cos(panAngle) * Sqrt2;
            float gR = MathF.Sin(panAngle) * Sqrt2;

            // 3. Uzaklık sönümlemesi ve mesafe LPF'i
            // gD = 1 / (1 + 1.5·d); kesim fc = 20000 - 12000·d (20kHz -> 8kHz)
            float gD = 1.0f / (1.0f + (1.5f * _currentDistance));
            float distFc = 20000.0f - (12000.0f * _currentDistance);
            float distAlpha = MathF.Exp(-TwoPi * distFc / SampleRate);

            // Giriş örneğine mesafe sönümü uygula
            float sig = sample * gD;

            // Mesafe alçak geçiren (her iki kulağa)
            _distLpfStateL = ((1.0f - distAlpha) * sig) + (distAlpha * _distLpfStateL);
            _distLpfStateR = ((1.0f - distAlpha) * sig) + (distAlpha * _distLpfStateR);
            float earL = _distLpfStateL;
            float earR = _distLpfStateR;

            // 4. Arka/ön ayrımı: |θ| > 90° ise her iki kulağa HighShelf 4 kHz -4 dB
            if (Math.Abs(effectiveAzimuth) > 90.0f)
            {
                earL = _backShelfL.Process(earL);
                earR = _backShelfR.Process(earR);
            }

            // 5. Kafa gölgesi (Uzak kulağa tek kutuplu LPF)
            // fc = 20000 - 16500·|sin θ| Hz (yan tarafta ~3.5 kHz)
            float shadowFc = Math.Max(2000.0f, 20000.0f - (16500.0f * MathF.Abs(p)));
            float shadowAlpha = MathF.Exp(-TwoPi * shadowFc / SampleRate);

            if (p > 0.001f) // Ses sağda -> Sol kulak uzak kulaktır
            {
                _shadowLpfStateL = ((1.0f - shadowAlpha) * earL) + (shadowAlpha * _shadowLpfStateL);
                earL = _shadowLpfStateL;
            }
            else if (p < -0.001f) // Ses solda -> Sağ kulak uzak kulaktır
            {
                _shadowLpfStateR = ((1.0f - shadowAlpha) * earR) + (shadowAlpha * _shadowLpfStateR);
                earR = _shadowLpfStateR;
            }

            // 6. ITD (Zaman farkı - Woodworth modeli)
            // Woodworth: itd = (a/c)·(sin|θ'| + |θ'|), en fazla ~0.66 ms ≈ 31.5 örnek
            // Hoparlör modunda ITD kapalıdır.
            float delaySamples = 0f;
            if (!_speakerMode)
            {
                // θ' = azimutun ön yarı düzleme yansıması (|θ'| ≤ 90°)
                float absAz = Math.Abs(effectiveAzimuth);
                float thetaPrimeDeg = absAz <= 90.0f ? absAz : (180.0f - absAz);
                float thetaPrimeRad = thetaPrimeDeg * DegToRad;

                float itdSec = WoodworthCoeff * (MathF.Sin(thetaPrimeRad) + thetaPrimeRad);
                delaySamples = Math.Clamp(itdSec * SampleRate, 0f, 32.0f);
            }

            // Gecikme halka tamponuna yaz
            _delayRingL[_delayWritePos] = earL;
            _delayRingR[_delayWritePos] = earR;

            float delayedL = earL;
            float delayedR = earR;

            if (delaySamples > 0.05f)
            {
                float readPos = _delayWritePos - delaySamples;
                while (readPos < 0f) readPos += DelayBufferSize;
                int idx0 = (int)readPos % DelayBufferSize;
                int idx1 = (idx0 + 1) % DelayBufferSize;
                float frac = readPos - (int)readPos;

                if (p > 0.001f) // Ses sağda -> Sol kulağa gecikme uygula
                {
                    delayedL = _delayRingL[idx0] + (frac * (_delayRingL[idx1] - _delayRingL[idx0]));
                }
                else if (p < -0.001f) // Ses solda -> Sağ kulağa gecikme uygula
                {
                    delayedR = _delayRingR[idx0] + (frac * (_delayRingR[idx1] - _delayRingR[idx0]));
                }
            }

            _delayWritePos = (_delayWritePos + 1) % DelayBufferSize;

            // 7. Nihai ILD kazançları ile stereo çıkışı oluştur
            outL = delayedL * gL;
            outR = delayedR * gR;
        }
    }
}
