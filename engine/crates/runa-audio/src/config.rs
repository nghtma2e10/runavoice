use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioSettings {
    #[serde(default = "default_input_gain")]
    pub input_gain: f32, // 0.0 - 2.5 (100% = 1.0)

    #[serde(default = "default_output_master")]
    pub output_master: f32, // 0.0 - 2.0 (100% = 1.0)

    #[serde(default = "default_ceiling_db")]
    pub ceiling_db: f32, // -24.0 to 0.0 dBFS

    #[serde(default = "default_ns_engine")]
    pub ns_engine: String, // "rnnoise" | "deep" | "speex" | "off"

    #[serde(default = "default_ns_level")]
    pub ns_level: i32, // 1 - 5

    #[serde(default = "default_impact_guard")]
    pub impact_guard: bool,

    #[serde(default = "default_headphone_aec")]
    pub headphone_aec: bool,

    #[serde(default = "default_eq_profile")]
    pub eq_profile: String, // "clean" | "podcast" | "clarity" | "deep" | "flat"

    #[serde(default = "default_eq_bands")]
    pub eq_bands: [f32; 5], // 80Hz, 300Hz, 1kHz, 3.5kHz, 8kHz

    #[serde(default = "default_gate_mode")]
    pub gate_mode: String, // "vad" | "ptt"

    #[serde(default = "default_vad_threshold_db")]
    pub vad_threshold_db: f32, // -60.0 to 0.0 dB

    #[serde(default = "default_opus_bitrate_kbps")]
    pub opus_bitrate_kbps: i32, // 32 - 128

    #[serde(default = "default_opus_frame_ms")]
    pub opus_frame_ms: i32, // 5 | 10 | 20

    #[serde(default = "default_leveling_mode")]
    pub leveling_mode: String, // "normal" | "light" | "strong" | "off"

    #[serde(default = "default_leveling_target_db")]
    pub leveling_target_db: f32, // -30.0 to -10.0 dBFS

    #[serde(default = "default_spatial_enabled")]
    pub spatial_enabled: bool,

    #[serde(default = "default_spatial_speaker_mode")]
    pub spatial_speaker_mode: bool,
}

fn default_input_gain() -> f32 { 1.0 }
fn default_output_master() -> f32 { 1.0 }
fn default_ceiling_db() -> f32 { -2.0 }
fn default_ns_engine() -> String { "rnnoise".to_string() }
fn default_ns_level() -> i32 { 3 }
fn default_impact_guard() -> bool { true }
fn default_headphone_aec() -> bool { false }
fn default_eq_profile() -> String { "clean".to_string() }
fn default_eq_bands() -> [f32; 5] { [-3.0, -1.5, 1.5, 3.0, 2.0] }
fn default_gate_mode() -> String { "vad".to_string() }
fn default_vad_threshold_db() -> f32 { -42.0 }
fn default_opus_bitrate_kbps() -> i32 { 64 }
fn default_opus_frame_ms() -> i32 { 10 }
fn default_leveling_mode() -> String { "normal".to_string() }
fn default_leveling_target_db() -> f32 { -22.0 }
fn default_spatial_enabled() -> bool { true }
fn default_spatial_speaker_mode() -> bool { false }

impl Default for AudioSettings {
    fn default() -> Self {
        Self {
            input_gain: default_input_gain(),
            output_master: default_output_master(),
            ceiling_db: default_ceiling_db(),
            ns_engine: default_ns_engine(),
            ns_level: default_ns_level(),
            impact_guard: default_impact_guard(),
            headphone_aec: default_headphone_aec(),
            eq_profile: default_eq_profile(),
            eq_bands: default_eq_bands(),
            gate_mode: default_gate_mode(),
            vad_threshold_db: default_vad_threshold_db(),
            opus_bitrate_kbps: default_opus_bitrate_kbps(),
            opus_frame_ms: default_opus_frame_ms(),
            leveling_mode: default_leveling_mode(),
            leveling_target_db: default_leveling_target_db(),
            spatial_enabled: default_spatial_enabled(),
            spatial_speaker_mode: default_spatial_speaker_mode(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerAudioConfig {
    #[serde(default = "default_peer_volume")]
    pub volume_percent: f32, // 0.0 - 200.0

    #[serde(default)]
    pub local_muted: bool,

    #[serde(default)]
    pub clarity_boost: bool,

    #[serde(default)]
    pub exclude_from_leveling: bool,

    #[serde(default)]
    pub spatial_azimuth_deg: f32, // -180.0 to 180.0

    #[serde(default = "default_peer_distance")]
    pub spatial_distance: f32, // 0.0 to 1.0
}

fn default_peer_volume() -> f32 { 100.0 }
fn default_peer_distance() -> f32 { 0.2 }

impl Default for PeerAudioConfig {
    fn default() -> Self {
        Self {
            volume_percent: default_peer_volume(),
            local_muted: false,
            clarity_boost: false,
            exclude_from_leveling: false,
            spatial_azimuth_deg: 0.0,
            spatial_distance: default_peer_distance(),
        }
    }
}
