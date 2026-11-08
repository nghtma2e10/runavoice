using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services
{
    public enum UiSoundEffect
    {
        RoomJoin,
        RoomLeave,
        PeerJoin,
        PeerLeave,
        Mute,
        Unmute,
        Deafen,
        Undeafen,
        ScreenShareStart,
        ScreenShareStop,
        ButtonHover,
        ButtonClick,
        MessageSent,
        MessageReceived
    }

    /// <summary>
    /// Yerleşik Sentezleyici Ses Efektleri + Özel Ses Paneli (.mp3/.wav/.ogg) Yöneticisi.
    /// Hem yerel kulaklığa hem de (açıksa) WebRTC üzerinden odadaki herkese 48kHz kristal netliğinde ses basar.
    /// </summary>
    public sealed class SoundpadService
    {
        private readonly AudioEngine _audio;
        private readonly StorageService _storage;

        public List<CustomSoundItem> BuiltInSounds { get; } = new()
        {
            new CustomSoundItem { Id = "sp_airhorn",  Title = "Hava Kornası",     Emoji = "📢", IsBuiltIn = true, BuiltInSynthId = "airhorn" },
            new CustomSoundItem { Id = "sp_victory",  Title = "Zafer Fanfarı",    Emoji = "🏆", IsBuiltIn = true, BuiltInSynthId = "victory" },
            new CustomSoundItem { Id = "sp_rimshot",  Title = "Ba-Dum-Tss",       Emoji = "🥁", IsBuiltIn = true, BuiltInSynthId = "rimshot" },
            new CustomSoundItem { Id = "sp_boom",     Title = "Sinematik Boom",   Emoji = "💥", IsBuiltIn = true, BuiltInSynthId = "boom" },
            new CustomSoundItem { Id = "sp_levelup",  Title = "Seviye Atlama",    Emoji = "✨", IsBuiltIn = true, BuiltInSynthId = "levelup" },
            new CustomSoundItem { Id = "sp_laser",    Title = "Lazer Efekti",     Emoji = "⚡", IsBuiltIn = true, BuiltInSynthId = "laser" },
            new CustomSoundItem { Id = "sp_buzzer",   Title = "Yanlış / Buzzer",  Emoji = "🚨", IsBuiltIn = true, BuiltInSynthId = "buzzer" },
            new CustomSoundItem { Id = "sp_chime",    Title = "Kristal Çan",      Emoji = "🔔", IsBuiltIn = true, BuiltInSynthId = "chime" }
        };

        public SoundpadService(AudioEngine audio, StorageService storage)
        {
            _audio = audio;
            _storage = storage;
        }

        public List<CustomSoundItem> GetAllSounds(string? searchQuery = null)
        {
            var all = new List<CustomSoundItem>(BuiltInSounds.Count + _storage.Settings.CustomSounds.Count);
            all.AddRange(BuiltInSounds);
            all.AddRange(_storage.Settings.CustomSounds);

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string q = searchQuery.Trim();
                all.RemoveAll(s => !s.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            return all;
        }

        public void StopAll()
        {
            _audio.ClearAuxClips();
        }

        public void PlayUiEffect(UiSoundEffect effect)
        {
            if (!_storage.Settings.UiSoundsEnabled) return;

            float[] samples = effect switch
            {
                UiSoundEffect.RoomJoin         => SynthSoftUiChime(587.33f, 880.00f, 0.055f, 0.11f, 0.045f),
                UiSoundEffect.RoomLeave        => SynthSoftUiChime(587.33f, 392.00f, 0.055f, 0.11f, 0.045f),
                UiSoundEffect.PeerJoin         => SynthSoftUiChime(659.25f, 987.77f, 0.045f, 0.09f, 0.040f),
                UiSoundEffect.PeerLeave        => SynthSoftUiChime(523.25f, 392.00f, 0.045f, 0.09f, 0.040f),
                UiSoundEffect.Mute             => SynthSoftUiChime(440.00f, 329.63f, 0.035f, 0.055f, 0.038f),
                UiSoundEffect.Unmute           => SynthSoftUiChime(329.63f, 493.88f, 0.035f, 0.055f, 0.038f),
                UiSoundEffect.Deafen           => SynthSoftUiChime(392.00f, 261.63f, 0.040f, 0.065f, 0.040f),
                UiSoundEffect.Undeafen         => SynthSoftUiChime(261.63f, 523.25f, 0.040f, 0.065f, 0.040f),
                UiSoundEffect.ScreenShareStart => SynthSoftUiChime(523.25f, 783.99f, 0.050f, 0.10f, 0.045f),
                UiSoundEffect.ScreenShareStop  => SynthSoftUiChime(659.25f, 440.00f, 0.050f, 0.09f, 0.042f),
                UiSoundEffect.ButtonHover      => SynthTick(1350f, 0.012f, 0.018f),
                UiSoundEffect.ButtonClick      => SynthClick(820f, 0.024f, 0.042f),
                UiSoundEffect.MessageSent      => SynthSoftUiChime(587.33f, 880.00f, 0.035f, 0.07f, 0.038f),
                UiSoundEffect.MessageReceived  => SynthSoftUiChime(783.99f, 1046.50f, 0.040f, 0.09f, 0.045f),
                _                              => Array.Empty<float>()
            };

            if (samples.Length > 0)
            {
                _audio.PlayAuxClip(samples, broadcastToRoom: false);
            }
        }

        public bool PlaySoundpadItem(CustomSoundItem item)
        {
            float volScale = Math.Clamp(_storage.Settings.SoundpadVolumePercent / 100f, 0f, 1.5f);
            bool broadcast = _storage.Settings.SoundpadBroadcastToRoom;

            float[]? pcm48k = item.IsBuiltIn
                ? SynthesizeSoundpadClip(item.BuiltInSynthId)
                : LoadAudioFileTo48kMono(item.FilePath);

            if (pcm48k == null || pcm48k.Length == 0) return false;

            if (Math.Abs(volScale - 1.0f) > 0.001f)
            {
                for (int i = 0; i < pcm48k.Length; i++)
                {
                    pcm48k[i] = Math.Clamp(pcm48k[i] * volScale, -0.98f, 0.98f);
                }
            }

            _audio.PlayAuxClip(pcm48k, broadcastToRoom: broadcast);
            return true;
        }

        private static float[]? LoadAudioFileTo48kMono(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

                using var reader = new AudioFileReader(filePath);
                ISampleProvider provider = reader;

                if (provider.WaveFormat.Channels == 2)
                {
                    provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
                }
                else if (provider.WaveFormat.Channels > 2)
                {
                    var mux = new MultiplexingSampleProvider(new[] { provider }, 1);
                    provider = mux;
                }

                if (provider.WaveFormat.SampleRate != 48000)
                {
                    provider = new WdlResamplingSampleProvider(provider, 48000);
                }

                // Maksimum 20 saniye ile sınırla (RAM koruması)
                int maxSamples = 48000 * 20;
                var list = new List<float>(48000 * 4);
                float[] buf = new float[4096];
                int read;
                while (list.Count < maxSamples && (read = provider.Read(buf, 0, buf.Length)) > 0)
                {
                    int toTake = Math.Min(read, maxSamples - list.Count);
                    for (int i = 0; i < toTake; i++) list.Add(buf[i]);
                }

                return list.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static float[] SynthesizeSoundpadClip(string id)
        {
            const int sr = 48000;
            var rng = new Random(42);

            switch (id)
            {
                case "airhorn":
                {
                    int len = (int)(sr * 0.85f);
                    var b = new float[len];
                    for (int i = 0; i < len; i++)
                    {
                        float t = (float)i / sr;
                        // 3 kısa korna darbesi + son uzun darbe
                        bool gate = (t < 0.11f) || (t > 0.15f && t < 0.26f) || (t > 0.30f);
                        if (!gate) continue;
                        float env = t > 0.65f ? Math.Max(0f, 1f - (t - 0.65f) / 0.20f) : 1f;
                        float s = MathF.Sin(2 * MathF.PI * 392f * t)
                                + 0.7f * MathF.Sin(2 * MathF.PI * 493.88f * t)
                                + 0.5f * MathF.Sin(2 * MathF.PI * 784f * t)
                                + 0.3f * MathF.Sin(2 * MathF.PI * 1174f * t);
                        b[i] = MathF.Tanh(s * 1.4f) * 0.42f * env;
                    }
                    return b;
                }

                case "victory":
                    return SynthArpeggio(new[] { 523.25f, 659.25f, 783.99f, 1046.50f }, 0.14f, 0.42f);

                case "levelup":
                    return SynthArpeggio(new[] { 440f, 554.37f, 659.25f, 880f, 1108.73f }, 0.08f, 0.38f);

                case "rimshot":
                {
                    int len = (int)(sr * 0.90f);
                    var b = new float[len];
                    for (int i = 0; i < len; i++)
                    {
                        float t = (float)i / sr;
                        float sample = 0f;
                        // Ba (Snare/Tom 1)
                        if (t < 0.16f)
                        {
                            float env = MathF.Exp(-t * 28f);
                            sample += MathF.Sin(2 * MathF.PI * (185f - t * 300f) * t) * env * 0.6f;
                        }
                        // Dum (Low Tom 2)
                        if (t >= 0.18f && t < 0.38f)
                        {
                            float lt = t - 0.18f;
                            float env = MathF.Exp(-lt * 22f);
                            sample += MathF.Sin(2 * MathF.PI * (120f - lt * 180f) * lt) * env * 0.65f;
                        }
                        // Tss (Cymbal)
                        if (t >= 0.40f)
                        {
                            float ct = t - 0.40f;
                            float env = MathF.Exp(-ct * 7.5f);
                            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                            sample += noise * env * 0.35f;
                        }
                        b[i] = Math.Clamp(sample, -0.9f, 0.9f);
                    }
                    return b;
                }

                case "boom":
                {
                    int len = (int)(sr * 1.15f);
                    var b = new float[len];
                    for (int i = 0; i < len; i++)
                    {
                        float t = (float)i / sr;
                        float env = MathF.Exp(-t * 3.8f);
                        float freq = 95f * MathF.Exp(-t * 2.4f) + 36f;
                        float sub = MathF.Sin(2 * MathF.PI * freq * t);
                        float punch = t < 0.06f ? (float)(rng.NextDouble() * 2 - 1) * (1f - t / 0.06f) * 0.4f : 0f;
                        b[i] = MathF.Tanh((sub * 1.5f + punch) * env) * 0.58f;
                    }
                    return b;
                }

                case "laser":
                {
                    int len = (int)(sr * 0.38f);
                    var b = new float[len];
                    float phase = 0f;
                    for (int i = 0; i < len; i++)
                    {
                        float t = (float)i / sr;
                        float freq = 1600f * MathF.Exp(-t * 7.5f) + 110f;
                        phase += 2 * MathF.PI * freq / sr;
                        float env = 1f - (t / 0.38f);
                        b[i] = MathF.Sin(phase) * env * 0.42f;
                    }
                    return b;
                }

                case "buzzer":
                {
                    int len = (int)(sr * 0.55f);
                    var b = new float[len];
                    for (int i = 0; i < len; i++)
                    {
                        float t = (float)i / sr;
                        float env = t > 0.42f ? (0.55f - t) / 0.13f : 1f;
                        float saw = 2f * ((t * 145f) - MathF.Floor(0.5f + t * 145f));
                        float saw2 = 2f * ((t * 152f) - MathF.Floor(0.5f + t * 152f));
                        b[i] = (saw + saw2) * 0.22f * env;
                    }
                    return b;
                }

                default: // chime
                    return SynthTwoTone(880f, 1318.5f, 0.12f, 0.32f, 0.36f);
            }
        }

        private static float[] SynthSoftUiChime(float f1, float f2, float gapSec, float tailSec, float amp)
        {
            const int sr = 48000;
            int leadSilence = (int)(sr * 0.025f); // 25ms sessiz başlangıç tamponu (DAC uyanma tıklamasını önler)
            int nGap = (int)(sr * gapSec);
            int nTail = (int)(sr * tailSec);
            var buf = new float[leadSilence + nGap + nTail];

            int attackSamples = (int)(sr * 0.008f);

            // 1. yumuşak nota
            int dur1 = nGap + nTail / 2;
            for (int i = 0; i < dur1 && (leadSilence + i) < buf.Length; i++)
            {
                float t = (float)i / sr;
                float att = i < attackSamples ? 0.5f * (1f - MathF.Cos(MathF.PI * i / attackSamples)) : 1f;
                float env = att * MathF.Exp(-t * 24f);
                buf[leadSilence + i] += MathF.Sin(2 * MathF.PI * f1 * t) * amp * env;
            }

            // 2. yumuşak nota
            int start2 = leadSilence + nGap;
            for (int i = 0; i < nTail && (start2 + i) < buf.Length; i++)
            {
                float t = (float)i / sr;
                float att = i < attackSamples ? 0.5f * (1f - MathF.Cos(MathF.PI * i / attackSamples)) : 1f;
                float env = att * MathF.Exp(-t * 20f);
                buf[start2 + i] += MathF.Sin(2 * MathF.PI * f2 * t) * amp * env;
            }

            return buf;
        }

        private static float[] SynthTwoTone(float f1, float f2, float d1, float d2, float amp)
        {
            const int sr = 48000;
            int n1 = (int)(sr * d1);
            int n2 = (int)(sr * d2);
            var buf = new float[n1 + n2];

            for (int i = 0; i < n1; i++)
            {
                float t = (float)i / sr;
                float env = MathF.Sin(MathF.PI * i / n1);
                buf[i] = MathF.Sin(2 * MathF.PI * f1 * t) * amp * env;
            }
            for (int i = 0; i < n2; i++)
            {
                float t = (float)i / sr;
                float env = MathF.Exp(-t / (d2 * 0.55f)) * Math.Min(1f, i / (sr * 0.005f));
                buf[n1 + i] = MathF.Sin(2 * MathF.PI * f2 * t) * amp * env;
            }
            return buf;
        }

        private static float[] SynthArpeggio(float[] freqs, float noteLenSec, float amp)
        {
            const int sr = 48000;
            int step = (int)(sr * noteLenSec);
            int tail = (int)(sr * 0.22f);
            var buf = new float[freqs.Length * step + tail];

            for (int n = 0; n < freqs.Length; n++)
            {
                int start = n * step;
                int dur = (n == freqs.Length - 1) ? step + tail : step + (step / 3);
                for (int i = 0; i < dur && (start + i) < buf.Length; i++)
                {
                    float t = (float)i / sr;
                    float attack = Math.Min(1f, i / (sr * 0.006f));
                    float decay = MathF.Exp(-t * 7.5f);
                    buf[start + i] += MathF.Sin(2 * MathF.PI * freqs[n] * t) * amp * attack * decay;
                }
            }
            return buf;
        }

        private static float[] SynthTick(float freq, float durSec, float amp)
        {
            const int sr = 48000;
            int n = (int)(sr * durSec);
            var buf = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float env = MathF.Exp(-t * 90f);
                buf[i] = MathF.Sin(2 * MathF.PI * freq * t) * amp * env;
            }
            return buf;
        }

        private static float[] SynthClick(float freq, float durSec, float amp)
        {
            const int sr = 48000;
            int n = (int)(sr * durSec);
            var buf = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float env = MathF.Exp(-t * 65f);
                buf[i] = MathF.Sin(2 * MathF.PI * freq * t) * amp * env;
            }
            return buf;
        }
    }
}
