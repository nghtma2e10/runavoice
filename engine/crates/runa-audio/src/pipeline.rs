use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use crate::biquad::{BiquadFilter, BiquadType};
use crate::config::AudioSettings;
use crate::denoise::RnnoiseFilter;

pub const SAMPLE_RATE: usize = 48000;
pub const OPUS_FRAME_SAMPLES: usize = 480; // 10 ms @ 48 kHz
const MIC_TEST_MAX_SAMPLES: usize = SAMPLE_RATE * 30; // 30 seconds

pub struct AudioInputPipeline {
    settings: Mutex<AudioSettings>,
    hp_impact: Mutex<BiquadFilter>,
    eq_80: Mutex<BiquadFilter>,
    eq_300: Mutex<BiquadFilter>,
    eq_1k: Mutex<BiquadFilter>,
    eq_35k: Mutex<BiquadFilter>,
    eq_8k: Mutex<BiquadFilter>,
    rnnoise: Mutex<RnnoiseFilter>,

    prev_impact_sample: f32,
    vad_hold_samples: usize,

    is_muted: AtomicBool,
    is_deafened: AtomicBool,
    is_ptt_held: AtomicBool,
    is_mic_monitor_enabled: AtomicBool,

    current_vu_level: f32,
    current_input_db: f32,

    // 30s mic test buffer
    is_recording_mic_test: AtomicBool,
    mic_test_buffer: Arc<Mutex<Vec<f32>>>,

    // Outgoing 10ms frame accumulator
    outgoing_frame_buf: Vec<i16>,
}

impl Default for AudioInputPipeline {
    fn default() -> Self {
        let settings = AudioSettings::default();

        let mut hp_impact = BiquadFilter::new();
        hp_impact.configure(BiquadType::HighPass, 48000.0, if settings.impact_guard { 85.0 } else { 35.0 }, 0.707, 0.0);

        let mut eq_80 = BiquadFilter::new();
        eq_80.configure(BiquadType::LowShelf, 48000.0, 80.0, 0.8, settings.eq_bands[0]);

        let mut eq_300 = BiquadFilter::new();
        eq_300.configure(BiquadType::PeakingEQ, 48000.0, 300.0, 1.0, settings.eq_bands[1]);

        let mut eq_1k = BiquadFilter::new();
        eq_1k.configure(BiquadType::PeakingEQ, 48000.0, 1000.0, 1.0, settings.eq_bands[2]);

        let mut eq_35k = BiquadFilter::new();
        eq_35k.configure(BiquadType::PeakingEQ, 48000.0, 3500.0, 1.0, settings.eq_bands[3]);

        let mut eq_8k = BiquadFilter::new();
        eq_8k.configure(BiquadType::HighShelf, 48000.0, 8000.0, 0.8, settings.eq_bands[4]);

        let rnnoise = RnnoiseFilter::new();

        Self {
            settings: Mutex::new(settings),
            hp_impact: Mutex::new(hp_impact),
            eq_80: Mutex::new(eq_80),
            eq_300: Mutex::new(eq_300),
            eq_1k: Mutex::new(eq_1k),
            eq_35k: Mutex::new(eq_35k),
            eq_8k: Mutex::new(eq_8k),
            rnnoise: Mutex::new(rnnoise),
            prev_impact_sample: 0.0,
            vad_hold_samples: 0,
            is_muted: AtomicBool::new(false),
            is_deafened: AtomicBool::new(false),
            is_ptt_held: AtomicBool::new(false),
            is_mic_monitor_enabled: AtomicBool::new(false),
            current_vu_level: 0.0,
            current_input_db: -65.0,
            is_recording_mic_test: AtomicBool::new(false),
            mic_test_buffer: Arc::new(Mutex::new(Vec::with_capacity(MIC_TEST_MAX_SAMPLES))),
            outgoing_frame_buf: Vec::with_capacity(OPUS_FRAME_SAMPLES),
        }
    }
}

impl AudioInputPipeline {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn apply_settings(&self, s: AudioSettings) {
        let mut hp = self.hp_impact.lock();
        hp.configure(BiquadType::HighPass, 48000.0, if s.impact_guard { 85.0 } else { 35.0 }, 0.707, 0.0);

        let mut eq80 = self.eq_80.lock();
        eq80.configure(BiquadType::LowShelf, 48000.0, 80.0, 0.8, s.eq_bands[0]);

