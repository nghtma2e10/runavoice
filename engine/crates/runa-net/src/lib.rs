pub mod media_config;
pub mod opus;
pub mod peer;
pub mod prototype;
pub mod webrtc_backend;

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use tokio::sync::mpsc;
use tracing::{info, warn};

pub use opus::{OpusDecoderSession, OpusEncoderSession};
pub use peer::{IceCandidateJson, LatencyReport, PeerEvent, PeerSession, SignalMessagePayload};
use runa_audio::mixer::AudioMasterMixer;
use std::sync::atomic::{AtomicU32, Ordering as AtomicOrdering};

/// Yerel kodlayıcı ölçümleri (mikrosaniye; 0 = henüz ölçülmedi). Karşı tarafa "lat_info" ile bildirilir.
#[derive(Default)]
pub struct CodecMetrics {
    pub encode_us: AtomicU32,
    pub lookahead_us: AtomicU32,
    pub frame_us: AtomicU32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerNetStats {
    pub peer_key: String,
    pub is_connected: bool,
    pub rtt_ms: f32,
    pub jitter_ms: f32,
    pub loss_pct: f32,
    pub control_channel_open: bool,
}

pub struct NetSubsystem {
    peers: Arc<Mutex<HashMap<String, Arc<PeerSession>>>>,
    mixer: Arc<AudioMasterMixer>,
    event_tx: mpsc::UnboundedSender<PeerEvent>,
    event_rx: Arc<tokio::sync::Mutex<mpsc::UnboundedReceiver<PeerEvent>>>,
    encoder: Arc<tokio::sync::Mutex<OpusEncoderSession>>,
    codec_metrics: Arc<CodecMetrics>,
    handle: tokio::runtime::Handle,
}

impl NetSubsystem {
    pub fn new(mixer: Arc<AudioMasterMixer>, handle: tokio::runtime::Handle) -> Result<Self, String> {
        let (event_tx, event_rx) = mpsc::unbounded_channel();
        let enc = OpusEncoderSession::new(48000, 1, 64, 10.0)
            .map_err(|e| format!("Default Opus encoder create failed: {}", e))?;
        let codec_metrics = Arc::new(CodecMetrics::default());
        codec_metrics.lookahead_us.store((enc.lookahead_ms() * 1000.0).round().max(1.0) as u32, AtomicOrdering::Relaxed);
        codec_metrics.frame_us.store((enc.frame_ms() * 1000.0).round().max(1.0) as u32, AtomicOrdering::Relaxed);
        let encoder = Arc::new(tokio::sync::Mutex::new(enc));

        Ok(Self {
            peers: Arc::new(Mutex::new(HashMap::new())),
            mixer,
            event_tx,
            event_rx: Arc::new(tokio::sync::Mutex::new(event_rx)),
            encoder,
            codec_metrics,
            handle,
        })
    }

    pub fn event_receiver(&self) -> Arc<tokio::sync::Mutex<mpsc::UnboundedReceiver<PeerEvent>>> {
        Arc::clone(&self.event_rx)
    }

    pub fn event_sender(&self) -> mpsc::UnboundedSender<PeerEvent> {
        self.event_tx.clone()
    }

    /// Başlatılan Tokioda çalışan ses yayın döngüsü
    pub fn start_audio_broadcast_worker(
        &self,
        mut outgoing_rx: tokio::sync::mpsc::UnboundedReceiver<Vec<i16>>,
    ) {
        let peers_map = Arc::clone(&self.peers);
        let encoder = Arc::clone(&self.encoder);
        let metrics = Arc::clone(&self.codec_metrics);

        self.handle.spawn(async move {
            let mut encoded_packet = [0u8; 1500];
            let mut enc_us_avg = -1.0f64;

            while let Some(pcm_frame) = outgoing_rx.recv().await {
                if pcm_frame.len() != 480 {
                    continue;
                }

                let mut enc_guard = encoder.lock().await;
                let t0 = std::time::Instant::now();
                let enc_res = enc_guard.encode(&pcm_frame, &mut encoded_packet);
                let us = t0.elapsed().as_secs_f64() * 1_000_000.0;
                enc_us_avg = if enc_us_avg < 0.0 { us } else { enc_us_avg * 0.95 + us * 0.05 };
                metrics.encode_us.store(enc_us_avg.round().max(1.0) as u32, AtomicOrdering::Relaxed);
                let enc_len = match enc_res {
                    Ok(len) => len,
                    Err(e) => {
                        warn!("Opus frame encode error: {}", e);
                        continue;
                    }
                };
                drop(enc_guard);

                let sample = webrtc::media::Sample {
                    data: bytes::Bytes::copy_from_slice(&encoded_packet[..enc_len]),
                    duration: Duration::from_millis(10),
                    ..Default::default()
                };

                // Tüm bağlı eşlere yayınla
                let peers: Vec<Arc<PeerSession>> = {
                    let map = peers_map.lock();
                    map.values().cloned().collect()
                };

                for peer in peers {
                    let _ = peer.send_audio_sample(&sample).await;
                }
            }
            info!("Outgoing audio broadcast worker stopped");
        });
    }

