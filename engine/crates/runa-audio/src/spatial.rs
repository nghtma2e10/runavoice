use std::f32::consts::PI;
use crate::biquad::{BiquadFilter, BiquadType};

const SAMPLE_RATE: f32 = 48000.0;
const TWO_PI: f32 = 2.0 * PI;
const DEG_TO_RAD: f32 = PI / 180.0;
const SQRT2: f32 = std::f32::consts::SQRT_2;
const WOODWORTH_COEFF: f32 = 0.0875 / 343.0; // head radius a (8.75 cm) / sound speed c (343 m/s)

const DELAY_BUFFER_SIZE: usize = 64; // Max ITD ~ 32 samples @ 48kHz
const SMOOTHING_SAMPLES: f32 = SAMPLE_RATE * 0.150; // 150 ms = 7200 samples
const SLEW_AZIMUTH_PER_SAMPLE: f32 = 360.0 / SMOOTHING_SAMPLES;
const SLEW_DISTANCE_PER_SAMPLE: f32 = 1.0 / SMOOTHING_SAMPLES;

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
#[derive(Debug, Clone)]
pub struct SpatialPanner {
    enabled: bool,
    speaker_mode: bool,
    target_azimuth_deg: f32,
    target_distance: f32,

    current_azimuth_deg: f32,
    current_distance: f32,

    delay_ring_l: [f32; DELAY_BUFFER_SIZE],
    delay_ring_r: [f32; DELAY_BUFFER_SIZE],
    delay_write_pos: usize,

    shadow_lpf_state_l: f32,
    shadow_lpf_state_r: f32,

    dist_lpf_state_l: f32,
    dist_lpf_state_r: f32,

    back_shelf_l: BiquadFilter,
    back_shelf_r: BiquadFilter,
}

impl Default for SpatialPanner {
    fn default() -> Self {
        let mut back_shelf_l = BiquadFilter::new();
        back_shelf_l.configure(BiquadType::HighShelf, SAMPLE_RATE, 4000.0, 0.707, -4.0);

        let mut back_shelf_r = BiquadFilter::new();
        back_shelf_r.configure(BiquadType::HighShelf, SAMPLE_RATE, 4000.0, 0.707, -4.0);

        Self {
            enabled: false,
            speaker_mode: false,
            target_azimuth_deg: 0.0,
            target_distance: 0.2,
            current_azimuth_deg: 0.0,
            current_distance: 0.2,
            delay_ring_l: [0.0; DELAY_BUFFER_SIZE],
            delay_ring_r: [0.0; DELAY_BUFFER_SIZE],
            delay_write_pos: 0,
            shadow_lpf_state_l: 0.0,
            shadow_lpf_state_r: 0.0,
            dist_lpf_state_l: 0.0,
            dist_lpf_state_r: 0.0,
            back_shelf_l,
            back_shelf_r,
        }
    }
}

impl SpatialPanner {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn configure(&mut self, enabled: bool, azimuth_deg: f32, distance: f32, speaker_mode: bool) {
        self.enabled = enabled;
        self.speaker_mode = speaker_mode;
        self.target_azimuth_deg = azimuth_deg.clamp(-180.0, 180.0);
        self.target_distance = distance.clamp(0.0, 1.0);
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    pub fn reset(&mut self) {
        self.current_azimuth_deg = self.target_azimuth_deg;
        self.current_distance = self.target_distance;
        self.delay_ring_l.fill(0.0);
        self.delay_ring_r.fill(0.0);
        self.delay_write_pos = 0;
        self.shadow_lpf_state_l = 0.0;
        self.shadow_lpf_state_r = 0.0;
        self.dist_lpf_state_l = 0.0;
        self.dist_lpf_state_r = 0.0;
        self.back_shelf_l.reset();
        self.back_shelf_r.reset();
    }

