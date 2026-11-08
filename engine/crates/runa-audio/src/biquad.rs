use std::f32::consts::PI;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BiquadType {
    LowPass,
    HighPass,
    PeakingEQ,
    LowShelf,
    HighShelf,
}

/// 2. Derece IIR Audio-EQ-Cookbook Biquad Filtresi.
///
/// 48kHz gerçek zamanlı ses işleme için sıfır bellek tahsisiyle çalışır.
#[derive(Debug, Clone)]
pub struct BiquadFilter {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
    z1: f32,
    z2: f32,
}

impl Default for BiquadFilter {
    fn default() -> Self {
        Self {
            b0: 1.0,
            b1: 0.0,
            b2: 0.0,
            a1: 0.0,
            a2: 0.0,
            z1: 0.0,
            z2: 0.0,
        }
    }
}

impl BiquadFilter {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn configure(
        &mut self,
        filter_type: BiquadType,
        sample_rate: f32,
        frequency: f32,
        q: f32,
        gain_db: f32,
    ) {
        let freq = frequency.clamp(20.0, sample_rate * 0.48);
        let q_val = q.max(0.1);

        let w0 = (2.0 * PI * freq / sample_rate) as f64;
        let cos_w0 = w0.cos();
        let sin_w0 = w0.sin();
        let alpha = sin_w0 / (2.0 * q_val as f64);
        let a = 10.0_f64.powf(gain_db as f64 / 40.0);

        let (b0, b1, b2, a0, a1, a2) = match filter_type {
            BiquadType::HighPass => {
                let b0 = (1.0 + cos_w0) * 0.5;
                let b1 = -(1.0 + cos_w0);
                let b2 = (1.0 + cos_w0) * 0.5;
                let a0 = 1.0 + alpha;
                let a1 = -2.0 * cos_w0;
                let a2 = 1.0 - alpha;
                (b0, b1, b2, a0, a1, a2)
            }
            BiquadType::LowPass => {
                let b0 = (1.0 - cos_w0) * 0.5;
                let b1 = 1.0 - cos_w0;
                let b2 = (1.0 - cos_w0) * 0.5;
                let a0 = 1.0 + alpha;
                let a1 = -2.0 * cos_w0;
                let a2 = 1.0 - alpha;
                (b0, b1, b2, a0, a1, a2)
            }
            BiquadType::PeakingEQ => {
                let b0 = 1.0 + alpha * a;
                let b1 = -2.0 * cos_w0;
                let b2 = 1.0 - alpha * a;
                let a0 = 1.0 + alpha / a;
                let a1 = -2.0 * cos_w0;
                let a2 = 1.0 - alpha / a;
                (b0, b1, b2, a0, a1, a2)
            }
            BiquadType::LowShelf => {
                let two_sqrt_a_alpha = 2.0 * a.sqrt() * alpha;
                let b0 = a * ((a + 1.0) - (a - 1.0) * cos_w0 + two_sqrt_a_alpha);
                let b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cos_w0);
                let b2 = a * ((a + 1.0) - (a - 1.0) * cos_w0 - two_sqrt_a_alpha);
                let a0 = (a + 1.0) + (a - 1.0) * cos_w0 + two_sqrt_a_alpha;
                let a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cos_w0);
                let a2 = (a + 1.0) + (a - 1.0) * cos_w0 - two_sqrt_a_alpha;
                (b0, b1, b2, a0, a1, a2)
            }
            BiquadType::HighShelf => {
                let two_sqrt_a_alpha = 2.0 * a.sqrt() * alpha;
                let b0 = a * ((a + 1.0) + (a - 1.0) * cos_w0 + two_sqrt_a_alpha);
                let b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cos_w0);
                let b2 = a * ((a + 1.0) + (a - 1.0) * cos_w0 - two_sqrt_a_alpha);
                let a0 = (a + 1.0) - (a - 1.0) * cos_w0 + two_sqrt_a_alpha;
                let a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cos_w0);
                let a2 = (a + 1.0) - (a - 1.0) * cos_w0 - two_sqrt_a_alpha;
                (b0, b1, b2, a0, a1, a2)
            }
        };

        self.b0 = (b0 / a0) as f32;
        self.b1 = (b1 / a0) as f32;
        self.b2 = (b2 / a0) as f32;
        self.a1 = (a1 / a0) as f32;
        self.a2 = (a2 / a0) as f32;
    }

    #[inline(always)]
    pub fn process(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;

        if !y.is_finite() {
            self.z1 = 0.0;
            self.z2 = 0.0;
            return 0.0;
        }
        y
    }

    pub fn reset(&mut self) {
        self.z1 = 0.0;
        self.z2 = 0.0;
    }

    pub fn get_preset_bands(profile: &str) -> (f32, f32, f32, f32, f32) {
        match profile.to_lowercase().as_str() {
            "clean" => (-3.0, -1.5, 1.5, 3.0, 2.0),
            "podcast" => (3.5, 1.0, -1.0, 3.5, 2.5),
            "clarity" => (-4.0, -2.0, 2.0, 5.5, 4.5),
            "deep" => (5.5, 2.5, -0.5, 1.0, 0.5),
            "flat" => (0.0, 0.0, 0.0, 0.0, 0.0),
            _ => (-2.0, -1.0, 1.5, 3.0, 2.0),
        }
    }
}

