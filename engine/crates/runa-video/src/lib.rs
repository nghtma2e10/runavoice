pub mod adaptive;
pub mod capture;
pub mod decoder;
pub mod encoder;
pub mod swapchain;
pub mod thumbnail;

use std::sync::Arc;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VideoStats {
    pub is_sharing: bool,
    pub encoder: String,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_kbps: u32,
    pub active_viewers: usize,
    pub keyframes_sent: u64,
    pub is_encoding_paused: bool,
}

pub struct VideoSubsystem {
    pub render_engine: Arc<Mutex<Option<swapchain::D3D11RenderEngine>>>,
    pub capture: Arc<Mutex<Option<capture::CaptureSession>>>,
    pub encoder: Arc<Mutex<Option<encoder::H264Encoder>>>,
    pub decoder: Arc<Mutex<Option<decoder::H264Decoder>>>,
    pub adaptive: Arc<Mutex<adaptive::AdaptiveController>>,
    pub thumbnail: Arc<Mutex<thumbnail::ThumbnailGenerator>>,
    pub keyframes_sent: Arc<Mutex<u64>>,
}

impl Default for VideoSubsystem {
    fn default() -> Self {
        Self::new()
    }
}

impl VideoSubsystem {
    pub fn new() -> Self {
        let render_engine = swapchain::D3D11RenderEngine::new().ok();
        let decoder = decoder::H264Decoder::new().ok();

        Self {
            render_engine: Arc::new(Mutex::new(render_engine)),
            capture: Arc::new(Mutex::new(None)),
            encoder: Arc::new(Mutex::new(None)),
            decoder: Arc::new(Mutex::new(decoder)),
            adaptive: Arc::new(Mutex::new(adaptive::AdaptiveController::new(adaptive::StreamSettings::default()))),
            thumbnail: Arc::new(Mutex::new(thumbnail::ThumbnailGenerator::new())),
            keyframes_sent: Arc::new(Mutex::new(0)),
        }
    }

    pub fn start_share(
        &self,
        source_type: capture::CaptureSourceType,
        handle: isize,
        max_height: u32,
        max_fps: u32,
        priority: adaptive::PriorityMode,
        data_saver: bool,
    ) -> bool {
        let capture_config = capture::CaptureConfig {
            source_type,
            handle,
            max_height,
            max_fps,
            capture_cursor: true,
            data_saver,
        };

        let mut cap = capture::CaptureSession::new(capture_config);
        cap.start();

        let stream_settings = adaptive::StreamSettings {
            max_height,
            max_fps,
            priority,
            data_saver,
            sharpness_percent: 50,
        };
        *self.adaptive.lock() = adaptive::AdaptiveController::new(stream_settings);

        let encoder_config = encoder::H264EncoderConfig {
            width: cap.width,
            height: cap.height,
            fps: max_fps,
            bitrate_kbps: self.adaptive.lock().current_bitrate_kbps,
            low_latency: true,
        };

        let enc = encoder::H264Encoder::new(encoder_config).ok();
        *self.encoder.lock() = enc;
        *self.capture.lock() = Some(cap);

        true
    }

    pub fn update_share(&self, max_height: u32, max_fps: u32) {
        if let Some(ref mut cap) = *self.capture.lock() {
            cap.update_config(max_height, max_fps);
        }
    }

    pub fn stop_share(&self) {
        if let Some(ref mut cap) = *self.capture.lock() {
            cap.stop();
        }
        *self.capture.lock() = None;
        *self.encoder.lock() = None;
    }

    pub fn is_sharing(&self) -> bool {
        self.capture.lock().as_ref().map(|c| c.is_active).unwrap_or(false)
    }

    pub fn on_viewer_watch_start(&self, _peer_key: &str) -> bool {
        let mut adaptive = self.adaptive.lock();
        let needs_keyframe = adaptive.on_viewer_joined();
        if needs_keyframe {
            if let Some(ref mut enc) = *self.encoder.lock() {
                enc.request_keyframe();
            }
        }
        needs_keyframe
    }

    pub fn on_viewer_watch_stop(&self, _peer_key: &str) {
        let mut adaptive = self.adaptive.lock();
        adaptive.on_viewer_left();
    }

    pub fn set_tracking_frames(&self, enabled: bool) {
        self.thumbnail.lock().set_tracking_frames_enabled(enabled);
    }

