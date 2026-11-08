use std::time::{Duration, Instant};
use serde::{Deserialize, Serialize};
use tokio::sync::mpsc;
use crate::udp::{UdpPacket, MAX_UDP_PAYLOAD};

pub const PROBE_RATES_KBPS: [u32; 5] = [1000, 2000, 4000, 6000, 8000];
pub const STEP_DURATION_MS: u64 = 500;
pub const PROBE_PACKET_SIZE: usize = MAX_UDP_PAYLOAD; // 1150 bytes

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct QualityProfile {
    pub resolution: String,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub codec: String,
    pub bitrate_kbps: u32,
    pub summary_text: String,
}

pub struct BandwidthProbe {
    pub session_id: String,
    pub target_rates: Vec<u32>,
    pub step_duration: Duration,
}

impl Default for BandwidthProbe {
    fn default() -> Self {
        Self::new("default_session")
    }
}

impl BandwidthProbe {
    pub fn new(session_id: &str) -> Self {
        Self {
            session_id: session_id.to_string(),
            target_rates: PROBE_RATES_KBPS.to_vec(),
            step_duration: Duration::from_millis(STEP_DURATION_MS),
        }
    }

    /// Simulates or executes probe sending burst of packets
    pub async fn run_probe_burst(
        &self,
        target_kbps: u32,
        outgoing_tx: &mpsc::Sender<Vec<u8>>,
    ) -> (u32, Duration) {
        let dummy_payload = vec![0xEEu8; PROBE_PACKET_SIZE - 10];
        let target_bytes = ((target_kbps as u64 * 1000) / 8) * (self.step_duration.as_millis() as u64) / 1000;
        let total_packets = std::cmp::max(1, (target_bytes / PROBE_PACKET_SIZE as u64) as u32);

        let t0 = Instant::now();
        let mut packets_sent = 0u32;

        let interval_us = (self.step_duration.as_micros() as f64) / (total_packets as f64);

        while t0.elapsed() < self.step_duration && packets_sent < total_packets {
            let packet = UdpPacket::new(47998, 0xFFFFFFFF, packets_sent as u16, dummy_payload.clone());
            if outgoing_tx.send(packet.encode()).await.is_err() {
                break;
            }
            packets_sent += 1;

            let target_elapsed_us = (packets_sent as f64) * interval_us;
            let current_elapsed_us = t0.elapsed().as_micros() as f64;
            if target_elapsed_us > current_elapsed_us + 1000.0 {
                tokio::time::sleep(Duration::from_micros((target_elapsed_us - current_elapsed_us) as u64)).await;
            }
        }

        (packets_sent, t0.elapsed())
    }

    pub fn calculate_recommended_profile(
        capacity_kbps: u32,
        common_codecs: &[String],
        user_max_bitrate_kbps: u32,
    ) -> QualityProfile {
        // B = min(0.8 * C - 64, user_max); minimum 1500 kbps (Section 8.2)
        let calc_b = (0.8 * capacity_kbps as f64 - 64.0) as i64;
        let mut recommended_bitrate = std::cmp::max(1500, calc_b) as u32;

        if user_max_bitrate_kbps > 0 {
            recommended_bitrate = std::cmp::min(recommended_bitrate, user_max_bitrate_kbps);
        }
        recommended_bitrate = std::cmp::max(1500, recommended_bitrate);

        // Codec selection: AV1 > HEVC > H264
        let selected_codec = if common_codecs.iter().any(|c| c.eq_ignore_ascii_case("AV1")) {
            "AV1".to_string()
        } else if common_codecs.iter().any(|c| c.eq_ignore_ascii_case("HEVC")) {
            "HEVC".to_string()
        } else {
            "H264".to_string()
        };

        // Resolution and FPS selection (Section 8.3)
        let (res, width, height, fps) = if recommended_bitrate >= 8000 {
            ("1080p", 1920, 1080, 60)
        } else if recommended_bitrate >= 3500 {
            let fps = if selected_codec == "H264" { 30 } else { 60 };
            ("1080p", 1920, 1080, fps)
        } else if recommended_bitrate >= 2000 {
            ("720p", 1280, 720, 60)
        } else {
            ("720p", 1280, 720, 30)
        };

        let summary = format!("{}p{} {} @ {:.1} Mbps", height, fps, selected_codec, recommended_bitrate as f64 / 1000.0);

        QualityProfile {
            resolution: res.to_string(),
            width,
            height,
            fps,
            codec: selected_codec,
            bitrate_kbps: recommended_bitrate,
            summary_text: summary,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_calculate_recommended_profile() {
        let codecs = vec!["H264".to_string(), "HEVC".to_string()];

        // 8000 kbps capacity
        let p8000 = BandwidthProbe::calculate_recommended_profile(8000, &codecs, 10000);
        assert_eq!(p8000.resolution, "1080p");
        assert_eq!(p8000.fps, 60);
        assert_eq!(p8000.codec, "HEVC");
        assert_eq!(p8000.bitrate_kbps, (0.8 * 8000.0 - 64.0) as u32);

        // 2000 kbps capacity (recommended = 0.8 * 2000 - 64 = 1536 kbps => 720p30)
        let p2000 = BandwidthProbe::calculate_recommended_profile(2000, &codecs, 0);
        assert_eq!(p2000.resolution, "720p");
        assert_eq!(p2000.fps, 30);

        // 3000 kbps capacity (recommended = 0.8 * 3000 - 64 = 2336 kbps => 720p60)
        let p3000 = BandwidthProbe::calculate_recommended_profile(3000, &codecs, 0);
        assert_eq!(p3000.resolution, "720p");
        assert_eq!(p3000.fps, 60);
    }
}