    /// 1 Hz Telemetry & STUN RTT döngüsü
    pub fn start_telemetry_worker(&self) {
        let peers_map = Arc::clone(&self.peers);
        let ev_tx = self.event_tx.clone();
        let metrics = Arc::clone(&self.codec_metrics);
        let mixer = Arc::clone(&self.mixer);

        self.handle.spawn(async move {
            let mut interval = tokio::time::interval(Duration::from_secs(1));
            loop {
                interval.tick().await;

                let peers: Vec<Arc<PeerSession>> = {
                    let map = peers_map.lock();
                    map.values().cloned().collect()
                };

                // Karşı taraflara kendi ses kodlama bilgilerimizi bildir (mikrosaniye; 0 = ölçülmedi)
                let cap_ms = mixer.capture_buffer_ms();
                let lat_info = format!(
                    "{{\"type\":\"lat_info\",\"cap_us\":{},\"frame_us\":{},\"la_us\":{},\"enc_us\":{}}}",
                    if cap_ms > 0.0 { (cap_ms * 1000.0).round() as i64 } else { -1 },
                    metrics.frame_us.load(AtomicOrdering::Relaxed),
                    metrics.lookahead_us.load(AtomicOrdering::Relaxed),
                    match metrics.encode_us.load(AtomicOrdering::Relaxed) { 0 => -1, v => v as i64 },
                );

                for peer in peers {
                    if !peer.is_connected.load(std::sync::atomic::Ordering::Relaxed) {
                        continue;
                    }
                    let _ = peer.send_control_message(&lat_info).await;

                    // Ağ gecikmesi: "runa-ping" kanalındaki ölçümlerin medyanı (webrtc-ice 0.12 STUN RTT
                    // alanını doldurmadığı için aday çifti istatistiği kullanılamıyor). Sonra yeni ölçüm gönder.
                    let rtt_ms = peer.network_rtt_ms();
                    peer.send_ping_probe().await;

                    let (jitter_ms, loss_pct, decode_ms, p_cap, p_frame, p_la, p_enc) = {
                        let mut st = peer.audio_stats.lock();
                        st.update_interval_loss();
                        (st.jitter_ms(), st.loss_pct, st.decode_ms(), st.peer_capture_ms, st.peer_frame_ms, st.peer_lookahead_ms, st.peer_encode_ms)
                    };
                    let jb = mixer.get_peer_jitter_stats(&peer.peer_key);

                    if rtt_ms >= 0.0 { *peer.last_rtt_ms.lock() = rtt_ms as f32; }
                    if jitter_ms >= 0.0 { *peer.last_jitter_ms.lock() = jitter_ms as f32; }
                    if loss_pct >= 0.0 { *peer.last_loss_pct.lock() = loss_pct as f32; }

                    let _ = ev_tx.send(PeerEvent::LatencyUpdate(LatencyReport {
                        peer_key: peer.peer_key.clone(),
                        rtt_ms,
                        jitter_ms,
                        loss_pct,
                        peer_capture_ms: p_cap,
                        peer_frame_ms: p_frame,
                        peer_lookahead_ms: p_la,
                        peer_encode_ms: p_enc,
                        local_decode_ms: decode_ms,
                        jitter_buffer_ms: jb.map(|j| j.avg_buffered_ms).unwrap_or(-1.0),
                        jitter_target_ms: jb.map(|j| j.target_ms).unwrap_or(-1.0),
                        underruns: jb.map(|j| j.underruns).unwrap_or(0),
                        playback_buffer_ms: mixer.playback_buffer_ms(),
                    }));
                }
            }
        });
    }

    pub async fn ensure_peer(
        &self,
        peer_key: &str,
        peer_name: &str,
        is_polite: bool,
    ) -> Result<Arc<PeerSession>, Box<dyn std::error::Error + Send + Sync>> {
        {
            let map = self.peers.lock();
            if let Some(existing) = map.get(peer_key) {
                *existing.peer_name.write() = peer_name.to_string();
                return Ok(Arc::clone(existing));
            }
        }

        let session = PeerSession::create(
            peer_key.to_string(),
            peer_name.to_string(),
            is_polite,
            self.event_tx.clone(),
            Arc::clone(&self.mixer),
            self.handle.clone(),
        ).await?;

        {
            let mut map = self.peers.lock();
            map.insert(peer_key.to_string(), Arc::clone(&session));
        }

        if !is_polite {
            // Callee (impolite) waits or callee/polite handles offer
            // In our symmetric WebRTC: polite peer creates offer first, or initiator creates offer
            session.start_initiator_negotiation().await?;
        }

        Ok(session)
    }

    pub async fn handle_remote_signal(
        &self,
        peer_key: &str,
        peer_name: &str,
        sig_type: &str,
        data: &str,
        is_polite: bool,
    ) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let existing = {
            let map = self.peers.lock();
            map.get(peer_key).cloned()
        };