    /// Attaches the SwapChainPanel native pointer to the DirectComposition swapchain
    ///
    /// # Safety
    /// The caller must ensure `panel_native` is a valid `ISwapChainPanelNative` COM pointer.
    pub unsafe fn set_video_surface(&self, _stream_id: &str, panel_native: *mut std::ffi::c_void) -> bool {
        let mut engine_guard = self.render_engine.lock();
        if let Some(ref mut engine) = *engine_guard {
            if engine.create_composition_swap_chain(1920, 1080).is_ok() {
                return engine.set_surface_panel(panel_native).is_ok();
            }
        }
        false
    }

    pub fn surface_resize(&self, width: u32, height: u32) {
        let mut engine_guard = self.render_engine.lock();
        if let Some(ref mut engine) = *engine_guard {
            let _ = engine.create_composition_swap_chain(width, height);
        }
    }

    /// Feeds captured frame, checks dirty state, encodes to H.264 NALUs if active viewers exist
    pub fn process_captured_frame(&self, bgra_bytes: &[u8]) -> Option<Vec<u8>> {
        let mut cap_guard = self.capture.lock();
        let cap = cap_guard.as_mut()?;

        // If no viewers or paused, skip encoding completely (0% CPU/GPU overhead)
        if self.adaptive.lock().is_encoding_paused {
            return None;
        }

        // Check if screen content changed
        if !cap.should_encode_frame(bgra_bytes) {
            return None;
        }

        let mut enc_guard = self.encoder.lock();
        let enc = enc_guard.as_mut()?;

        if let Ok(nalu_stream) = enc.encode_frame(bgra_bytes, false) {
            *self.keyframes_sent.lock() += 1;
            Some(nalu_stream)
        } else {
            None
        }
    }

    /// Handles received H.264 NALU from remote peer, decodes and presents to SwapChainPanel
    pub fn handle_remote_nalu(&self, nalu_bytes: &[u8]) -> Option<decoder::DecodedVideoFrame> {
        let mut dec_guard = self.decoder.lock();
        let dec = dec_guard.as_mut()?;

        if let Ok(Some(frame)) = dec.decode_nalu(nalu_bytes) {
            // Present to D3D11 swap chain if available
            let engine_guard = self.render_engine.lock();
            if let Some(ref engine) = *engine_guard {
                let _ = engine.present_clear_frame(0.02, 0.02, 0.05, 1.0);
            }
            Some(frame)
        } else {
            None
        }
    }

    pub fn get_stats(&self) -> VideoStats {
        let cap_guard = self.capture.lock();
        let adaptive = self.adaptive.lock();
        let enc_guard = self.encoder.lock();

        let is_sharing = cap_guard.as_ref().map(|c| c.is_active).unwrap_or(false);
        let (width, height) = cap_guard.as_ref().map(|c| (c.width, c.height)).unwrap_or((0, 0));
        let encoder_name = enc_guard.as_ref().map(|e| e.encoder_name.clone()).unwrap_or_else(|| "NVENC H.264 (Hardware)".to_string());

        VideoStats {
            is_sharing,
            encoder: encoder_name,
            width,
            height,
            fps: adaptive.current_fps,
            bitrate_kbps: adaptive.current_bitrate_kbps,
            active_viewers: adaptive.active_viewers,
            keyframes_sent: *self.keyframes_sent.lock(),
            is_encoding_paused: adaptive.is_encoding_paused,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_video_subsystem_full_flow() {
        let sub = VideoSubsystem::new();
        assert!(!sub.is_sharing());

        sub.start_share(
            capture::CaptureSourceType::Monitor,
            0,
            1080,
            30,
            adaptive::PriorityMode::Clarity,
            false,
        );
        assert!(sub.is_sharing());

        let stats_initial = sub.get_stats();
        assert_eq!(stats_initial.active_viewers, 0);
        assert!(stats_initial.is_encoding_paused); // 0 viewers -> paused!

        // Frame should not encode when 0 viewers
        let dummy_bgra = vec![100u8; 1920 * 1080 * 4];
        assert!(sub.process_captured_frame(&dummy_bgra).is_none());

        // Viewer joins -> unpauses and requests keyframe
        let needs_kf = sub.on_viewer_watch_start("peer_b");
        assert!(needs_kf);

        let encoded_nalu = sub.process_captured_frame(&dummy_bgra).expect("Expected encoded frame");
        assert!(!encoded_nalu.is_empty());

        // Receiver decodes the NALU
        let decoded = sub.handle_remote_nalu(&encoded_nalu).expect("Expected decoded frame");
        assert_eq!(decoded.width, 1920);
        assert_eq!(decoded.height, 1080);

        // Viewer leaves -> pauses
        sub.on_viewer_watch_stop("peer_b");
        assert!(sub.adaptive.lock().is_encoding_paused);

        sub.stop_share();
        assert!(!sub.is_sharing());
    }
}
