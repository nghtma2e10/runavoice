using System;
using RNNoise.NET;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Çoklu Motorlu Gürültü Engelleme İşlemcisi (RNNoise C Native RNN + Derin Hibrit Spektral Kapı + Hafif DSP).
    /// Mozilla RNNoise için zorunlu olan [-32768.0f, +32767.0f] ölçeklemesini ve taşmasız 480-örnek (10ms @ 48kHz)
    /// dairesel FIFO tamponlamasını eksiksiz uygular.
    /// </summary>
    public sealed class RnnoiseFilter : IDisposable
    {
        public const int FrameSize = 480; // 48kHz @ 10ms

        private IntPtr _state = IntPtr.Zero;
        private readonly object _sync = new();

        private readonly float[] _inFifo = new float[FrameSize];
        private readonly float[] _rnnIn = new float[FrameSize];
        private readonly float[] _rnnOut = new float[FrameSize];
        private readonly float[] _outQueue = new float[16384];

        private int _inPos = 0;
        private int _outReadPos = 0;
        private int _outWritePos = 0;
        private int _outCount = 0;

        private float _smoothVadGate = 1.0f;
        private float _noiseFloorEnv = 0.002f;
        private bool _disposed = false;

        public bool IsAvailable => _state != IntPtr.Zero;
        public float LastVadProbability { get; private set; } = 0f;

        // "rnnoise" | "deep" | "speex" | "off"
        public string EngineMode { get; set; } = "rnnoise";
        // 1..5
        public int SuppressionLevel { get; set; } = 3;

        public RnnoiseFilter()
        {
            try
            {
                _state = Native.rnnoise_create(IntPtr.Zero);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RnnoiseFilter] Native DLL başlatılamadı (DSP Fallback devrede): {ex.Message}");
                _state = IntPtr.Zero;
            }
        }

        /// <summary>
        /// [-1.0f, +1.0f] aralığındaki 48kHz mono örnekleri yerinde temizler.
        /// Paket boyutu 480'in katı olmasa bile dairesel kuyruk sayesinde asla taşma (IndexOutOfRange) yapmaz.
        /// </summary>
        public unsafe void ProcessInPlace(float[] samples, int count)
        {
            string mode = (EngineMode ?? "rnnoise").ToLowerInvariant();
            if (mode == "off")
            {
                LastVadProbability = ComputeSimpleEnergyVad(samples, count);
                return;
            }

            int level = Math.Clamp(SuppressionLevel, 1, 5);
            // Wet/Dry karışım oranı: Kademe 1=%50, 2=%70, 3=%86, 4=%95, 5=%100
            float wetMix = level switch
            {
                1 => 0.50f,
                2 => 0.70f,
                3 => 0.86f,
                4 => 0.95f,
                _ => 1.00f
            };

            if (mode == "speex" || _state == IntPtr.Zero)
            {
                ProcessLightweightDspGate(samples, count, level);
                return;
            }

            lock (_sync)
            {
                if (_disposed || _state == IntPtr.Zero) return;

                fixed (float* rnnInPtr = _rnnIn)
                fixed (float* rnnOutPtr = _rnnOut)
                {
                    for (int i = 0; i < count; i++)
                    {
                        _inFifo[_inPos++] = samples[i];

                        if (_inPos == FrameSize)
                        {
                            // KRİTİK: Mozilla RNNoise C kütüphanesi [-32768.0f, +32767.0f] ölçeği bekler!
                            for (int k = 0; k < FrameSize; k++)
                            {
                                _rnnIn[k] = Math.Clamp(_inFifo[k] * 32768.0f, -32768.0f, 32767.0f);
                            }

                            float vad = Native.rnnoise_process_frame(_state, rnnOutPtr, rnnInPtr);
                            LastVadProbability = Math.Clamp(vad, 0f, 1f);

                            // "deep" modunda veya yüksek kademede ek spektral VAD kapısı uygula
                            float targetGate = 1.0f;
                            if (mode == "deep" || level >= 4)
                            {
                                float vadThreshold = mode == "deep" ? (0.18f + level * 0.06f) : (0.12f + (level - 3) * 0.08f);
                                if (vad < vadThreshold)
                                {
                                    targetGate = mode == "deep" ? 0.02f : 0.12f;
                                }
                            }

                            for (int k = 0; k < FrameSize; k++)
                            {
                                // Yumuşak zarf geçişi: 2ms atak, 90ms bırakma (kelime sonlarını yutmaz)
                                float coeff = targetGate > _smoothVadGate ? 0.88f : 0.9975f;
                                _smoothVadGate = coeff * _smoothVadGate + (1f - coeff) * targetGate;

                                float cleanNorm = (_rnnOut[k] / 32768.0f) * _smoothVadGate;
                                float rawNorm = _inFifo[k];
                                float blended = cleanNorm * wetMix + rawNorm * (1f - wetMix);

                                EnqueueOutput(Math.Clamp(blended, -1.0f, 1.0f));
                            }

                            _inPos = 0;
                        }
                    }
                }

                // Çıkış kuyruğundan temizlenmiş örnekleri geri yaz (ilk 10ms dolana kadar yumuşak sıfır verir)
                for (int i = 0; i < count; i++)
                {
                    samples[i] = DequeueOutput();
                }
            }
        }

        private void ProcessLightweightDspGate(float[] samples, int count, int level)
        {
            float rmsSum = 0f;
            for (int i = 0; i < count; i++) rmsSum += samples[i] * samples[i];
            float rms = count > 0 ? MathF.Sqrt(rmsSum / count) : 0f;

            // Adaptif taban gürültüsü tahmini
            if (rms < _noiseFloorEnv * 1.5f)
                _noiseFloorEnv = 0.98f * _noiseFloorEnv + 0.02f * rms;
            else
                _noiseFloorEnv = 0.999f * _noiseFloorEnv + 0.001f * rms;

            _noiseFloorEnv = Math.Clamp(_noiseFloorEnv, 0.0005f, 0.04f);
            float threshold = _noiseFloorEnv * (1.8f + level * 0.65f);
            LastVadProbability = Math.Clamp((rms - _noiseFloorEnv) / Math.Max(0.01f, threshold * 2f), 0f, 1f);

            float targetGain = rms >= threshold ? 1.0f : Math.Max(0.05f, 1.0f - level * 0.19f);
            for (int i = 0; i < count; i++)
            {
                float coeff = targetGain > _smoothVadGate ? 0.92f : 0.998f;
                _smoothVadGate = coeff * _smoothVadGate + (1f - coeff) * targetGain;
                samples[i] *= _smoothVadGate;
            }
        }

        private static float ComputeSimpleEnergyVad(float[] samples, int count)
        {
            if (count <= 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < count; i++) sum += samples[i] * samples[i];
            float rms = MathF.Sqrt(sum / count);
            return Math.Clamp(rms * 12.0f, 0f, 1f);
        }

        private void EnqueueOutput(float val)
        {
            if (_outCount >= _outQueue.Length)
            {
                // Taşma koruması: en eski örneği atla
                _outReadPos = (_outReadPos + 1) % _outQueue.Length;
                _outCount--;
            }
            _outQueue[_outWritePos] = val;
            _outWritePos = (_outWritePos + 1) % _outQueue.Length;
            _outCount++;
        }

        private float DequeueOutput()
        {
            if (_outCount <= 0) return 0f;
            float val = _outQueue[_outReadPos];
            _outReadPos = (_outReadPos + 1) % _outQueue.Length;
            _outCount--;
            return val;
        }

        public void ResetBuffers()
        {
            lock (_sync)
            {
                _inPos = 0;
                _outReadPos = 0;
                _outWritePos = 0;
                _outCount = 0;
                _smoothVadGate = 1.0f;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (_state != IntPtr.Zero)
                    {
                        try { Native.rnnoise_destroy(_state); } catch { }
                        _state = IntPtr.Zero;
                    }
                }
            }
            GC.SuppressFinalize(this);
        }

        ~RnnoiseFilter()
        {
            Dispose();
        }
    }
}