        let mut eq300 = self.eq_300.lock();
        eq300.configure(BiquadType::PeakingEQ, 48000.0, 300.0, 1.0, s.eq_bands[1]);

        let mut eq1k = self.eq_1k.lock();
        eq1k.configure(BiquadType::PeakingEQ, 48000.0, 1000.0, 1.0, s.eq_bands[2]);

        let mut eq35k = self.eq_35k.lock();
        eq35k.configure(BiquadType::PeakingEQ, 48000.0, 3500.0, 1.0, s.eq_bands[3]);

        let mut eq8k = self.eq_8k.lock();
        eq8k.configure(BiquadType::HighShelf, 48000.0, 8000.0, 0.8, s.eq_bands[4]);

        let mut rnn = self.rnnoise.lock();
        rnn.engine_mode = s.ns_engine.clone();
        rnn.suppression_level = s.ns_level;

        *self.settings.lock() = s;
    }

    pub fn set_mute(&self, muted: bool) {
        self.is_muted.store(muted, Ordering::SeqCst);
    }

    pub fn set_deafen(&self, deafened: bool) {
        self.is_deafened.store(deafened, Ordering::SeqCst);
    }

    pub fn set_ptt(&self, held: bool) {
        self.is_ptt_held.store(held, Ordering::SeqCst);
    }

    pub fn set_mic_monitor(&self, enabled: bool) {
        self.is_mic_monitor_enabled.store(enabled, Ordering::SeqCst);
    }

    pub fn start_mic_test_recording(&self) {
        let mut buf = self.mic_test_buffer.lock();
        buf.clear();
        self.is_recording_mic_test.store(true, Ordering::SeqCst);
    }

    pub fn stop_mic_test_recording(&self) -> Option<Vec<f32>> {
        self.is_recording_mic_test.store(false, Ordering::SeqCst);
        let buf = self.mic_test_buffer.lock();
        if buf.len() > SAMPLE_RATE / 4 {
            Some(buf.clone())
        } else {
            None
        }
    }

    pub fn is_recording_mic_test(&self) -> bool {
        self.is_recording_mic_test.load(Ordering::Relaxed)
    }

    pub fn current_vad_prob(&self) -> f32 {
        self.rnnoise.lock().last_vad_probability()
    }

    pub fn mic_test_progress(&self) -> f32 {
        let len = self.mic_test_buffer.lock().len();
        (len as f32 / MIC_TEST_MAX_SAMPLES as f32).clamp(0.0, 1.0)
    }

    pub fn current_vu(&self) -> f32 {
        self.current_vu_level
    }

    pub fn current_db(&self) -> f32 {
        self.current_input_db
    }

