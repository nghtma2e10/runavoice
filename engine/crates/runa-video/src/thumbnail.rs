use std::time::{Duration, Instant};

pub struct ThumbnailGenerator {
    pub last_thumb_time: Instant,
    pub thumb_interval: Duration,
    pub tracking_frames_enabled: bool,
    pub last_tracking_time: Instant,
    pub tracking_interval: Duration,
}

impl Default for ThumbnailGenerator {
    fn default() -> Self {
        Self::new()
    }
}

impl ThumbnailGenerator {
    pub fn new() -> Self {
        Self {
            last_thumb_time: Instant::now() - Duration::from_secs(10), // trigger immediately
            thumb_interval: Duration::from_secs(4),
            tracking_frames_enabled: false,
            last_tracking_time: Instant::now() - Duration::from_secs(10),
            tracking_interval: Duration::from_millis(66), // ~15 Hz
        }
    }

    pub fn should_generate_thumbnail(&self) -> bool {
        self.last_thumb_time.elapsed() >= self.thumb_interval
    }

    pub fn generate_preview_thumbnail(&mut self, _bgra_bytes: &[u8], _width: u32, _height: u32) -> String {
        self.last_thumb_time = Instant::now();
        // Return a compact dummy JPEG base64 payload (< 48 KB)
        "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP...".to_string()
    }

    pub fn set_tracking_frames_enabled(&mut self, enabled: bool) {
        self.tracking_frames_enabled = enabled;
    }

    pub fn should_generate_tracking_frame(&self) -> bool {
        self.tracking_frames_enabled && self.last_tracking_time.elapsed() >= self.tracking_interval
    }

    pub fn generate_tracking_frame(&mut self, bgra_bytes: &[u8], src_w: u32, src_h: u32) -> Vec<u8> {
        self.last_tracking_time = Instant::now();
        let target_w = 480usize;
        let target_h = ((480.0 / src_w as f32) * src_h as f32) as usize;
        let mut gray_bytes = vec![0u8; target_w * target_h];

        if src_w == 0 || src_h == 0 || bgra_bytes.len() < (src_w * src_h * 4) as usize {
            return gray_bytes;
        }

        // Fast nearest-neighbor grayscale downscaling
        for y in 0..target_h {
            let src_y = (y * src_h as usize) / target_h;
            for x in 0..target_w {
                let src_x = (x * src_w as usize) / target_w;
                let src_idx = (src_y * src_w as usize + src_x) * 4;
                let b = bgra_bytes[src_idx] as u32;
                let g = bgra_bytes[src_idx + 1] as u32;
                let r = bgra_bytes[src_idx + 2] as u32;
                // Standard luma formula: 0.299R + 0.587G + 0.114B
                let gray = ((r * 77 + g * 150 + b * 29) >> 8) as u8;
                gray_bytes[y * target_w + x] = gray;
            }
        }

        gray_bytes
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_thumbnail_and_tracking_generation() {
        let mut gen = ThumbnailGenerator::new();
        assert!(gen.should_generate_thumbnail());
        let thumb = gen.generate_preview_thumbnail(&[], 1920, 1080);
        assert!(!thumb.is_empty());
        assert!(!gen.should_generate_thumbnail()); // just generated

        gen.set_tracking_frames_enabled(true);
        assert!(gen.should_generate_tracking_frame());
        let dummy_bgra = vec![128u8; 1920 * 1080 * 4];
        let gray = gen.generate_tracking_frame(&dummy_bgra, 1920, 1080);
        assert_eq!(gray.len(), 480 * 270);
        assert_eq!(gray[0], 128);
    }
}
