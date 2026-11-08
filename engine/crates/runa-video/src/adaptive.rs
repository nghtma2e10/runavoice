#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PriorityMode {
    Clarity,
    Fluidity,
}

#[derive(Debug, Clone)]
pub struct StreamSettings {
    pub max_height: u32,
    pub max_fps: u32,
    pub priority: PriorityMode,
    pub data_saver: bool,
    pub sharpness_percent: u32,
}

impl Default for StreamSettings {
    fn default() -> Self {
        Self {
            max_height: 1080,
            max_fps: 30,
            priority: PriorityMode::Clarity,
            data_saver: false,
            sharpness_percent: 50,
        }
    }
}

pub struct AdaptiveController {
    pub settings: StreamSettings,
    pub active_viewers: usize,
    pub current_height: u32,
    pub current_fps: u32,
    pub current_bitrate_kbps: u32,
    pub is_encoding_paused: bool,
}

impl AdaptiveController {
    pub fn new(settings: StreamSettings) -> Self {
        let (height, fps, bitrate) = Self::compute_initial_profile(&settings);
        Self {
            settings,
            active_viewers: 0,
            current_height: height,
            current_fps: fps,
            current_bitrate_kbps: bitrate,
            is_encoding_paused: true, // paused until first viewer joins
        }
    }

    fn compute_initial_profile(s: &StreamSettings) -> (u32, u32, u32) {
        if s.data_saver {
            (720, 15, 1200)
        } else {
            match s.max_height {
                720 => (720, s.max_fps.min(30), 2000),
                480 => (480, s.max_fps.min(30), 1000),
                _ => (1080, s.max_fps, 3500),
            }
        }
    }

    pub fn on_viewer_joined(&mut self) -> bool {
        self.active_viewers += 1;
        if self.is_encoding_paused {
            self.is_encoding_paused = false;
            true // needs keyframe immediately!
        } else {
            false
        }
    }

    pub fn on_viewer_left(&mut self) {
        if self.active_viewers > 0 {
            self.active_viewers -= 1;
        }
        if self.active_viewers == 0 {
            self.is_encoding_paused = true;
        }
    }

    pub fn handle_packet_loss(&mut self, loss_percent: f32) {
        if loss_percent > 10.0 {
            // Severe congestion: drop resolution and bitrate
            if self.settings.priority == PriorityMode::Fluidity {
                self.current_height = 480;
                self.current_fps = 30;
                self.current_bitrate_kbps = 800;
            } else {
                self.current_height = 720;
                self.current_fps = 15;
                self.current_bitrate_kbps = 1000;
            }
        } else if loss_percent > 3.0 {
            // Moderate congestion
            if self.settings.priority == PriorityMode::Fluidity {
                self.current_height = 720;
                self.current_fps = 30;
                self.current_bitrate_kbps = 1500;
            } else {
                self.current_height = 1080;
                self.current_fps = 15;
                self.current_bitrate_kbps = 2000;
            }
        } else {
            // Good network: restore to max target
            let (h, f, b) = Self::compute_initial_profile(&self.settings);
            self.current_height = h;
            self.current_fps = f;
            self.current_bitrate_kbps = b;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_adaptive_viewer_pause_and_congestion_adaptation() {
        let mut ctrl = AdaptiveController::new(StreamSettings::default());
        assert!(ctrl.is_encoding_paused); // 0 viewers -> paused!

        let needs_keyframe = ctrl.on_viewer_joined();
        assert!(needs_keyframe);
        assert!(!ctrl.is_encoding_paused);
        assert_eq!(ctrl.active_viewers, 1);

        // Network loss test
        ctrl.handle_packet_loss(12.0);
        assert!(ctrl.current_bitrate_kbps <= 1000);

        ctrl.on_viewer_left();
        assert_eq!(ctrl.active_viewers, 0);
        assert!(ctrl.is_encoding_paused); // 0 viewers -> paused!
    }
}
