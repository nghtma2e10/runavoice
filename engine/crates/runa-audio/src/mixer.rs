use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::time::{Duration, Instant};
use parking_lot::Mutex;
use crate::biquad::{BiquadFilter, BiquadType, EarProtectionLimiter};
use crate::config::{AudioSettings, PeerAudioConfig};
use crate::leveler::LoudnessLeveler;
use crate::spatial::SpatialPanner;

pub struct PeerAudioChannel {
    pub peer_key: String,
    pub peer_name: String,
    config: Mutex<PeerAudioConfig>,
    leveler: Mutex<LoudnessLeveler>,
    spatial_panner: Mutex<SpatialPanner>,
    clarity_shelf: Mutex<BiquadFilter>,
    samples_ring: Vec<f32>,
    read_pos: usize,
    write_pos: usize,
    available_samples: usize,
    is_speaking: AtomicBool,
    jitter: JitterState,
}

// ---- Uyarlanabilir alış (jitter) tamponu ----
// "Pay" = okuma bloğundan SONRA tamponda kalacak örnek sayısı. Hedef pay 10 ms ile başlar; ses akarken
// tampon boşalırsa (gerçek kesinti) 2,5 ms büyür ve o seviye taban olur; 30 sn kesintisiz geçince 5 sn'de bir
// 0,5 ms küçülür; taban 5 dk'da bir 0,5 ms gevşer. Son 500 ms'deki en düşük pay hedefin üzerindeyse fazlası
// 2 ms çapraz geçişle atılır: iki bilgisayarın ses saatleri arasındaki fark gecikme olarak birikmez.
const SLACK_MIN: usize = 48; // 1 ms
const SLACK_MAX: usize = 3840; // 80 ms
const SLACK_STEP: usize = 120; // 2,5 ms
const SLACK_DECAY: usize = 24; // 0,5 ms
const CROSSFADE: usize = 96; // 2 ms

struct JitterState {
    playing: bool,
    target_slack: usize,
    floor_slack: usize,
    floor_set: Option<Instant>,
    window_min: i64,
    window_start: Instant,
    last_underrun: Option<Instant>,
    last_decay: Instant,
    last_write: Option<Instant>,
    stream_active: bool,
    underruns: u32,
    avg_buffered: f64, // örnek; ölçüm yoksa < 0
}

impl JitterState {
    fn new() -> Self {
        let now = Instant::now();
        Self {
            playing: false,
            target_slack: 480,
            floor_slack: SLACK_MIN,
            floor_set: None,
            window_min: i64::MAX,
            window_start: now,
            last_underrun: None,
            last_decay: now,
            last_write: None,
            stream_active: false,
            underruns: 0,
            avg_buffered: -1.0,
        }
    }
}

/// Bir eşin alış tamponu ölçümleri (ms; ölçüm yoksa -1).
#[derive(Debug, Clone, Copy)]
pub struct PeerJitterStats {
    pub avg_buffered_ms: f64,
    pub target_ms: f64,
    pub underruns: u32,
}

impl PeerAudioChannel {
    pub fn new(peer_key: String, peer_name: String, cfg: PeerAudioConfig, s: &AudioSettings) -> Self {
        let mut clarity_shelf = BiquadFilter::new();
        clarity_shelf.configure(BiquadType::HighShelf, 48000.0, 3500.0, 0.707, 3.5);

        let mut leveler = LoudnessLeveler::new();
        leveler.configure(&s.leveling_mode, s.leveling_target_db, cfg.exclude_from_leveling);

        let mut spatial_panner = SpatialPanner::new();
        spatial_panner.configure(
            s.spatial_enabled,
            cfg.spatial_azimuth_deg,
            cfg.spatial_distance,
            s.spatial_speaker_mode,
        );

        Self {
            peer_key,
            peer_name,
            config: Mutex::new(cfg),
            leveler: Mutex::new(leveler),
            spatial_panner: Mutex::new(spatial_panner),
            clarity_shelf: Mutex::new(clarity_shelf),
            samples_ring: vec![0.0; 48000], // 1 second buffer
            read_pos: 0,
            write_pos: 0,
            available_samples: 0,
            is_speaking: AtomicBool::new(false),
            jitter: JitterState::new(),
        }
    }

