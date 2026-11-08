pub mod probe;
pub mod stats;
pub mod tcp;
pub mod tunnel;
pub mod udp;

use std::sync::Arc;
use parking_lot::Mutex;
pub use probe::{BandwidthProbe, QualityProfile};
pub use stats::GameStreamStats;
pub use tunnel::{GameStreamTunnel, TunnelRole};

pub struct GameStreamSubsystem {
    pub active_tunnel: Arc<Mutex<Option<Arc<GameStreamTunnel>>>>,
    handle: tokio::runtime::Handle,
}

impl GameStreamSubsystem {
    pub fn new(handle: tokio::runtime::Handle) -> Self {
        Self {
            active_tunnel: Arc::new(Mutex::new(None)),
            handle,
        }
    }

    pub fn start_tunnel(&self, role: TunnelRole, bind_ip: &str) -> Arc<GameStreamTunnel> {
        let tunnel = Arc::new(GameStreamTunnel::new(role, self.handle.clone()));
        tunnel.is_active.store(true, std::sync::atomic::Ordering::SeqCst);
        let tunnel_clone = tunnel.clone();
        let bind_ip_str = bind_ip.to_string();

        self.handle.spawn(async move {
            tunnel_clone.start(&bind_ip_str).await;
        });

        *self.active_tunnel.lock() = Some(tunnel.clone());
        tunnel
    }

    pub fn stop_tunnel(&self) {
        if let Some(t) = self.active_tunnel.lock().take() {
            t.stop();
        }
    }

    pub fn is_active(&self) -> bool {
        self.active_tunnel.lock().as_ref().map(|t| t.is_active.load(std::sync::atomic::Ordering::Relaxed)).unwrap_or(false)
    }

    pub fn get_stats(&self) -> Option<GameStreamStats> {
        self.active_tunnel.lock().as_ref().map(|t| t.get_stats())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};
    use std::time::Instant;

    #[test]
    fn test_tcp_and_udp_roundtrip() {
        let payload = vec![1, 2, 3, 4, 5, 6, 7, 8];
        let tcp_frame = tcp::TcpFrame::new(tcp::TCP_FRAME_DATA, 100, 47989, payload.clone());
        let encoded_tcp = tcp_frame.encode();
        let decoded_tcp = tcp::TcpFrame::decode(&encoded_tcp).unwrap();
        assert_eq!(decoded_tcp.conn_id, 100);
        assert_eq!(decoded_tcp.payload, payload);

        let udp_pkt = udp::UdpPacket::new(47998, 1, 42, payload.clone());
        let encoded_udp = udp_pkt.encode();
        let decoded_udp = udp::UdpPacket::decode(&encoded_udp).unwrap();
        assert_eq!(decoded_udp.seq, 42);
        assert_eq!(decoded_udp.payload, payload);
    }

    #[tokio::test]
    async fn test_mock_100mb_tcp_integrity_sha256() {
        println!("Running 100 MB TCP data stream integrity benchmark with SHA-256...");

        // Generate 100 MB deterministic data
        // Chunk size 16 KB (MAX_TCP_DATA_CHUNK)
        let chunk_size = tcp::MAX_TCP_DATA_CHUNK;
        let total_bytes = 100 * 1024 * 1024usize; // 104,857,600 bytes
        let total_chunks = total_bytes / chunk_size;

        let mut sender_hasher = Sha256::new();
        let mut receiver_hasher = Sha256::new();

        // Synthetic chunk pattern
        let mut sample_chunk = vec![0u8; chunk_size];
        for (i, b) in sample_chunk.iter_mut().enumerate() {
            *b = (i % 251) as u8;
        }

        let t0 = Instant::now();

        // Pipeline chunks through encode -> simulate WebRTC channel -> decode
        for _chunk_idx in 0..total_chunks {
            // Update sender hash
            sender_hasher.update(&sample_chunk);

            // Encode frame
            let frame = tcp::TcpFrame::new(tcp::TCP_FRAME_DATA, 1, 47989, sample_chunk.clone());
            let wire_bytes = frame.encode();

            // Simulate transport & receiver decode
            let decoded = tcp::TcpFrame::decode(&wire_bytes).expect("Frame decode failed");
            assert_eq!(decoded.conn_id, 1);
            assert_eq!(decoded.port, 47989);

            // Update receiver hash
            receiver_hasher.update(&decoded.payload);
        }

        let elapsed = t0.elapsed();
        let sender_hash = sender_hasher.finalize();
        let receiver_hash = receiver_hasher.finalize();

        assert_eq!(sender_hash, receiver_hash, "SHA-256 data integrity check FAILED!");
        let throughput_mbps = (total_bytes as f64 * 8.0 / (1024.0 * 1024.0)) / elapsed.as_secs_f64();

        println!(
            "100 MB TCP Integrity Check PASSED in {:.3} s (Throughput: {:.2} Mbps, SHA-256: {:x})",
            elapsed.as_secs_f64(),
            throughput_mbps,
            sender_hash
        );
    }

    #[tokio::test]
    async fn test_mock_20mbps_udp_throughput() {
        println!("Running 20 Mbps UDP throughput and packet loss simulation...");

        let target_mbps = 20.0f64;
        let packet_size = 1150usize;
        let test_duration_sec = 0.5f64; // 500 ms test window

        let target_bytes = ((target_mbps * 1_000_000.0 / 8.0) * test_duration_sec) as usize;
        let total_packets = target_bytes / packet_size;

        let dummy_payload = vec![0xABu8; packet_size - 10];
        let mut packets_sent = 0usize;
        let mut packets_received = 0usize;

        let t0 = Instant::now();
        for seq in 0..total_packets {
            let pkt = udp::UdpPacket::new(47998, 1, seq as u16, dummy_payload.clone());
            let wire = pkt.encode();
            packets_sent += 1;

            // Simulate transport
            if let Some(decoded) = udp::UdpPacket::decode(&wire) {
                assert_eq!(decoded.port, 47998);
                packets_received += 1;
            }
        }
        let elapsed = t0.elapsed();

        assert_eq!(packets_sent, packets_received);
        let actual_mbps = (packets_received * packet_size * 8) as f64 / (elapsed.as_secs_f64() * 1_000_000.0);

        println!(
            "20 Mbps UDP Test: Sent {} packets, Received {} packets (Loss: 0.0%), Speed: {:.2} Mbps in {:.3} ms",
            packets_sent,
            packets_received,
            actual_mbps,
            elapsed.as_secs_f64() * 1000.0
        );
    }
}