    /// 48 kHz mono PCM f32 bloğunu işler
    /// Çıktı:
    /// - `ready_frames`: Opus'a gönderilmeye hazır 10 ms (480 i16) çerçeveler
    /// - `monitor_samples`: "Sesimi Dinle" için temizlenmiş f32 örnekler
    /// - `is_speaking`: VAD/PTT kapısının açık ve konuşma olup olmadığı
    pub fn process_input_samples(
        &mut self,
        samples: &mut [f32],
        aux_broadcast_samples: &[f32],
        is_room_active: bool,
    ) -> (Vec<Vec<i16>>, Option<Vec<f32>>, bool) {
        let count = samples.len();
        if count == 0 {
            return (Vec::new(), None, false);
        }

        let s = self.settings.lock().clone();
        let in_gain = (s.input_gain).clamp(0.0, 2.5);

        // 1. Giriş kazancı + Darbe & Nefes Kalkanı
        let mut hp = self.hp_impact.lock();
        for sample in samples.iter_mut() {
            let mut x = *sample * in_gain;
            x = hp.process(x);

            if s.impact_guard {
                let delta = x - self.prev_impact_sample;
                const MAX_SLEW: f32 = 0.28;
                if delta > MAX_SLEW {
                    x = self.prev_impact_sample + MAX_SLEW;
                } else if delta < -MAX_SLEW {
                    x = self.prev_impact_sample - MAX_SLEW;
                }
                self.prev_impact_sample = x;
            }

            *sample = x.clamp(-1.0, 1.0);
        }
        drop(hp);

        // 2. RNNoise
        let mut rnn = self.rnnoise.lock();
        rnn.process_in_place(samples);
        let rnn_vad_prob = rnn.last_vad_probability();
        drop(rnn);

        // 3. 5-Bant Biquad EQ
        let mut eq80 = self.eq_80.lock();
        let mut eq300 = self.eq_300.lock();
        let mut eq1k = self.eq_1k.lock();
        let mut eq35k = self.eq_35k.lock();
        let mut eq8k = self.eq_8k.lock();

        let mut sum_sq = 0.0_f32;
        let mut max_peak = 0.0_f32;

        for sample in samples.iter_mut() {
            let mut y = *sample;
            y = eq80.process(y);
            y = eq300.process(y);
            y = eq1k.process(y);
            y = eq35k.process(y);
            y = eq8k.process(y);

            // Doğal doygunluk koruması
            y = y.clamp(-0.96, 0.96);

            *sample = y;
            sum_sq += y * y;
            let abs = y.abs();
            if abs > max_peak {
                max_peak = abs;
            }
        }
        drop(eq80);
        drop(eq300);
        drop(eq1k);
        drop(eq35k);
        drop(eq8k);

        let rms = (sum_sq / count as f32).sqrt();
        let db = if rms > 1e-5 { 20.0 * rms.log10() } else { -65.0 };
        self.current_input_db = db.clamp(-65.0, 0.0);
        self.current_vu_level = (max_peak * 1.45).clamp(0.0, 1.0);

        // 4. 30s Mikrofon Testi Kaydı
        if self.is_recording_mic_test.load(Ordering::Relaxed) {
            let mut buf = self.mic_test_buffer.lock();
            let space = MIC_TEST_MAX_SAMPLES.saturating_sub(buf.len());
            let to_copy = count.min(space);
            buf.extend_from_slice(&samples[..to_copy]);
            if buf.len() >= MIC_TEST_MAX_SAMPLES {
                self.is_recording_mic_test.store(false, Ordering::Relaxed);
            }
        }

        // 5. "Sesimi Dinle" canlı kulaklık monitörü
        let monitor_samples = if self.is_mic_monitor_enabled.load(Ordering::Relaxed) && !self.is_deafened.load(Ordering::Relaxed) {
            Some(samples.to_vec())
        } else {
            None
        };

        // 6. Susturma, Sağırlaştırma ve VAD / PTT Kapısı
        let is_muted = self.is_muted.load(Ordering::Relaxed);
        let is_deafened = self.is_deafened.load(Ordering::Relaxed);
        let is_ptt_held = self.is_ptt_held.load(Ordering::Relaxed);

        let gate_open = if is_muted || is_deafened {
            self.current_vu_level = 0.0;
            false
        } else if s.gate_mode == "ptt" {
            is_ptt_held
        } else {
            let above_threshold = (db >= s.vad_threshold_db)
                && (s.ns_engine == "off" || rnn_vad_prob >= 0.08 || db >= s.vad_threshold_db + 8.0);
            if above_threshold {
                self.vad_hold_samples = SAMPLE_RATE * 180 / 1000; // 180ms
                true
            } else if self.vad_hold_samples > 0 {
                self.vad_hold_samples = self.vad_hold_samples.saturating_sub(count);
                true
            } else {
                false
            }
        };

        let is_speaking = gate_open && rms > 0.004;

        // 7. Çerçeveleme (Opus için 10 ms 480 örnek)
        let mut ready_frames = Vec::new();
        if is_room_active {
            for i in 0..count {
                let voice_sample = if gate_open { samples[i] } else { 0.0 };
                let aux_sample = if i < aux_broadcast_samples.len() { aux_broadcast_samples[i] } else { 0.0 };
                let combined = (voice_sample + aux_sample).clamp(-0.98, 0.98);

                self.outgoing_frame_buf.push((combined * 32767.0) as i16);

                if self.outgoing_frame_buf.len() == OPUS_FRAME_SAMPLES {
                    ready_frames.push(std::mem::replace(
                        &mut self.outgoing_frame_buf,
                        Vec::with_capacity(OPUS_FRAME_SAMPLES),
                    ));
                }
            }
        }

        (ready_frames, monitor_samples, is_speaking)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_pipeline_smoke() {
        let mut pipe = AudioInputPipeline::new();
        let mut samples = vec![0.1_f32; 480];
        let aux = vec![0.0_f32; 480];
        let (frames, _, speaking) = pipe.process_input_samples(&mut samples, &aux, true);
        assert_eq!(frames.len(), 1, "Must produce exactly one 480-sample frame for 480 input samples");
        assert!(pipe.current_vu() >= 0.0);
        let _ = speaking;
    }
}