    /// Okuma konumundan itibaren `drop` örneği atlar; 2 ms çapraz geçişle tıklama olmaz.
    fn drop_with_crossfade(&mut self, drop: usize) {
        if drop == 0 || drop > self.available_samples {
            return;
        }
        let n = self.samples_ring.len();
        let fade = CROSSFADE.min(self.available_samples - drop);
        for k in 0..fade {
            let w = (k as f32 + 1.0) / (fade as f32 + 1.0);
            let a = (self.read_pos + k) % n;
            let b = (self.read_pos + drop + k) % n;
            self.samples_ring[b] = self.samples_ring[a] * (1.0 - w) + self.samples_ring[b] * w;
        }
        self.read_pos = (self.read_pos + drop) % n;
        self.available_samples -= drop;
    }

    /// Her çıkış bloğunun başında bir kez çağrılır (ön tampon, fazlalık atma, hedef payın uyarlanması).
    fn begin_block(&mut self, frames: usize) {
        let now = Instant::now();
        let js = &mut self.jitter;
        js.stream_active = js.last_write.map(|t| now.duration_since(t) < Duration::from_millis(80)).unwrap_or(false);

        if !js.playing && js.stream_active && self.available_samples >= frames + js.target_slack {
            js.playing = true;
            js.window_min = i64::MAX;
            js.window_start = now;
        }

        if !(js.playing && js.stream_active) {
            return;
        }

        let avail = self.available_samples as f64;
        js.avg_buffered = if js.avg_buffered < 0.0 { avail } else { js.avg_buffered * 0.97 + avail * 0.03 };

        let slack = self.available_samples as i64 - frames as i64;
        if slack < js.window_min {
            js.window_min = slack;
        }

        let mut drop = 0usize;
        if now.duration_since(js.window_start) >= Duration::from_millis(500) {
            if js.window_min != i64::MAX && js.window_min > (js.target_slack + CROSSFADE) as i64 {
                drop = js.window_min as usize - js.target_slack;
            }
            js.window_min = i64::MAX;
            js.window_start = now;
        }

        if let Some(fs) = js.floor_set {
            if now.duration_since(fs) > Duration::from_secs(300) {
                js.floor_slack = js.floor_slack.saturating_sub(SLACK_DECAY).max(SLACK_MIN);
                js.floor_set = Some(now);
            }
        }
        let clean_for = js.last_underrun.map(|t| now.duration_since(t)).unwrap_or(Duration::from_secs(3600));
        if clean_for > Duration::from_secs(30) && now.duration_since(js.last_decay) > Duration::from_secs(5) {
            js.target_slack = js.target_slack.saturating_sub(SLACK_DECAY).max(js.floor_slack.max(SLACK_MIN));
            js.last_decay = now;
        }

        if drop > 0 {
            self.drop_with_crossfade(drop);
        }
    }

    fn jitter_stats(&self) -> PeerJitterStats {
        let js = &self.jitter;
        PeerJitterStats {
            avg_buffered_ms: if js.avg_buffered < 0.0 { -1.0 } else { js.avg_buffered / 48.0 },
            target_ms: js.target_slack as f64 / 48.0,
            underruns: js.underruns,
        }
    }

    pub fn apply_config(&self, cfg: PeerAudioConfig, s: &AudioSettings) {
        let mut lev = self.leveler.lock();
        lev.configure(&s.leveling_mode, s.leveling_target_db, cfg.exclude_from_leveling);

        let mut sp = self.spatial_panner.lock();
        sp.configure(
            s.spatial_enabled,
            cfg.spatial_azimuth_deg,
            cfg.spatial_distance,
            s.spatial_speaker_mode,
        );

        *self.config.lock() = cfg;
    }

