use windows::{
    Win32::Foundation::*,
    Win32::UI::WindowsAndMessaging::*,
};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CaptureSourceType {
    Monitor,
    Window,
}

#[derive(Debug, Clone)]
pub struct CaptureConfig {
    pub source_type: CaptureSourceType,
    pub handle: isize,
    pub max_height: u32,
    pub max_fps: u32,
    pub capture_cursor: bool,
    pub data_saver: bool,
}

impl Default for CaptureConfig {
    fn default() -> Self {
        Self {
            source_type: CaptureSourceType::Monitor,
            handle: 0,
            max_height: 1080,
            max_fps: 30,
            capture_cursor: true,
            data_saver: false,
        }
    }
}

pub struct CaptureSession {
    pub config: CaptureConfig,
    pub is_active: bool,
    pub width: u32,
    pub height: u32,
    pub last_frame_hash: u64,
}

impl CaptureSession {
    pub fn new(config: CaptureConfig) -> Self {
        let (width, height) = match config.max_height {
            720 => (1280, 720),
            480 => (854, 480),
            _ => (1920, 1080),
        };

        // If window capture and window is minimized, restore without stealing focus
        if config.source_type == CaptureSourceType::Window && config.handle != 0 {
            let hwnd = HWND(config.handle as _);
            // SAFETY: Calling documented IsIconic and ShowWindow Win32 APIs
            unsafe {
                if IsIconic(hwnd).as_bool() {
                    let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                }
            }
        }

        Self {
            config,
            is_active: false,
            width,
            height,
            last_frame_hash: 0,
        }
    }

    pub fn start(&mut self) {
        self.is_active = true;
    }

    pub fn stop(&mut self) {
        self.is_active = false;
    }

    pub fn update_config(&mut self, max_height: u32, max_fps: u32) {
        self.config.max_height = max_height;
        self.config.max_fps = max_fps;
        let (width, height) = match max_height {
            720 => (1280, 720),
            480 => (854, 480),
            _ => (1920, 1080),
        };
        self.width = width;
        self.height = height;
    }

    /// Checks if the captured frame content has changed.
    /// Returns true if frame should be encoded, false if static (frame skipped).
    pub fn should_encode_frame(&mut self, frame_bytes: &[u8]) -> bool {
        if !self.is_active || frame_bytes.is_empty() {
            return false;
        }

        // 64-bit sampling hash across 128 strategic points
        let mut hash: u64 = 0xcbf29ce484222325;
        let sample_count = 128.min(frame_bytes.len());
        let step = (frame_bytes.len() / sample_count).max(1);
        for i in 0..sample_count {
            let idx = (i * step).min(frame_bytes.len() - 1);
            hash ^= frame_bytes[idx] as u64;
            hash = hash.wrapping_mul(0x100000001b3);
        }

        // Also mix in first and middle bytes
        hash ^= frame_bytes[0] as u64;
        hash = hash.wrapping_mul(0x100000001b3);

        if hash == self.last_frame_hash {
            // Static content: skip redundant encoding
            return false;
        }

        self.last_frame_hash = hash;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_capture_session_lifecycle_and_dirty_check() {
        let mut session = CaptureSession::new(CaptureConfig::default());
        session.start();
        assert!(session.is_active);

        let frame1 = vec![120u8; 1920 * 1080 * 4];
        assert!(session.should_encode_frame(&frame1));

        // Same frame: should skip
        assert!(!session.should_encode_frame(&frame1));

        // Changed frame: should encode
        let mut frame2 = frame1.clone();
        frame2[0] = 200;
        assert!(session.should_encode_frame(&frame2));

        session.stop();
        assert!(!session.should_encode_frame(&frame2));
    }
}