/// Kulak Koruyucu Dinamik Tavan Sınırlayıcı (Max dB Brickwall Soft-Knee Limiter).
/// Ani çığlık, mikrofon düşmesi veya %200 ses artırmada işitme sağlığını korur.
#[derive(Debug, Clone)]
pub struct EarProtectionLimiter {
    envelope: f32,
    ceiling_linear: f32,
}

impl Default for EarProtectionLimiter {
    fn default() -> Self {
        Self {
            envelope: 0.0,
            ceiling_linear: 0.7943282, // -2.0 dBFS default (10^(-2/20))
        }
    }
}

impl EarProtectionLimiter {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn set_ceiling_db(&mut self, ceiling_db: f32) {
        let clamped = ceiling_db.clamp(-24.0, 0.0);
        self.ceiling_linear = 10.0_f32.powf(clamped / 20.0);
    }

    #[inline(always)]
    pub fn process(&mut self, mut sample: f32) -> f32 {
        let abs = sample.abs();
        let coeff = if abs > self.envelope { 0.90 } else { 0.9993 };
        self.envelope = coeff * self.envelope + (1.0 - coeff) * abs;
        if abs > self.envelope {
            self.envelope = abs;
        }

        if self.envelope > self.ceiling_linear && self.envelope > 1e-6 {
            let gain = self.ceiling_linear / self.envelope;
            sample *= gain;
        }

        sample.clamp(-self.ceiling_linear, self.ceiling_linear)
    }

    #[inline(always)]
    pub fn process_stereo(&mut self, l: &mut f32, r: &mut f32) {
        let peak = l.abs().max(r.abs());
        let coeff = if peak > self.envelope { 0.90 } else { 0.9993 };
        self.envelope = coeff * self.envelope + (1.0 - coeff) * peak;
        if peak > self.envelope {
            self.envelope = peak;
        }

        if self.envelope > self.ceiling_linear && self.envelope > 1e-6 {
            let gain = self.ceiling_linear / self.envelope;
            *l *= gain;
            *r *= gain;
        }

        *l = l.clamp(-self.ceiling_linear, self.ceiling_linear);
        *r = r.clamp(-self.ceiling_linear, self.ceiling_linear);
    }

    pub fn reset(&mut self) {
        self.envelope = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_biquad_highpass_parity() {
        let mut f = BiquadFilter::new();
        f.configure(BiquadType::HighPass, 48000.0, 85.0, 0.7071, 0.0);

        // Feed DC (constant 1.0) -> Highpass must attenuate to ~0
        let mut out = 0.0;
        for _ in 0..1000 {
            out = f.process(1.0);
        }
        assert!(out.abs() < 1e-3, "Highpass DC attenuation failed: {}", out);
    }

    #[test]
    fn test_limiter_ceiling() {
        let mut lim = EarProtectionLimiter::new();
        lim.set_ceiling_db(-6.0); // ~0.501
        let max_allowed = 10.0_f32.powf(-6.0 / 20.0) + 1e-5;

        for _ in 0..200 {
            let out = lim.process(2.0); // Huge input
            assert!(out <= max_allowed && out >= -max_allowed, "Limiter ceiling exceeded: {}", out);
        }
    }
}