    pub fn push_samples(&mut self, samples: &[f32]) {
        self.jitter.last_write = Some(Instant::now());
        for &s in samples {
            self.samples_ring[self.write_pos] = s;
            self.write_pos = (self.write_pos + 1) % self.samples_ring.len();
            if self.available_samples < self.samples_ring.len() {
                self.available_samples += 1;
            } else {
                // Buffer overflow: advance read pointer
                self.read_pos = (self.read_pos + 1) % self.samples_ring.len();
            }
        }
    }

    pub fn read_sample(&mut self) -> (f32, f32) {
        if !self.jitter.playing {
            // Ön tampon dolana kadar sessizlik (tampondan okunmaz)
            self.is_speaking.store(false, Ordering::Relaxed);
            return (0.0, 0.0);
        }
        if self.available_samples == 0 {
            // Tampon boşaldı. Ses hâlâ akıyorsa gerçek kesinti: hedef payı büyüt ve taban yap.
            let js = &mut self.jitter;
            if js.stream_active {
                js.underruns += 1;
                js.floor_slack = (js.target_slack + SLACK_STEP).min(SLACK_MAX);
                js.floor_set = Some(Instant::now());
                js.target_slack = js.floor_slack;
                js.last_underrun = Some(Instant::now());
            }
            js.playing = false;
            self.is_speaking.store(false, Ordering::Relaxed);
            return (0.0, 0.0);
        }

        let raw = self.samples_ring[self.read_pos];
        self.read_pos = (self.read_pos + 1) % self.samples_ring.len();
        self.available_samples -= 1;

        let cfg = self.config.lock().clone();
        if cfg.local_muted {
            return (0.0, 0.0);
        }

        let is_speaking = raw.abs() > 0.005;
        self.is_speaking.store(is_speaking, Ordering::Relaxed);

        // 1. Clarity Boost (if enabled)
        let mut sample = raw;
        if cfg.clarity_boost {
            sample = self.clarity_shelf.lock().process(sample);
        }

        // 2. Loudness Leveler
        sample = self.leveler.lock().process(sample, is_speaking);

        // 3. Peer Volume
        let gain = (cfg.volume_percent / 100.0).clamp(0.0, 2.0);
        sample *= gain;

        // 4. 3D Spatial Panner
        self.spatial_panner.lock().process(sample)
    }

    pub fn is_peer_speaking(&self) -> bool {
        self.is_speaking.load(Ordering::Relaxed)
    }

    pub fn current_gain_db(&self) -> f32 {
        self.leveler.lock().current_gain_db()
    }
}

pub struct ActiveAuxClip {
    samples: Vec<f32>,
    pos: usize,
    is_stereo: bool,
}

pub struct AudioMasterMixer {
    settings: Mutex<AudioSettings>,
    peers: Mutex<HashMap<String, PeerAudioChannel>>,
    local_aux_clips: Mutex<Vec<ActiveAuxClip>>,
    limiter: Mutex<EarProtectionLimiter>,
    is_deafened: AtomicBool,
    // Ölçülen cihaz tamponları (mikrosaniye; 0 = henüz ölçülmedi)
    capture_chunk_us: AtomicU32,
    playback_buffer_us: AtomicU32,
}

impl Default for AudioMasterMixer {
    fn default() -> Self {
        let settings = AudioSettings::default();
        let mut limiter = EarProtectionLimiter::new();
        limiter.set_ceiling_db(settings.ceiling_db);

        Self {
            settings: Mutex::new(settings),
            peers: Mutex::new(HashMap::new()),
            local_aux_clips: Mutex::new(Vec::new()),
            limiter: Mutex::new(limiter),
            is_deafened: AtomicBool::new(false),
            capture_chunk_us: AtomicU32::new(0),
            playback_buffer_us: AtomicU32::new(0),
        }
    }
}

