pub mod biquad;
pub mod config;
pub mod denoise;
pub mod leveler;
pub mod mixer;
pub mod pipeline;
pub mod spatial;
pub mod wasapi;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use tracing::info;

pub use config::{AudioSettings, PeerAudioConfig};
pub use mixer::{AudioMasterMixer, PeerAudioChannel};
pub use pipeline::AudioInputPipeline;
pub use wasapi::{AudioDeviceInfo, WasapiCaptureStream, WasapiDeviceManager, WasapiRenderStream};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerLevelInfo {
    pub key: String,
    pub name: String,
    pub gain_db: f32,
    pub is_speaking: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioLevels {
    pub vu: f32,
    pub db: f32,
    pub vad_prob: f32,
    pub is_speaking: bool,
    pub is_recording_mic_test: bool,
    pub mic_test_progress: f32,
    pub peers: Vec<PeerLevelInfo>,
}

pub struct AudioSubsystem {
    pipeline: Arc<Mutex<AudioInputPipeline>>,
    mixer: Arc<AudioMasterMixer>,
    capture_stream: Mutex<Option<WasapiCaptureStream>>,
    render_stream: Mutex<Option<WasapiRenderStream>>,
    input_device_id: Mutex<String>,
    output_device_id: Mutex<String>,
    is_room_active: AtomicBool,
    outgoing_tx: tokio::sync::mpsc::UnboundedSender<Vec<i16>>,
    outgoing_rx: Mutex<Option<tokio::sync::mpsc::UnboundedReceiver<Vec<i16>>>>,
}

impl Default for AudioSubsystem {
    fn default() -> Self {
        Self::new()
    }
}

impl AudioSubsystem {
    pub fn new() -> Self {
        let (outgoing_tx, outgoing_rx) = tokio::sync::mpsc::unbounded_channel();
        let pipeline = Arc::new(Mutex::new(AudioInputPipeline::new()));
        let mixer = Arc::new(AudioMasterMixer::new());

        Self {
            pipeline,
            mixer,
            capture_stream: Mutex::new(None),
            render_stream: Mutex::new(None),
            input_device_id: Mutex::new(String::new()),
            output_device_id: Mutex::new(String::new()),
            is_room_active: AtomicBool::new(false),
            outgoing_tx,
            outgoing_rx: Mutex::new(Some(outgoing_rx)),
        }
    }

    pub fn pipeline(&self) -> Arc<Mutex<AudioInputPipeline>> {
        Arc::clone(&self.pipeline)
    }

    pub fn mixer(&self) -> Arc<AudioMasterMixer> {
        Arc::clone(&self.mixer)
    }

    pub fn take_outgoing_frame_receiver(&self) -> Option<tokio::sync::mpsc::UnboundedReceiver<Vec<i16>>> {
        self.outgoing_rx.lock().take()
    }

    pub fn start_devices(&self) -> Result<(), String> {
        let in_id = self.input_device_id.lock().clone();
        let out_id = self.output_device_id.lock().clone();

        // 1. Start Capture
        let cap_stream = WasapiCaptureStream::start(
            in_id,
            Arc::clone(&self.pipeline),
            self.outgoing_tx.clone(),
            Some(Arc::clone(&self.mixer)),
        ).map_err(|e| format!("Failed to start WASAPI capture: {}", e))?;

        *self.capture_stream.lock() = Some(cap_stream);

        // 2. Start Render
        let rend_stream = WasapiRenderStream::start(
            out_id,
            Arc::clone(&self.mixer),
        ).map_err(|e| format!("Failed to start WASAPI render: {}", e))?;

        *self.render_stream.lock() = Some(rend_stream);

        info!("WASAPI capture and render streams active");
        Ok(())
    }

    pub fn stop_devices(&self) {
        if let Some(mut cap) = self.capture_stream.lock().take() {
            cap.stop();
        }
        if let Some(mut rend) = self.render_stream.lock().take() {
            rend.stop();
        }
        info!("WASAPI audio streams stopped");
    }

    pub fn restart_devices(&self) -> Result<(), String> {
        self.stop_devices();
        self.start_devices()
    }

    pub fn set_devices(&self, in_id: &str, out_id: &str) -> Result<(), String> {
        let mut cur_in = self.input_device_id.lock();
        let mut cur_out = self.output_device_id.lock();
        let changed = *cur_in != in_id || *cur_out != out_id;
        *cur_in = in_id.to_string();
        *cur_out = out_id.to_string();
        drop(cur_in);
        drop(cur_out);

        if changed && (self.capture_stream.lock().is_some() || self.render_stream.lock().is_some()) {
            self.restart_devices()?;
        }
        Ok(())
    }

    pub fn start_room_audio(&self) -> Result<(), String> {
        self.is_room_active.store(true, Ordering::SeqCst);
        if self.capture_stream.lock().is_none() || self.render_stream.lock().is_none() {
            self.start_devices()?;
        }
        Ok(())
    }

    pub fn stop_room_audio(&self) {
        self.is_room_active.store(false, Ordering::SeqCst);
        self.mixer.clear_aux();
    }

    pub fn apply_settings(&self, s: AudioSettings) {
        self.pipeline.lock().apply_settings(s.clone());
        self.mixer.apply_settings(s);
    }

    pub fn set_mute(&self, muted: bool) {
        self.pipeline.lock().set_mute(muted);
    }

    pub fn set_deafen(&self, deafened: bool) {
        self.pipeline.lock().set_deafen(deafened);
        self.mixer.set_deafen(deafened);
    }

    pub fn set_ptt(&self, held: bool) {
        self.pipeline.lock().set_ptt(held);
    }

    pub fn set_mic_monitor(&self, enabled: bool) {
        self.pipeline.lock().set_mic_monitor(enabled);
    }

    pub fn start_mic_test(&self) {
        self.pipeline.lock().start_mic_test_recording();
    }

    pub fn stop_and_play_mic_test(&self) -> bool {
        if let Some(recorded) = self.pipeline.lock().stop_mic_test_recording() {
            self.mixer.play_local_aux(recorded, false);
            true
        } else {
            false
        }
    }

    pub fn stop_mic_test(&self) {
        let _ = self.pipeline.lock().stop_mic_test_recording();
        self.mixer.clear_aux();
    }

    pub fn is_recording_mic_test(&self) -> bool {
        self.pipeline.lock().is_recording_mic_test()
    }

    pub fn play_spatial_test_tone(&self, azimuth_deg: f32, distance: f32) {
        self.mixer.play_spatial_test_tone(azimuth_deg, distance);
    }

    pub fn play_local_aux(&self, samples: Vec<f32>, is_stereo: bool) {
        self.mixer.play_local_aux(samples, is_stereo);
    }

    pub fn clear_aux(&self) {
        self.mixer.clear_aux();
    }

    pub fn update_peer_config(&self, peer_key: &str, cfg: PeerAudioConfig) {
        self.mixer.update_peer_config(peer_key, cfg);
    }

    pub fn remove_peer(&self, peer_key: &str) {
        self.mixer.remove_peer(peer_key);
    }

    pub fn push_peer_samples(&self, peer_key: &str, peer_name: &str, samples: &[f32]) {
        self.mixer.push_peer_samples(peer_key, peer_name, samples);
    }

    pub fn get_peer_leveling_gain_db(&self, peer_key: &str) -> f32 {
        self.mixer.get_peer_leveling_gain_db(peer_key)
    }

    pub fn get_audio_levels(&self) -> AudioLevels {
        let pipe = self.pipeline.lock();
        let vu = pipe.current_vu();
        let db = pipe.current_db();
        let vad_prob = pipe.current_vad_prob();
        let is_recording_mic_test = pipe.is_recording_mic_test();
        let mic_test_progress = pipe.mic_test_progress();
        drop(pipe);

        let peer_stats = self.mixer.get_peer_stats();
        let peers = peer_stats
            .into_iter()
            .map(|(key, name, gain_db, is_speaking)| PeerLevelInfo {
                key,
                name,
                gain_db,
                is_speaking,
            })
            .collect();

        AudioLevels {
            vu,
            db,
            vad_prob,
            is_speaking: vu > 0.05,
            is_recording_mic_test,
            mic_test_progress,
            peers,
        }
    }
}
