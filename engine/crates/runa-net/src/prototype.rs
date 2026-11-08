#![allow(clippy::disallowed_methods)]
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::Mutex;
use tracing::{info, warn};
use webrtc::media::Sample;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::track::track_remote::TrackRemote;
use crate::webrtc_backend::WebRtcEndpoint;
use bytes::Bytes;

#[derive(Debug, Clone, serde::Serialize)]
pub struct BenchmarkResult {
    pub engine_option: String,
    pub connected: bool,
    pub connect_time_ms: f64,
    pub audio_packets_sent: usize,
    pub audio_packets_received: usize,
    pub audio_packet_loss_percent: f64,
    pub video_packets_sent: usize,
    pub video_packets_received: usize,
    pub rtt_ms: f64,
    pub min_rtt_ms: f64,
    pub max_rtt_ms: f64,
    pub dc_messages_exchanged: usize,
    pub cpu_time_ms: f64,
}

pub async fn run_webrtc_rs_loopback_test(
    duration_secs: u64,
    simulated_packet_loss_pct: u32,
) -> Result<BenchmarkResult, Box<dyn std::error::Error + Send + Sync>> {
    info!("Starting WebRTC-RS (Option R) Loopback Benchmark for {}s (simulated loss: {}%)...", duration_secs, simulated_packet_loss_pct);

    let start_time = Instant::now();
    let ep_a = WebRtcEndpoint::create("PeerA", false).await?;
    let ep_b = WebRtcEndpoint::create("PeerB", true).await?;

    // Create data channel on PeerA
    ep_a.create_data_channels().await?;

    let audio_received = Arc::new(AtomicUsize::new(0));
    let video_received = Arc::new(AtomicUsize::new(0));
    let audio_recv_clone = Arc::clone(&audio_received);
    let video_recv_clone = Arc::clone(&video_received);

    // Track on PeerB
    ep_b.pc.on_track(Box::new(move |track: Arc<TrackRemote>, _, _| {
        let kind = track.kind();
        let audio_recv = Arc::clone(&audio_recv_clone);
        let video_recv = Arc::clone(&video_recv_clone);

        tokio::spawn(async move {
            let mut buf = vec![0u8; 2048];
            while let Ok((_rtp_packet, _)) = track.read(&mut buf).await {
                if kind == webrtc::rtp_transceiver::rtp_codec::RTPCodecType::Audio {
                    audio_recv.fetch_add(1, Ordering::Relaxed);
                } else if kind == webrtc::rtp_transceiver::rtp_codec::RTPCodecType::Video {
                    video_recv.fetch_add(1, Ordering::Relaxed);
                }
            }
        });
        Box::pin(async {})
    }));

    // Data channel echo on PeerB
    let rtt_samples = Arc::new(Mutex::new(Vec::<f64>::new()));
    let dc_echo_count = Arc::new(AtomicUsize::new(0));
    let dc_echo_count_b = Arc::clone(&dc_echo_count);

    ep_b.pc.on_data_channel(Box::new(move |dc| {
        let dc_b = Arc::clone(&dc);
        let count_b = Arc::clone(&dc_echo_count_b);
        dc.on_message(Box::new(move |msg| {
            let dc_reply = Arc::clone(&dc_b);
            let count = Arc::clone(&count_b);
            Box::pin(async move {
                count.fetch_add(1, Ordering::Relaxed);
                // Echo back message
                let _ = dc_reply.send(&msg.data).await;
            })
        }));
        Box::pin(async {})
    }));

    // Perform negotiation
    let offer = ep_a.pc.create_offer(None).await?;
    ep_a.pc.set_local_description(offer.clone()).await?;
    ep_b.pc.set_remote_description(offer).await?;

    let answer = ep_b.pc.create_answer(None).await?;
    ep_b.pc.set_local_description(answer.clone()).await?;
    ep_a.pc.set_remote_description(answer).await?;

    // Exchange ICE candidates
    let pc_a_clone = Arc::clone(&ep_a.pc);
    let pc_b_clone = Arc::clone(&ep_b.pc);
    let rx_a = Arc::clone(&ep_a.ice_candidates_rx);
    let rx_b = Arc::clone(&ep_b.ice_candidates_rx);

    tokio::spawn(async move {
        let mut rx = rx_a.lock().await;
        while let Some(cand) = rx.recv().await {
            let _ = pc_b_clone.add_ice_candidate(cand).await;
        }
    });

    tokio::spawn(async move {
        let mut rx = rx_b.lock().await;
        while let Some(cand) = rx.recv().await {
            let _ = pc_a_clone.add_ice_candidate(cand).await;
        }
    });

    // Wait for connection
    let mut connect_time_ms = 0.0;
    let connect_start = Instant::now();
    for _ in 0..100 {
        tokio::time::sleep(Duration::from_millis(50)).await;
        let sa = ep_a.pc.connection_state();
        let sb = ep_b.pc.connection_state();
        if sa == RTCPeerConnectionState::Connected && sb == RTCPeerConnectionState::Connected {
            connect_time_ms = connect_start.elapsed().as_secs_f64() * 1000.0;
            info!("Both PeerConnections CONNECTED in {:.2} ms!", connect_time_ms);
            break;
        }
    }

    let connected = connect_time_ms > 0.0;
    if !connected {
        warn!("Peers did not reach Connected state within timeout!");
    }

    // Set up RTT response handling on PeerA
    if let Some(dc_a) = ep_a.control_channel.lock().await.as_ref() {
        let rtt_collector = Arc::clone(&rtt_samples);
        dc_a.on_message(Box::new(move |msg| {
            if msg.data.len() >= 8 {
                let sent_micros = u64::from_le_bytes(msg.data[0..8].try_into().unwrap());
                let now_micros = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_micros() as u64;
                if now_micros >= sent_micros {
                    let rtt = (now_micros - sent_micros) as f64 / 1000.0;
                    let rtt_col = Arc::clone(&rtt_collector);
                    tokio::spawn(async move {
                        rtt_col.lock().await.push(rtt);
                    });
                }
            }
            Box::pin(async {})
        }));
    }

    // Streaming loop: 100 Hz audio (10 ms frames), 30 Hz video (1080p slices)
    let audio_track = Arc::clone(&ep_a.audio_track);
    let video_track = Arc::clone(&ep_a.video_track);
    let dc_a = Arc::clone(&ep_a.control_channel);

    let audio_sent = Arc::new(AtomicUsize::new(0));
    let video_sent = Arc::new(AtomicUsize::new(0));

    let audio_sent_c = Arc::clone(&audio_sent);
    let video_sent_c = Arc::clone(&video_sent);

    let run_until = Instant::now() + Duration::from_secs(duration_secs);

    // Audio sender task (10 ms period = 100 Hz)
    let audio_task = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_millis(10));
        let dummy_opus_frame = vec![0xFC; 80]; // ~80 bytes typical 10 ms 64 kbps Opus frame
        let mut seq = 0u64;

        while Instant::now() < run_until {
            interval.tick().await;
            seq += 1;
            // Simulated loss
            if simulated_packet_loss_pct > 0 && (seq % 100) < (simulated_packet_loss_pct as u64) {
                continue;
            }
            let sample = Sample {
                data: Bytes::from(dummy_opus_frame.clone()),
                duration: Duration::from_millis(10),
                ..Default::default()
            };
            if audio_track.write_sample(&sample).await.is_ok() {
                audio_sent_c.fetch_add(1, Ordering::Relaxed);
            }
        }
    });

    // Video sender task (33.3 ms period = 30 Hz)
    let video_task = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_millis(33));
        // Simulated H.264 slice (NAL unit header + 1200 bytes)
        let dummy_h264_frame = vec![0x65; 1200];
        let mut seq = 0u64;

        while Instant::now() < run_until {
            interval.tick().await;
            seq += 1;
            if simulated_packet_loss_pct > 0 && (seq % 100) < (simulated_packet_loss_pct as u64) {
                continue;
            }
            let sample = Sample {
                data: Bytes::from(dummy_h264_frame.clone()),
                duration: Duration::from_millis(33),
                ..Default::default()
            };
            if video_track.write_sample(&sample).await.is_ok() {
                video_sent_c.fetch_add(1, Ordering::Relaxed);
            }
        }
    });

    // DataChannel ping task (10 Hz ping with microsecond timestamps)
    let dc_task = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_millis(100));
        while Instant::now() < run_until {
            interval.tick().await;
            if let Some(dc) = dc_a.lock().await.as_ref() {
                let now_micros = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_micros() as u64;
                let payload = now_micros.to_le_bytes();
                let _ = dc.send(&Bytes::from(payload.to_vec())).await;
            }
        }
    });

    let _ = tokio::join!(audio_task, video_task, dc_task);

    // Gather results
    let a_sent = audio_sent.load(Ordering::SeqCst);
    let a_recv = audio_received.load(Ordering::SeqCst);
    let v_sent = video_sent.load(Ordering::SeqCst);
    let v_recv = video_received.load(Ordering::SeqCst);

    let loss_pct = if a_sent > 0 {
        if a_sent >= a_recv {
            ((a_sent - a_recv) as f64 / a_sent as f64) * 100.0
        } else {
            0.0
        }
    } else {
        0.0
    };

    let samples = rtt_samples.lock().await;
    let (avg_rtt, min_rtt, max_rtt) = if !samples.is_empty() {
        let sum: f64 = samples.iter().sum();
        let avg = sum / samples.len() as f64;
        let min = samples.iter().copied().fold(f64::INFINITY, f64::min);
        let max = samples.iter().copied().fold(f64::NEG_INFINITY, f64::max);
        (avg, min, max)
    } else {
        (0.0, 0.0, 0.0)
    };

    // Close connections
    let _ = ep_a.pc.close().await;
    let _ = ep_b.pc.close().await;

    Ok(BenchmarkResult {
        engine_option: "Option R (webrtc-rs)".to_string(),
        connected,
        connect_time_ms,
        audio_packets_sent: a_sent,
        audio_packets_received: a_recv,
        audio_packet_loss_percent: loss_pct,
        video_packets_sent: v_sent,
        video_packets_received: v_recv,
        rtt_ms: avg_rtt,
        min_rtt_ms: min_rtt,
        max_rtt_ms: max_rtt,
        dc_messages_exchanged: samples.len(),
        cpu_time_ms: start_time.elapsed().as_secs_f64() * 1000.0,
    })
}