impl AudioMasterMixer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn apply_settings(&self, s: AudioSettings) {
        self.limiter.lock().set_ceiling_db(s.ceiling_db);
        let mut peers = self.peers.lock();
        for peer in peers.values_mut() {
            let cfg = peer.config.lock().clone();
            peer.apply_config(cfg, &s);
        }
        *self.settings.lock() = s;
    }

    pub fn set_deafen(&self, deafened: bool) {
        self.is_deafened.store(deafened, Ordering::SeqCst);
    }

    pub fn play_local_aux(&self, samples: Vec<f32>, is_stereo: bool) {
        if samples.is_empty() { return; }
        let mut clips = self.local_aux_clips.lock();
        clips.push(ActiveAuxClip { samples, pos: 0, is_stereo });
    }

    pub fn play_spatial_test_tone(&self, azimuth_deg: f32, distance: f32) {
        const DURATION_SAMPLES: usize = 48000 * 250 / 1000; // 250 ms
        let mut stereo_samples = vec![0.0_f32; DURATION_SAMPLES * 2];
        let mut test_panner = SpatialPanner::new();
        let speaker_mode = self.settings.lock().spatial_speaker_mode;
        test_panner.configure(true, azimuth_deg, distance, speaker_mode);

        for i in 0..DURATION_SAMPLES {
            let t = i as f32 / 48000.0;
            let freq = if i < DURATION_SAMPLES / 2 { 880.0 } else { 1320.0 };
            let env = (std::f32::consts::PI * i as f32 / DURATION_SAMPLES as f32).sin();
            let tone = (2.0 * std::f32::consts::PI * freq * t).sin() * 0.25 * env;
            let (out_l, out_r) = test_panner.process(tone);
            stereo_samples[i * 2] = out_l;
            stereo_samples[i * 2 + 1] = out_r;
        }

        self.play_local_aux(stereo_samples, true);
    }

    pub fn clear_aux(&self) {
        self.local_aux_clips.lock().clear();
    }

    pub fn push_peer_samples(&self, peer_key: &str, peer_name: &str, samples: &[f32]) {
        if self.is_deafened.load(Ordering::Relaxed) || samples.is_empty() {
            return;
        }

        let mut peers = self.peers.lock();
        let s = self.settings.lock().clone();

        let channel = peers.entry(peer_key.to_string()).or_insert_with(|| {
            PeerAudioChannel::new(
                peer_key.to_string(),
                peer_name.to_string(),
                PeerAudioConfig::default(),
                &s,
            )
        });

        channel.push_samples(samples);
    }

    pub fn update_peer_config(&self, peer_key: &str, cfg: PeerAudioConfig) {
        let s = self.settings.lock().clone();
        let mut peers = self.peers.lock();
        if let Some(peer) = peers.get_mut(peer_key) {
            peer.apply_config(cfg, &s);
        }
    }

    pub fn remove_peer(&self, peer_key: &str) {
        self.peers.lock().remove(peer_key);
    }

    pub fn get_peer_leveling_gain_db(&self, peer_key: &str) -> f32 {
        self.peers.lock().get(peer_key).map(|p| p.current_gain_db()).unwrap_or(0.0)
    }

    /// Yakalama iş parçacığı: bir geri çağrıda gelen ses süresi (üstel ortalama).
    pub fn report_capture_chunk_ms(&self, ms: f64) {
        let new_us = (ms * 1000.0).round().clamp(1.0, 1_000_000.0);
        let old = self.capture_chunk_us.load(Ordering::Relaxed) as f64;
        let v = if old <= 0.0 { new_us } else { old * 0.95 + new_us * 0.05 };
        self.capture_chunk_us.store(v as u32, Ordering::Relaxed);
    }

    /// Çalma iş parçacığı: WASAPI tampon süresi (cihaz kuyruğunda bekleyen en fazla ses).
    pub fn report_playback_buffer_ms(&self, ms: f64) {
        self.playback_buffer_us.store((ms * 1000.0).round().clamp(1.0, 1_000_000.0) as u32, Ordering::Relaxed);
    }

    pub fn capture_buffer_ms(&self) -> f64 {
        let v = self.capture_chunk_us.load(Ordering::Relaxed);
        if v == 0 { -1.0 } else { v as f64 / 1000.0 }
    }

    pub fn playback_buffer_ms(&self) -> f64 {
        let v = self.playback_buffer_us.load(Ordering::Relaxed);
        if v == 0 { -1.0 } else { v as f64 / 1000.0 }
    }

    pub fn get_peer_jitter_stats(&self, peer_key: &str) -> Option<PeerJitterStats> {
        self.peers.lock().get(peer_key).map(|p| p.jitter_stats())
    }

    pub fn get_peer_stats(&self) -> Vec<(String, String, f32, bool)> {
        let peers = self.peers.lock();
        peers.values().map(|p| {
            (p.peer_key.clone(), p.peer_name.clone(), p.current_gain_db(), p.is_peer_speaking())
        }).collect()
    }

    /// Cihaz çıkış arabelleği için stereo [L, R, L, R, ...] örneklerini doldurur
    pub fn render_stereo_samples(&self, out_interleaved: &mut [f32]) {
        let frame_count = out_interleaved.len() / 2;
        if frame_count == 0 {
            return;
        }

        if self.is_deafened.load(Ordering::Relaxed) {
            out_interleaved.fill(0.0);
            return;
        }

        let s = self.settings.lock().clone();
        let master_gain = s.output_master.clamp(0.0, 2.0);

        let mut peers = self.peers.lock();
        let mut aux_clips = self.local_aux_clips.lock();
        let mut limiter = self.limiter.lock();

        for peer in peers.values_mut() {
            peer.begin_block(frame_count);
        }

        for i in 0..frame_count {
            let mut sum_l = 0.0_f32;
            let mut sum_r = 0.0_f32;

            // 1. Peer ses kanallarını topla
            for peer in peers.values_mut() {
                let (pl, pr) = peer.read_sample();
                sum_l += pl;
                sum_r += pr;
            }

            // 2. Yerel aux klipleri (soundpad, arayüz sesleri, mikrofon testi) topla
            for clip in aux_clips.iter_mut() {
                if clip.is_stereo {
                    if clip.pos + 1 < clip.samples.len() {
                        sum_l += clip.samples[clip.pos];
                        sum_r += clip.samples[clip.pos + 1];
                        clip.pos += 2;
                    }
                } else if clip.pos < clip.samples.len() {
                    let s_aux = clip.samples[clip.pos];
                    clip.pos += 1;
                    sum_l += s_aux;
                    sum_r += s_aux;
                }
            }

            // 3. Ana çıkış kazancı
            sum_l *= master_gain;
            sum_r *= master_gain;

            // 4. Kulak koruyucu tavan sınırlayıcı (stereo linked peak)
            limiter.process_stereo(&mut sum_l, &mut sum_r);

            out_interleaved[i * 2] = sum_l;
            out_interleaved[i * 2 + 1] = sum_r;
        }

        // Biten aux kliplerini temizle
        aux_clips.retain(|clip| clip.pos < clip.samples.len());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_master_mixer_smoke() {
        let mixer = AudioMasterMixer::new();
        // Ön tampon: okuma bloğu (480) + başlangıç güvenlik payı (480) dolmadan çalma başlamaz
        mixer.push_peer_samples("p1", "Alice", &[0.2; 1440]);

        let mut out = vec![0.0_f32; 480 * 2];
        mixer.render_stereo_samples(&mut out);

        // First sample of left/right must be non-zero
        assert!(out[0] != 0.0 || out[1] != 0.0, "Mixer must render peer audio");
    }
}
