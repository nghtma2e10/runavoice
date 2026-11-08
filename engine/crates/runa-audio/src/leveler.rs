use crate::biquad::{BiquadFilter, BiquadType};

const SAMPLE_RATE: f32 = 48000.0;
const BLOCK_SIZE: usize = 480; // 10 ms @ 48 kHz
const CEILING_LINEAR: f32 = 0.89125; // -1 dBFS

const MAX_RISE_PER_SAMPLE: f32 = 3.0 / SAMPLE_RATE;  // +3 dB / s
const MAX_FALL_PER_SAMPLE: f32 = 12.0 / SAMPLE_RATE; // -12 dB / s

/// Ses Dengeleme Modu (Loudness Leveler - Özellik 2):
/// - Uzak katılımcıların konuşma seviyelerini ölçüp hedef desibele doğru dengeler.
/// - K-ağırlıklı yan zincir (HP 100Hz + HighShelf 1500Hz +4dB).
/// - 10 ms (480 örnek) bloklarla konuşma kapısı (RMS > -50 dBFS ve vokal tespiti).
/// - Kapı kapalıyken kazanç dondurulur; sessizlik veya gürültü yükseltilmez.
/// - Yükselirken en fazla 3 dB/sn, düşerken en fazla 12 dB/sn hız sınırı.
/// - Kanal içi -1 dBFS yumuşak sınırlayıcı (atak 1 ms, bırakma 80 ms).
#[derive(Debug, Clone)]
pub struct LoudnessLeveler {
    sidechain_hp: BiquadFilter,
    sidechain_shelf: BiquadFilter,

    enabled: bool,
    excluded: bool,
    target_db: f32,
    max_boost_db: f32,
    max_cut_db: f32,

    block_sample_count: usize,
    block_sum_sq: f32,
    level_db: f32,
    target_gain_db: f32,
    current_gain_db: f32,
    current_linear_gain: f32,

    speech_sample_count: usize,
    limiter_envelope: f32,

    limiter_attack_coeff: f32,
    limiter_release_coeff: f32,
}

impl Default for LoudnessLeveler {
    fn default() -> Self {
        let mut sidechain_hp = BiquadFilter::new();
        sidechain_hp.configure(BiquadType::HighPass, SAMPLE_RATE, 100.0, std::f32::consts::FRAC_1_SQRT_2, 0.0);

        let mut sidechain_shelf = BiquadFilter::new();
        sidechain_shelf.configure(BiquadType::HighShelf, SAMPLE_RATE, 1500.0, std::f32::consts::FRAC_1_SQRT_2, 4.0);

        Self {
            sidechain_hp,
            sidechain_shelf,
            enabled: true,
            excluded: false,
            target_db: -22.0,
            max_boost_db: 12.0,
            max_cut_db: 10.0,
            block_sample_count: 0,
            block_sum_sq: 0.0,
            level_db: -22.0,
            target_gain_db: 0.0,
            current_gain_db: 0.0,
            current_linear_gain: 1.0,
            speech_sample_count: 0,
            limiter_envelope: 0.0,
            limiter_attack_coeff: (-1.0 / (0.001 * SAMPLE_RATE)).exp(),
            limiter_release_coeff: (-1.0 / (0.080 * SAMPLE_RATE)).exp(),
        }
    }
}

impl LoudnessLeveler {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn configure(&mut self, mode: &str, target_db: f32, excluded: bool) {
        self.excluded = excluded;
        self.target_db = target_db.clamp(-30.0, -10.0);

        match mode.to_lowercase().as_str() {
            "light" => {
                self.enabled = true;
                self.max_boost_db = 6.0;
                self.max_cut_db = 6.0;
            }
            "strong" => {
                self.enabled = true;
                self.max_boost_db = 18.0;
                self.max_cut_db = 14.0;
            }
            "off" => {
                self.enabled = false;
                self.max_boost_db = 0.0;
                self.max_cut_db = 0.0;
            }
            _ => {
                // "normal"
                self.enabled = true;
                self.max_boost_db = 12.0;
                self.max_cut_db = 10.0;
            }
        }
    }