    #[inline(always)]
    pub fn process(&mut self, sample: f32) -> (f32, f32) {
        if !self.enabled {
            return (sample, sample);
        }

        // 1. Parametre yumuşatma
        let az_diff = self.target_azimuth_deg - self.current_azimuth_deg;
        if az_diff.abs() <= SLEW_AZIMUTH_PER_SAMPLE {
            self.current_azimuth_deg = self.target_azimuth_deg;
        } else {
            self.current_azimuth_deg += az_diff.signum() * SLEW_AZIMUTH_PER_SAMPLE;
        }

        let dist_diff = self.target_distance - self.current_distance;
        if dist_diff.abs() <= SLEW_DISTANCE_PER_SAMPLE {
            self.current_distance = self.target_distance;
        } else {
            self.current_distance += dist_diff.signum() * SLEW_DISTANCE_PER_SAMPLE;
        }

        let effective_azimuth = if self.speaker_mode {
            self.current_azimuth_deg * 0.5
        } else {
            self.current_azimuth_deg
        };
        let az_rad = effective_azimuth * DEG_TO_RAD;

        // 2. ILD (Sabit güçlü kaydırma)
        let p = az_rad.sin();
        let pan_angle = (p + 1.0) * (PI * 0.25);
        let g_l = pan_angle.cos() * SQRT2;
        let g_r = pan_angle.sin() * SQRT2;

        // 3. Uzaklık sönümlemesi ve mesafe LPF
        let g_d = 1.0 / (1.0 + 1.5 * self.current_distance);
        let dist_fc = 20000.0 - 12000.0 * self.current_distance;
        let dist_alpha = (-TWO_PI * dist_fc / SAMPLE_RATE).exp();

        let sig = sample * g_d;
        self.dist_lpf_state_l = (1.0 - dist_alpha) * sig + dist_alpha * self.dist_lpf_state_l;
        self.dist_lpf_state_r = (1.0 - dist_alpha) * sig + dist_alpha * self.dist_lpf_state_r;
        let mut ear_l = self.dist_lpf_state_l;
        let mut ear_r = self.dist_lpf_state_r;

        // 4. Arka/ön ayrımı: |θ| > 90° ise HighShelf 4 kHz -4 dB
        if effective_azimuth.abs() > 90.0 {
            ear_l = self.back_shelf_l.process(ear_l);
            ear_r = self.back_shelf_r.process(ear_r);
        }

        // 5. Kafa gölgesi
        let shadow_fc = (20000.0 - 16500.0 * p.abs()).max(2000.0);
        let shadow_alpha = (-TWO_PI * shadow_fc / SAMPLE_RATE).exp();

        if p > 0.001 {
            // Ses sağda -> Sol kulak uzak kulak
            self.shadow_lpf_state_l = (1.0 - shadow_alpha) * ear_l + shadow_alpha * self.shadow_lpf_state_l;
            ear_l = self.shadow_lpf_state_l;
        } else if p < -0.001 {
            // Ses solda -> Sağ kulak uzak kulak
            self.shadow_lpf_state_r = (1.0 - shadow_alpha) * ear_r + shadow_alpha * self.shadow_lpf_state_r;
            ear_r = self.shadow_lpf_state_r;
        }

        // 6. ITD (Woodworth yaklaşımı)
        let mut delay_samples = 0.0_f32;
        if !self.speaker_mode {
            let abs_az = effective_azimuth.abs();
            let theta_prime_deg = if abs_az <= 90.0 { abs_az } else { 180.0 - abs_az };
            let theta_prime_rad = theta_prime_deg * DEG_TO_RAD;

            let itd_sec = WOODWORTH_COEFF * (theta_prime_rad.sin() + theta_prime_rad);
            delay_samples = (itd_sec * SAMPLE_RATE).clamp(0.0, 32.0);
        }

        self.delay_ring_l[self.delay_write_pos] = ear_l;
        self.delay_ring_r[self.delay_write_pos] = ear_r;

        let mut delayed_l = ear_l;
        let mut delayed_r = ear_r;

        if delay_samples > 0.05 {
            let mut read_pos = self.delay_write_pos as f32 - delay_samples;
            while read_pos < 0.0 {
                read_pos += DELAY_BUFFER_SIZE as f32;
            }
            let idx0 = (read_pos as usize) % DELAY_BUFFER_SIZE;
            let idx1 = (idx0 + 1) % DELAY_BUFFER_SIZE;
            let frac = read_pos - read_pos.floor();

            if p > 0.001 {
                delayed_l = self.delay_ring_l[idx0] + frac * (self.delay_ring_l[idx1] - self.delay_ring_l[idx0]);
            } else if p < -0.001 {
                delayed_r = self.delay_ring_r[idx0] + frac * (self.delay_ring_r[idx1] - self.delay_ring_r[idx0]);
            }
        }

        self.delay_write_pos = (self.delay_write_pos + 1) % DELAY_BUFFER_SIZE;

        (delayed_l * g_l, delayed_r * g_r)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_spatial_panner_center() {
        let mut panner = SpatialPanner::new();
        panner.configure(true, 0.0, 0.0, false);
        panner.reset();

        // At center (azimuth = 0, distance = 0), left and right should be equal and match input in steady-state
        let mut out_l = 0.0;
        let mut out_r = 0.0;
        for _ in 0..50 {
            let (l, r) = panner.process(0.5);
            out_l = l;
            out_r = r;
        }
        assert!((out_l - out_r).abs() < 1e-4, "Center must be symmetric: L={}, R={}", out_l, out_r);
        assert!((out_l - 0.5).abs() < 1e-3, "Center power compensation must match input: {}", out_l);
    }

    #[test]
    fn test_spatial_panner_hard_right() {
        let mut panner = SpatialPanner::new();
        panner.configure(true, 90.0, 0.0, false);
        panner.reset();

        let (out_l, out_r) = panner.process(0.5);
        assert!(out_r > out_l, "Right ear must be louder when sound is on the right: R={}, L={}", out_r, out_l);
    }
}