        let session = if let Some(existing) = existing {
            existing
        } else {
            // Section 7: En fazla 24 eş, bilinmeyen anahtardan yalnızca teklif (offer) yeni bağlantı açabilir
            {
                let map = self.peers.lock();
                if map.len() >= 24 {
                    return Err("Peer capacity limit (24) reached".into());
                }
            }
            if sig_type != "sdp-offer" {
                return Err(format!("Unknown peer {} sent unexpected signal: {}", peer_key, sig_type).into());
            }

            let new_session = PeerSession::create(
                peer_key.to_string(),
                peer_name.to_string(),
                is_polite,
                self.event_tx.clone(),
                Arc::clone(&self.mixer),
                self.handle.clone(),
            ).await?;

            let mut map = self.peers.lock();
            map.insert(peer_key.to_string(), Arc::clone(&new_session));
            new_session
        };

        match sig_type {
            "sdp-offer" => {
                session.handle_offer(data).await?;
            }
            "sdp-answer" => {
                session.handle_answer(data).await?;
            }
            "ice-candidate" => {
                session.handle_ice_candidate(data).await?;
            }
            _ => {
                warn!("[Peer {}] Unknown signal type: {}", peer_key, sig_type);
            }
        }

        Ok(())
    }

    pub async fn send_control_message(
        &self,
        peer_key: &str,
        msg: &str,
    ) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let session = {
            let map = self.peers.lock();
            map.get(peer_key).cloned()
        };

        if let Some(session) = session {
            session.send_control_message(msg).await
        } else {
            Err(format!("Peer {} not found", peer_key).into())
        }
    }

    pub async fn broadcast_control_message(
        &self,
        msg: &str,
        except_peer_key: Option<&str>,
    ) {
        let sessions: Vec<Arc<PeerSession>> = {
            let map = self.peers.lock();
            map.values()
                .filter(|s| {
                    if let Some(ex) = except_peer_key {
                        s.peer_key != ex
                    } else {
                        true
                    }
                })
                .cloned()
                .collect()
        };

        for s in sessions {
            let _ = s.send_control_message(msg).await;
        }
    }

    pub fn is_control_open(&self, peer_key: &str) -> bool {
        let map = self.peers.lock();
        if let Some(s) = map.get(peer_key) {
            s.is_control_open()
        } else {
            false
        }
    }

    pub fn has_connected_peers(&self) -> bool {
        let map = self.peers.lock();
        map.values().any(|s| s.is_connected.load(std::sync::atomic::Ordering::Relaxed))
    }

    pub async fn remove_peer(&self, peer_key: &str) {
        let session = {
            let mut map = self.peers.lock();
            map.remove(peer_key)
        };

        if let Some(s) = session {
            s.close().await;
        }
        self.mixer.remove_peer(peer_key);
    }

    pub async fn close_all(&self) {
        let sessions: Vec<Arc<PeerSession>> = {
            let mut map = self.peers.lock();
            map.drain().map(|(_, s)| s).collect()
        };

        for s in sessions {
            s.close().await;
            self.mixer.remove_peer(&s.peer_key);
        }
    }

    pub fn get_peer_stats(&self) -> Vec<PeerNetStats> {
        let map = self.peers.lock();
        map.values().map(|s| {
            PeerNetStats {
                peer_key: s.peer_key.clone(),
                is_connected: s.is_connected.load(std::sync::atomic::Ordering::Relaxed),
                rtt_ms: *s.last_rtt_ms.lock(),
                jitter_ms: *s.last_jitter_ms.lock(),
                loss_pct: *s.last_loss_pct.lock(),
                control_channel_open: s.is_control_open(),
            }
        }).collect()
    }

    pub async fn broadcast_video_sample(&self, sample: &webrtc::media::Sample) {
        let sessions: Vec<Arc<PeerSession>> = {
            let map = self.peers.lock();
            map.values().cloned().collect()
        };
        for s in sessions {
            let _ = s.send_video_sample(sample).await;
        }
    }

    pub async fn ensure_gamestream_channels(&self, peer_key: &str) -> bool {
        let session = {
            let map = self.peers.lock();
            map.get(peer_key).cloned()
        };
        if let Some(s) = session {
            s.ensure_gamestream_channels().await.is_ok()
        } else {
            false
        }
    }

    pub async fn send_gamestream_tcp(&self, peer_key: &str, payload: &[u8]) {
        let session = {
            let map = self.peers.lock();
            map.get(peer_key).cloned()
        };
        if let Some(s) = session {
            let _ = s.send_gamestream_tcp(payload).await;
        }
    }

    pub async fn send_gamestream_udp(&self, peer_key: &str, payload: &[u8]) {
        let session = {
            let map = self.peers.lock();
            map.get(peer_key).cloned()
        };
        if let Some(s) = session {
            let _ = s.send_gamestream_udp(payload).await;
        }
    }
}