    pub fn current_gain_db(&self) -> f32 {
        self.current_gain_db
    }

    pub fn reset(&mut self) {
        self.block_sample_count = 0;
        self.block_sum_sq = 0.0;
        self.level_db = self.target_db;
        self.target_gain_db = 0.0;
        self.current_gain_db = 0.0;
        self.current_linear_gain = 1.0;
        self.speech_sample_count = 0;
        self.limiter_envelope = 0.0;
        self.sidechain_hp.reset();
        self.sidechain_shelf.reset();
    }

    #[inline(always)]
    pub fn process(&mut self, sample: f32, is_channel_speaking: bool) -> f32 {
        if !self.enabled || self.excluded {
            return sample;
        }

        // 1. K-weighted sidechain analysis
        let filtered = self.sidechain_shelf.process(self.sidechain_hp.process(sample));
        self.block_sum_sq += filtered * filtered;
        self.block_sample_count += 1;

        if self.block_sample_count >= BLOCK_SIZE {
            let rms = (self.block_sum_sq / BLOCK_SIZE as f32).sqrt();
            self.block_sum_sq = 0.0;
            self.block_sample_count = 0;

            let block_db = 20.0 * (rms.max(1e-5)).log10();

            // 2. Speech gate: block > -50 dBFS and channel speaking
            if block_db > -50.0 && is_channel_speaking {
                self.speech_sample_count += BLOCK_SIZE;
                let tau: f32 = if self.speech_sample_count < (SAMPLE_RATE * 1.5) as usize {
                    0.5
                } else {
                    3.0
                };
                let dt = BLOCK_SIZE as f32 / SAMPLE_RATE;
                let alpha = 1.0 - (-dt / tau).exp();

                self.level_db = (1.0 - alpha) * self.level_db + alpha * block_db;

                // 4. Target gain
                self.target_gain_db = (self.target_db - self.level_db).clamp(-self.max_cut_db, self.max_boost_db);
            }
        }

        // 5. Gain slew smoothing
        if self.current_gain_db < self.target_gain_db {
            self.current_gain_db = self.target_gain_db.min(self.current_gain_db + MAX_RISE_PER_SAMPLE);
            self.current_linear_gain = 10.0_f32.powf(self.current_gain_db / 20.0);
        } else if self.current_gain_db > self.target_gain_db {
            self.current_gain_db = self.target_gain_db.max(self.current_gain_db - MAX_FALL_PER_SAMPLE);
            self.current_linear_gain = 10.0_f32.powf(self.current_gain_db / 20.0);
        }

        let mut amplified = sample * self.current_linear_gain;

        // 6. In-channel soft limiter
        let abs = amplified.abs();
        let coeff = if abs > self.limiter_envelope {
            self.limiter_attack_coeff
        } else {
            self.limiter_release_coeff
        };
        self.limiter_envelope = coeff * self.limiter_envelope + (1.0 - coeff) * abs;
        if abs > self.limiter_envelope {
            self.limiter_envelope = abs;
        }

        if self.limiter_envelope > CEILING_LINEAR {
            amplified *= CEILING_LINEAR / self.limiter_envelope;
        }

        amplified.clamp(-CEILING_LINEAR, CEILING_LINEAR)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_loudness_leveler_boost() {
        let mut leveler = LoudnessLeveler::new();
        leveler.configure("normal", -22.0, false);

        // Feed quiet speech (RMS ~ -35 dBFS -> linear ~ 0.0177)
        let quiet_val = 0.0177_f32;
        let mut processed = 0.0;
        for _ in 0..(48000 * 2) {
            processed = leveler.process(quiet_val, true);
        }

        assert!(leveler.current_gain_db() > 0.0, "Leveler should boost quiet speech: {}", leveler.current_gain_db());
        assert!(processed > quiet_val, "Processed output must be louder than input");
    }
}
