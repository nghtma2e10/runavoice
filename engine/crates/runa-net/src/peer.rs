use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Weak};
use std::time::Duration;
use bytes::Bytes;
use parking_lot::{Mutex, RwLock};
use serde::{Deserialize, Serialize};
use tokio::sync::mpsc;
use tracing::{info, warn};

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::MediaEngine;
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_init::RTCDataChannelInit;
use webrtc::data_channel::data_channel_message::DataChannelMessage;
use webrtc::data_channel::data_channel_state::RTCDataChannelState;
use webrtc::data_channel::RTCDataChannel;
use webrtc::ice_transport::ice_candidate::RTCIceCandidateInit;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::interceptor::registry::Registry;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::signaling_state::RTCSignalingState;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::RTPCodecType;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;
use webrtc::track::track_remote::TrackRemote;

use crate::media_config::{configure_media_engine, create_audio_track, create_video_track};
use crate::opus::OpusDecoderSession;
use runa_audio::mixer::AudioMasterMixer;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SignalMessagePayload {
    pub peer_key: String,
    pub sig_type: String,
    pub data: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IceCandidateJson {
    pub candidate: String,
    #[serde(rename = "sdpMid", alias = "sdp_mid")]
    pub sdp_mid: Option<String>,
    #[serde(rename = "sdpMLineIndex", alias = "sdp_mline_index")]
    pub sdp_mline_index: Option<u16>,
    #[serde(rename = "usernameFragment", alias = "username_fragment")]
    pub username_fragment: Option<String>,
}

/// Bir eşle ilgili gecikme ölçümleri (ms; ölçülemeyen alan -1). C#'taki PeerLatencyInfo'ya eşlenir.
#[derive(Debug, Clone, Serialize)]
pub struct LatencyReport {
    pub peer_key: String,
    pub rtt_ms: f64,              // seçili ICE aday çiftinin STUN gidiş-dönüşü (yalnız ağ)
    pub jitter_ms: f64,           // gelen ses RTP'sinin RFC 3550 dalgalanması
    pub loss_pct: f64,            // son 1 sn'de RTP sıra numaralarından hesaplanan kayıp
    pub peer_capture_ms: f64,     // karşı tarafın mikrofon parça süresi (bildirilen)
    pub peer_frame_ms: f64,       // karşı tarafın Opus çerçeve süresi (bildirilen)
    pub peer_lookahead_ms: f64,   // karşı tarafın Opus algoritmik gecikmesi (bildirilen)
    pub peer_encode_ms: f64,      // karşı tarafın ölçülen kodlama süresi (bildirilen)
    pub local_decode_ms: f64,     // bu eşin sesinin bizdeki ölçülen çözme süresi
    pub jitter_buffer_ms: f64,    // alış tamponunda ortalama bekleme
    pub jitter_target_ms: f64,    // uyarlanabilir tamponun güvenlik payı
    pub underruns: u32,           // alış tamponu boşalma (kesinti) sayısı
    pub playback_buffer_ms: f64,  // WASAPI çalma tamponu
}

/// Eş başına ses istatistikleri (ses izi okuyucusu ve kontrol kanalı tarafından güncellenir).
#[derive(Debug)]
pub struct PeerAudioStats {
    pub decode_us_avg: f64,
    // RTP sıra/zaman damgası takibi (kayıp ve dalgalanma)
    has_seq: bool,
    base_seq: u32,
    max_seq_low: u16,
    max_seq_ext: u32,
    received: u32,
    expected_prior: u32,
    received_prior: u32,
    last_transit: Option<f64>,
    jitter_samples: f64,
    start: std::time::Instant,
    pub loss_pct: f64,
    // Karşı tarafın "lat_info" ile bildirdiği değerler (ms; -1 = bilinmiyor)
    pub peer_capture_ms: f64,
    pub peer_frame_ms: f64,
    pub peer_lookahead_ms: f64,
    pub peer_encode_ms: f64,
}

impl PeerAudioStats {
    pub fn new() -> Self {
        Self {
            decode_us_avg: -1.0,
            has_seq: false,
            base_seq: 0,
            max_seq_low: 0,
            max_seq_ext: 0,
            received: 0,
            expected_prior: 0,
            received_prior: 0,
            last_transit: None,
            jitter_samples: -1.0,
            start: std::time::Instant::now(),
            loss_pct: -1.0,
            peer_capture_ms: -1.0,
            peer_frame_ms: -1.0,
            peer_lookahead_ms: -1.0,
            peer_encode_ms: -1.0,
        }
    }

    /// Gelen her ses RTP paketi için (RFC 3550: genişletilmiş sıra numarası ve dalgalanma).
    pub fn on_rtp(&mut self, seq: u16, rtp_ts: u32, decode_us: f64) {
        self.decode_us_avg = if self.decode_us_avg < 0.0 { decode_us } else { self.decode_us_avg * 0.95 + decode_us * 0.05 };

        if !self.has_seq {
            self.has_seq = true;
            self.base_seq = seq as u32;
            self.max_seq_low = seq;
            self.max_seq_ext = seq as u32;
        } else {
            let diff = seq.wrapping_sub(self.max_seq_low) as i16;
            if diff > 0 {
                self.max_seq_ext = self.max_seq_ext.wrapping_add(diff as u32);
                self.max_seq_low = seq;
            }
        }
        self.received = self.received.wrapping_add(1);

        let arrival = self.start.elapsed().as_secs_f64() * 48000.0;
        let transit = arrival - rtp_ts as f64;
        if let Some(prev) = self.last_transit {
            let mut d = transit - prev;
            if d < 0.0 { d = -d; }
            // Aşırı sıçramaları (ör. RTP zaman damgası sıfırlanması) dalgalanmaya katma
            if d < 48000.0 {
                self.jitter_samples = if self.jitter_samples < 0.0 { d } else { self.jitter_samples + (d - self.jitter_samples) / 16.0 };
            }
        }
        self.last_transit = Some(transit);
    }

    /// 1 sn'lik aralık için kaybı günceller (telemetri döngüsü çağırır).
    pub fn update_interval_loss(&mut self) {
        if !self.has_seq { return; }
        let expected = self.max_seq_ext.wrapping_sub(self.base_seq).wrapping_add(1);
        let exp_int = expected.wrapping_sub(self.expected_prior) as i64;
        let rec_int = self.received.wrapping_sub(self.received_prior) as i64;
        self.expected_prior = expected;
        self.received_prior = self.received;
        if exp_int > 0 {
            let lost = (exp_int - rec_int).max(0);
            self.loss_pct = (lost as f64 * 100.0 / exp_int as f64).clamp(0.0, 100.0);
        }
    }

    pub fn jitter_ms(&self) -> f64 {
        if self.jitter_samples < 0.0 { -1.0 } else { self.jitter_samples / 48.0 }
    }

    pub fn decode_ms(&self) -> f64 {
        if self.decode_us_avg < 0.0 { -1.0 } else { self.decode_us_avg / 1000.0 }
    }
}

#[derive(Debug, Clone)]
pub enum PeerEvent {
    LocalSignal(SignalMessagePayload),
    StateChanged { peer_key: String, state: String },
    ControlChannelOpened { peer_key: String },
    DataChannelMessage { peer_key: String, channel: String, data: String },
    LatencyUpdate(LatencyReport),
    RemoteVideoPacket { peer_key: String, payload: Vec<u8> },
    GameStreamTcpPacket { peer_key: String, payload: Vec<u8> },
    GameStreamUdpPacket { peer_key: String, payload: Vec<u8> },
}

pub struct PeerSession {
    pub peer_key: String,
    pub peer_name: RwLock<String>,
    pub is_polite: bool,
    pc_slot: Arc<RwLock<Arc<RTCPeerConnection>>>,
    pub audio_track: Arc<TrackLocalStaticSample>,
    pub video_track: Arc<TrackLocalStaticSample>,
    pub control_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
    pub gs_tcp_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
    pub gs_udp_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
    pub decoder: Arc<tokio::sync::Mutex<OpusDecoderSession>>,

    pub making_offer: AtomicBool,
    pub ignore_offer: AtomicBool,
    pub has_remote_description: AtomicBool,
    pub is_connected: AtomicBool,
    pub is_closed: AtomicBool,

    pub early_ice_candidates: Mutex<Vec<RTCIceCandidateInit>>,
    pub local_offer_sdp: Mutex<Option<String>>,
    pub local_answer_sdp: Mutex<Option<String>>,
    pub last_applied_remote_offer_sdp: Mutex<Option<String>>,
    pub last_applied_remote_answer_sdp: Mutex<Option<String>>,
    pub remote_ice_ufrag: Mutex<Option<String>>,
    pub local_ice_candidates: Mutex<Vec<RTCIceCandidateInit>>,
    pub offer_retry_cancel: Arc<tokio::sync::Notify>,

    pub last_rtt_ms: Mutex<f32>,
    pub last_jitter_ms: Mutex<f32>,
    pub last_loss_pct: Mutex<f32>,
    pub audio_stats: Arc<Mutex<PeerAudioStats>>,
    // Ağ gecikmesi ölçümü: sırasız ve yeniden göndermesiz "runa-ping" veri kanalı, monoton µs saat.
    // (webrtc-ice 0.12 STUN aday çifti RTT'sini hesaplamıyor — alan her zaman 0.)
    pub ping_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
    pub ping_samples: Mutex<std::collections::VecDeque<f64>>,
    pub epoch: std::time::Instant,

    pub mixer: Arc<AudioMasterMixer>,
    pub handle: tokio::runtime::Handle,
    pub weak_self: RwLock<Weak<PeerSession>>,
    event_tx: mpsc::UnboundedSender<PeerEvent>,
}

fn extract_ice_ufrag(sdp: &str) -> Option<String> {
    for line in sdp.lines() {
        let trimmed = line.trim();
        if let Some(ufrag) = trimmed.strip_prefix("a=ice-ufrag:") {
            return Some(ufrag.trim().to_string());
        }
    }
    None
}

impl PeerSession {
    pub fn pc(&self) -> Arc<RTCPeerConnection> {
        self.pc_slot.read().clone()
    }

    pub fn local_ice_ufrag(&self) -> Option<String> {
        if let Some(ref ans) = *self.local_answer_sdp.lock() {
            return extract_ice_ufrag(ans);
        }
        if let Some(ref offer) = *self.local_offer_sdp.lock() {
            return extract_ice_ufrag(offer);
        }
        None
    }

    async fn create_raw_pc(
        audio_track: &Arc<TrackLocalStaticSample>,
        video_track: &Arc<TrackLocalStaticSample>,
    ) -> Result<Arc<RTCPeerConnection>, Box<dyn std::error::Error + Send + Sync>> {
        let mut m = MediaEngine::default();
        configure_media_engine(&mut m)?;

        let mut registry = Registry::new();
        registry = register_default_interceptors(registry, &mut m)?;

        let api = APIBuilder::new()
            .with_media_engine(m)
            .with_interceptor_registry(registry)
            .build();

        let config = RTCConfiguration {
            ice_servers: vec![
                RTCIceServer {
                    urls: vec![
                        "stun:stun.l.google.com:19302".to_string(),
                        "stun:stun1.l.google.com:19302".to_string(),
                        "stun:stun.cloudflare.com:3478".to_string(),
                    ],
                    ..Default::default()
                },
            ],
            ..Default::default()
        };

        let pc = Arc::new(api.new_peer_connection(config).await?);
        pc.add_track(Arc::clone(audio_track) as Arc<dyn TrackLocal + Send + Sync>).await?;
        pc.add_track(Arc::clone(video_track) as Arc<dyn TrackLocal + Send + Sync>).await?;

        Ok(pc)
    }

    #[allow(clippy::too_many_arguments)]
    fn attach_pc_callbacks(
        pc: &Arc<RTCPeerConnection>,
        peer_key: String,
        session_weak: Weak<PeerSession>,
        control_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
        gs_tcp_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
        gs_udp_channel: Arc<tokio::sync::Mutex<Option<Arc<RTCDataChannel>>>>,
        mixer: Arc<AudioMasterMixer>,
        decoder: Arc<tokio::sync::Mutex<OpusDecoderSession>>,
        event_tx: mpsc::UnboundedSender<PeerEvent>,
        handle: tokio::runtime::Handle,
    ) {
        // ICE Candidate callback
        let pkey_ice = peer_key.clone();
        let ev_ice = event_tx.clone();
        let s_weak_ice = session_weak.clone();
        pc.on_ice_candidate(Box::new(move |candidate| {
            if let Some(cand) = candidate {
                if let Ok(mut json_cand) = cand.to_json() {
                    if let Some(s) = s_weak_ice.upgrade() {
                        if json_cand.username_fragment.is_none() {
                            json_cand.username_fragment = s.local_ice_ufrag();
                        }
                        s.local_ice_candidates.lock().push(json_cand.clone());
                    }
                    let cand_str = serde_json::to_string(&json_cand).unwrap_or_default();
                    let _ = ev_ice.send(PeerEvent::LocalSignal(SignalMessagePayload {
                        peer_key: pkey_ice.clone(),
                        sig_type: "ice-candidate".to_string(),
                        data: cand_str,
                    }));
                }
            }
            Box::pin(async {})
        }));

        // State change callback
        let pkey_state = peer_key.clone();
        let ev_state = event_tx.clone();
        let s_weak_state = session_weak.clone();
        pc.on_peer_connection_state_change(Box::new(move |state: RTCPeerConnectionState| {
            let state_str = match state {
                RTCPeerConnectionState::Connecting => "connecting",
                RTCPeerConnectionState::Connected => "connected",
                RTCPeerConnectionState::Disconnected => "disconnected",
                RTCPeerConnectionState::Failed => "failed",
                RTCPeerConnectionState::Closed => "closed",
                _ => "new",
            };
            info!("[Peer {}] State -> {}", pkey_state, state_str);

            if let Some(s) = s_weak_state.upgrade() {
                if state == RTCPeerConnectionState::Connected {
                    s.is_connected.store(true, Ordering::SeqCst);
                } else if state == RTCPeerConnectionState::Failed || state == RTCPeerConnectionState::Closed {
                    s.is_connected.store(false, Ordering::SeqCst);
                }
            }

            let _ = ev_state.send(PeerEvent::StateChanged {
                peer_key: pkey_state.clone(),
                state: state_str.to_string(),
            });

            Box::pin(async {})
        }));

        // Incoming DataChannels
        let cc_slot = Arc::clone(&control_channel);
        let gs_tcp_slot = Arc::clone(&gs_tcp_channel);
        let gs_udp_slot = Arc::clone(&gs_udp_channel);
        let ev_dc = event_tx.clone();
        let pkey_dc = peer_key.clone();
        let s_weak_dc = session_weak.clone();
        let handle_dc = handle.clone();
        pc.on_data_channel(Box::new(move |dc: Arc<RTCDataChannel>| {
            let label = dc.label().to_string();
            info!("[Peer {}] Incoming DataChannel: {}", pkey_dc, label);

            if label == "runa-control" {
                let cc_slot = Arc::clone(&cc_slot);
                let pkey_dc = pkey_dc.clone();
                let ev_dc = ev_dc.clone();
                let s_weak = s_weak_dc.clone();
                let h_dc = handle_dc.clone();

                handle_dc.spawn(async move {
                    *cc_slot.lock().await = Some(Arc::clone(&dc));
                    Self::setup_dc_handlers(&dc, &pkey_dc, ev_dc, s_weak, h_dc).await;
                });
            } else if label == "runa-ping" {
                let s_weak = s_weak_dc.clone();
                handle_dc.spawn(async move {
                    if let Some(s) = s_weak.upgrade() {
                        *s.ping_channel.lock().await = Some(Arc::clone(&dc));
                    }
                    Self::setup_ping_handlers(&dc, s_weak);
                });
            } else if label == "runa-gs-tcp" {
                let tcp_slot = Arc::clone(&gs_tcp_slot);
                let ev_dc = ev_dc.clone();
                let pkey_dc = pkey_dc.clone();

                handle_dc.spawn(async move {
                    *tcp_slot.lock().await = Some(Arc::clone(&dc));
                    Self::setup_gs_dc_handlers(&dc, &pkey_dc, "runa-gs-tcp", ev_dc).await;
                });
            } else if label == "runa-gs-udp" {
                let udp_slot = Arc::clone(&gs_udp_slot);
                let ev_dc = ev_dc.clone();
                let pkey_dc = pkey_dc.clone();

                handle_dc.spawn(async move {
                    *udp_slot.lock().await = Some(Arc::clone(&dc));
                    Self::setup_gs_dc_handlers(&dc, &pkey_dc, "runa-gs-udp", ev_dc).await;
                });
            }
            Box::pin(async {})
        }));

        // Incoming Tracks
        let pkey_track = peer_key.clone();
        let mixer_track = Arc::clone(&mixer);
        let dec_track = Arc::clone(&decoder);
        let handle_track = handle.clone();
        let s_weak_track = session_weak.clone();
        pc.on_track(Box::new(move |track: Arc<TrackRemote>, _receiver, _transceiver| {
            let kind = track.kind();
            if kind == RTPCodecType::Audio {
                info!("[Peer {}] Audio track received", pkey_track);
                let pkey = pkey_track.clone();
                let mixer = Arc::clone(&mixer_track);
                let dec = Arc::clone(&dec_track);
                let s_weak = s_weak_track.clone();

                handle_track.spawn(async move {
                    let mut pcm_buf = [0.0f32; 480];
                    while let Ok((rtp_packet, _)) = track.read_rtp().await {
                        let payload = &rtp_packet.payload;
                        if payload.is_empty() {
                            continue;
                        }
                        let mut decoder_guard = dec.lock().await;
                        let t0 = std::time::Instant::now();
                        let res = decoder_guard.decode(Some(payload), &mut pcm_buf, false);
                        let decode_us = t0.elapsed().as_secs_f64() * 1_000_000.0;
                        drop(decoder_guard);
                        let session = s_weak.upgrade();
                        if let Some(ref s) = session {
                            s.audio_stats.lock().on_rtp(rtp_packet.header.sequence_number, rtp_packet.header.timestamp, decode_us);
                        }
                        if let Ok(samples) = res {
                            if samples > 0 {
                                let pname = session.as_ref().map(|s| s.peer_name.read().clone()).unwrap_or_else(|| pkey.clone());
                                mixer.push_peer_samples(&pkey, &pname, &pcm_buf[..samples]);
                            }
                        }
                    }
                    info!("[Peer {}] Audio track reader stopped", pkey);
                });
            } else if kind == RTPCodecType::Video {
                info!("[Peer {}] Video track received", pkey_track);
                let pkey = pkey_track.clone();
                let ev_video = event_tx.clone();
                handle_track.spawn(async move {
                    while let Ok((rtp_packet, _)) = track.read_rtp().await {
                        let payload = rtp_packet.payload.to_vec();
                        if !payload.is_empty() {
                            let _ = ev_video.send(PeerEvent::RemoteVideoPacket {
                                peer_key: pkey.clone(),
                                payload,
                            });
                        }
                    }
                    info!("[Peer {}] Video track reader stopped", pkey);
                });
            }
            Box::pin(async {})
        }));
    }

    pub async fn create(
        peer_key: String,
        peer_name: String,
        is_polite: bool,
        event_tx: mpsc::UnboundedSender<PeerEvent>,
        mixer: Arc<AudioMasterMixer>,
        handle: tokio::runtime::Handle,
    ) -> Result<Arc<Self>, Box<dyn std::error::Error + Send + Sync>> {
        let audio_track = create_audio_track(&peer_key);
        let video_track = create_video_track(&peer_key);
        let pc = Self::create_raw_pc(&audio_track, &video_track).await?;

        let decoder = Arc::new(tokio::sync::Mutex::new(
            OpusDecoderSession::new(48000, 1, 10.0).map_err(|e| format!("Decoder err: {}", e))?
        ));

        let control_channel = Arc::new(tokio::sync::Mutex::new(None));
        let gs_tcp_channel = Arc::new(tokio::sync::Mutex::new(None));
        let gs_udp_channel = Arc::new(tokio::sync::Mutex::new(None));

        let session = Arc::new(Self {
            peer_key: peer_key.clone(),
            peer_name: RwLock::new(peer_name.clone()),
            is_polite,
            pc_slot: Arc::new(RwLock::new(pc.clone())),
            audio_track,
            video_track,
            control_channel: Arc::clone(&control_channel),
            gs_tcp_channel: Arc::clone(&gs_tcp_channel),
            gs_udp_channel: Arc::clone(&gs_udp_channel),
            decoder: Arc::clone(&decoder),
            making_offer: AtomicBool::new(false),
            ignore_offer: AtomicBool::new(false),
            has_remote_description: AtomicBool::new(false),
            is_connected: AtomicBool::new(false),
            is_closed: AtomicBool::new(false),
            early_ice_candidates: Mutex::new(Vec::new()),
            local_offer_sdp: Mutex::new(None),
            local_answer_sdp: Mutex::new(None),
            last_applied_remote_offer_sdp: Mutex::new(None),
            last_applied_remote_answer_sdp: Mutex::new(None),
            remote_ice_ufrag: Mutex::new(None),
            local_ice_candidates: Mutex::new(Vec::new()),
            offer_retry_cancel: Arc::new(tokio::sync::Notify::new()),
            last_rtt_ms: Mutex::new(0.0),
            last_jitter_ms: Mutex::new(0.0),
            last_loss_pct: Mutex::new(0.0),
            audio_stats: Arc::new(Mutex::new(PeerAudioStats::new())),
            ping_channel: Arc::new(tokio::sync::Mutex::new(None)),
            ping_samples: Mutex::new(std::collections::VecDeque::with_capacity(8)),
            epoch: std::time::Instant::now(),
            mixer: Arc::clone(&mixer),
            handle: handle.clone(),
            weak_self: RwLock::new(Weak::new()),
            event_tx: event_tx.clone(),
        });

        *session.weak_self.write() = Arc::downgrade(&session);
        info!("[Peer {}] Session initialized (role: {})", peer_key, if is_polite { "polite" } else { "impolite" });

        Self::attach_pc_callbacks(
            &pc,
            peer_key,
            Arc::downgrade(&session),
            control_channel,
            gs_tcp_channel,
            gs_udp_channel,
            mixer,
            decoder,
            event_tx,
            handle,
        );

        Ok(session)
    }

    pub async fn reset_peer_connection(&self) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let old_pc = self.pc();
        let _ = old_pc.close().await;

        let new_pc = Self::create_raw_pc(&self.audio_track, &self.video_track).await?;
        Self::attach_pc_callbacks(
            &new_pc,
            self.peer_key.clone(),
            self.weak_self.read().clone(),
            Arc::clone(&self.control_channel),
            Arc::clone(&self.gs_tcp_channel),
            Arc::clone(&self.gs_udp_channel),
            Arc::clone(&self.mixer),
            Arc::clone(&self.decoder),
            self.event_tx.clone(),
            self.handle.clone(),
        );

        *self.pc_slot.write() = new_pc;
        *self.local_offer_sdp.lock() = None;
        self.local_ice_candidates.lock().clear();
        self.early_ice_candidates.lock().clear();
        self.making_offer.store(false, Ordering::SeqCst);
        self.ignore_offer.store(false, Ordering::SeqCst);
        self.has_remote_description.store(false, Ordering::SeqCst);
        self.is_connected.store(false, Ordering::SeqCst);

        info!("[Peer {}] PeerConnection cleanly reset for rollback/reconnection", self.peer_key);
        Ok(())
    }

    async fn setup_dc_handlers(
        dc: &Arc<RTCDataChannel>,
        peer_key: &str,
        event_tx: mpsc::UnboundedSender<PeerEvent>,
        session_weak: Weak<PeerSession>,
        handle: tokio::runtime::Handle,
    ) {
        let pkey_open = peer_key.to_string();
        let ev_open = event_tx.clone();
        dc.on_open(Box::new(move || {
            info!("[Peer {}] runa-control channel opened", pkey_open);
            let _ = ev_open.send(PeerEvent::ControlChannelOpened { peer_key: pkey_open.clone() });
            Box::pin(async {})
        }));

        let pkey_msg = peer_key.to_string();
        let ev_msg = event_tx.clone();
        let dc_clone = Arc::clone(dc);
        dc.on_message(Box::new(move |msg: DataChannelMessage| {
            if msg.data.is_empty() {
                return Box::pin(async {});
            }
            let text = String::from_utf8_lossy(&msg.data).to_string();

            // Check for internal latency ping/pong
            if text.starts_with("{\"type\":\"ping\"") {
                if let Ok(val) = serde_json::from_str::<serde_json::Value>(&text) {
                    if let Some(t) = val.get("t").and_then(|v| v.as_i64()) {
                        let pong = format!("{{\"type\":\"pong\",\"t\":{}}}", t);
                        let dc = Arc::clone(&dc_clone);
                        handle.spawn(async move {
                            let _ = dc.send_text(pong).await;
                        });
                        return Box::pin(async {});
                    }
                }
            } else if text.starts_with("{\"type\":\"pong\"") {
                // Eski veri kanalı ping'i artık ölçüm kaynağı değil (SCTP kendi gecikmesini ekliyor, ms çözünürlüklü);
                // gerçek ağ gecikmesi STUN aday çifti RTT'sinden gelir. Eski sürümler için yanıt yok sayılır.
                return Box::pin(async {});
            } else if text.starts_with("{\"type\":\"lat_info\"") {
                // Karşı tarafın ses kodlama bilgileri (1 Hz): mikrofon parçası, çerçeve, lookahead, kodlama süresi
                if let Ok(val) = serde_json::from_str::<serde_json::Value>(&text) {
                    if let Some(s) = session_weak.upgrade() {
                        let f = |k: &str| -> f64 {
                            val.get(k).and_then(|v| v.as_f64()).filter(|x| x.is_finite() && *x >= 0.0 && *x < 10_000_000.0)
                                .map(|us| us / 1000.0).unwrap_or(-1.0)
                        };
                        let mut st = s.audio_stats.lock();
                        st.peer_capture_ms = f("cap_us");
                        st.peer_frame_ms = f("frame_us");
                        st.peer_lookahead_ms = f("la_us");
                        st.peer_encode_ms = f("enc_us");
                    }
                }
                return Box::pin(async {});
            }

            // Normal application message (P2P_CHAT, ANN_*, STREAM_*, WATCH_*, GS_*)
            let _ = ev_msg.send(PeerEvent::DataChannelMessage {
                peer_key: pkey_msg.clone(),
                channel: "runa-control".to_string(),
                data: text,
            });

            Box::pin(async {})
        }));
    }

    async fn setup_gs_dc_handlers(
        dc: &Arc<RTCDataChannel>,
        peer_key: &str,
        channel_name: &str,
        event_tx: mpsc::UnboundedSender<PeerEvent>,
    ) {
        let pkey_open = peer_key.to_string();
        let ch_open = channel_name.to_string();
        dc.on_open(Box::new(move || {
            info!("[Peer {}] {} channel opened", pkey_open, ch_open);
            Box::pin(async {})
        }));

        let pkey_msg = peer_key.to_string();
        let ev_msg = event_tx.clone();
        let ch_msg = channel_name.to_string();
        dc.on_message(Box::new(move |msg: DataChannelMessage| {
            if msg.data.is_empty() {
                return Box::pin(async {});
            }
            if ch_msg == "runa-gs-tcp" {
                let _ = ev_msg.send(PeerEvent::GameStreamTcpPacket {
                    peer_key: pkey_msg.clone(),
                    payload: msg.data.to_vec(),
                });
            } else if ch_msg == "runa-gs-udp" {
                let _ = ev_msg.send(PeerEvent::GameStreamUdpPacket {
                    peer_key: pkey_msg.clone(),
                    payload: msg.data.to_vec(),
                });
            }
            Box::pin(async {})
        }));
    }

    /// "runa-ping" kanalı: "p:<µs>" gelirse aynen "q:<µs>" döner; "q:<µs>" gelirse gidiş-dönüş, gönderenin
    /// kendi monoton saatiyle hesaplanır (saat eşitlemesi gerekmez). Son 5 örneğin medyanı kullanılır.
    fn setup_ping_handlers(dc: &Arc<RTCDataChannel>, session_weak: Weak<PeerSession>) {
        let dc_reply = Arc::clone(dc);
        dc.on_message(Box::new(move |msg: DataChannelMessage| {
            let text = String::from_utf8_lossy(&msg.data).to_string();
            if text.len() > 32 {
                return Box::pin(async {});
            }
            if let Some(rest) = text.strip_prefix("p:") {
                let reply = format!("q:{}", rest);
                let dc = Arc::clone(&dc_reply);
                return Box::pin(async move {
                    let _ = dc.send_text(reply).await;
                });
            }
            if let Some(rest) = text.strip_prefix("q:") {
                if let (Ok(sent_us), Some(s)) = (rest.parse::<u64>(), session_weak.upgrade()) {
                    let now_us = s.epoch.elapsed().as_micros() as u64;
                    if now_us >= sent_us {
                        let rtt_ms = (now_us - sent_us) as f64 / 1000.0;
                        if rtt_ms < 10_000.0 {
                            let mut q = s.ping_samples.lock();
                            q.push_back(rtt_ms);
                            while q.len() > 5 { q.pop_front(); }
                        }
                    }
                }
            }
            Box::pin(async {})
        }));
    }

    /// Ağ gecikmesi ölçüm paketi gönderir (telemetri döngüsü, 1 Hz).
    pub async fn send_ping_probe(&self) {
        let dc = self.ping_channel.lock().await.clone();
        if let Some(dc) = dc {
            if dc.ready_state() == RTCDataChannelState::Open {
                let _ = dc.send_text(format!("p:{}", self.epoch.elapsed().as_micros() as u64)).await;
            }
        }
    }

    /// Son ölçümlerin medyanı (ms); ölçüm yoksa -1.
    pub fn network_rtt_ms(&self) -> f64 {
        let q = self.ping_samples.lock();
        if q.is_empty() { return -1.0; }
        let mut v: Vec<f64> = q.iter().copied().collect();
        v.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
        v[v.len() / 2]
    }

    pub async fn start_initiator_negotiation(self: &Arc<Self>) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        // Create runa-control channel
        let dc = self.pc().create_data_channel(
            "runa-control",
            Some(RTCDataChannelInit {
                ordered: Some(true),
                ..Default::default()
            }),
        ).await?;

        *self.control_channel.lock().await = Some(Arc::clone(&dc));
        Self::setup_dc_handlers(&dc, &self.peer_key, self.event_tx.clone(), Arc::downgrade(self), self.handle.clone()).await;

        // Ağ gecikmesi ölçüm kanalı: sırasız, yeniden gönderim yok (kayıp ping bir sonrakiyle telafi edilir;
        // büyük mesajların arkasında bekleme / yeniden gönderim gecikmesi ölçüme girmez)
        if let Ok(ping_dc) = self.pc().create_data_channel(
            "runa-ping",
            Some(RTCDataChannelInit {
                ordered: Some(false),
                max_retransmits: Some(0),
                ..Default::default()
            }),
        ).await {
            *self.ping_channel.lock().await = Some(Arc::clone(&ping_dc));
            Self::setup_ping_handlers(&ping_dc, Arc::downgrade(self));
        }

        self.start_negotiation().await?;

        // Start offer resend timer (MQTT resilience: Section 7 - up to 4 attempts with 4s intervals)
        // Cancelled immediately when remote answer is received and applied.
        let session_weak = Arc::downgrade(self);
        let cancel_notify = Arc::clone(&self.offer_retry_cancel);
        self.handle.spawn(async move {
            for _attempt in 0..4 {
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_secs(4)) => {
                        if let Some(s) = session_weak.upgrade() {
                            if s.has_remote_description.load(Ordering::SeqCst) || s.is_closed.load(Ordering::SeqCst) {
                                return;
                            }
                            let sdp = s.local_offer_sdp.lock().clone();
                            if let Some(offer_sdp) = sdp {
                                info!("[Peer {}] Retrying offer due to MQTT silence...", s.peer_key);
                                let _ = s.event_tx.send(PeerEvent::LocalSignal(SignalMessagePayload {
                                    peer_key: s.peer_key.clone(),
                                    sig_type: "sdp-offer".to_string(),
                                    data: offer_sdp,
                                }));
                                let candidates = s.local_ice_candidates.lock().clone();
                                for cand in candidates {
                                    if let Ok(cand_str) = serde_json::to_string(&cand) {
                                        let _ = s.event_tx.send(PeerEvent::LocalSignal(SignalMessagePayload {
                                            peer_key: s.peer_key.clone(),
                                            sig_type: "ice-candidate".to_string(),
                                            data: cand_str,
                                        }));
                                    }
                                }
                            }
                        } else {
                            return;
                        }
                    }
                    _ = cancel_notify.notified() => {
                        // Successfully received and applied answer, stop offer retry!
                        return;
                    }
                }
            }
        });

        Ok(())
    }

    pub async fn start_negotiation(&self) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        self.making_offer.store(true, Ordering::SeqCst);
        let pc = self.pc();
        let offer = match pc.create_offer(None).await {
            Ok(o) => o,
            Err(e) => {
                self.making_offer.store(false, Ordering::SeqCst);
                return Err(Box::new(e));
            }
        };

        if let Err(e) = pc.set_local_description(offer.clone()).await {
            self.making_offer.store(false, Ordering::SeqCst);
            return Err(Box::new(e));
        }

        *self.local_offer_sdp.lock() = Some(offer.sdp.clone());
        self.making_offer.store(false, Ordering::SeqCst);

        let ufrag = extract_ice_ufrag(&offer.sdp).unwrap_or_else(|| "unknown".into());
        info!("[Peer {}] Offer generated and sent (ufrag: {})", self.peer_key, ufrag);
        info!("[Peer {}] Local Offer SDP:\n{}", self.peer_key, offer.sdp);

        let _ = self.event_tx.send(PeerEvent::LocalSignal(SignalMessagePayload {
            peer_key: self.peer_key.clone(),
            sig_type: "sdp-offer".to_string(),
            data: offer.sdp,
        }));

        Ok(())
    }

    pub async fn handle_offer(&self, sdp: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        // Step 1: Idempotency check for duplicate offers (Bölüm 4.2.2)
        // Son uygulanan uzak teklif SDP'si saklanır; aynı SDP tekrar gelirse yeniden uygulanmaz,
        // kayıtlı yanıt ve yerel ICE adayları yeniden gönderilir.
        {
            let last_offer = self.last_applied_remote_offer_sdp.lock();
            if let Some(ref last) = *last_offer {
                if last == sdp {
                    info!("[Peer {}] Duplicate offer received; resending cached local answer and ICE candidates", self.peer_key);
                    if let Some(ref ans_sdp) = *self.local_answer_sdp.lock() {
                        let _ = self.event_tx.send(PeerEvent::LocalSignal(SignalMessagePayload {
                            peer_key: self.peer_key.clone(),
                            sig_type: "sdp-answer".to_string(),
                            data: ans_sdp.clone(),
                        }));
                        let candidates = self.local_ice_candidates.lock().clone();
                        for cand in candidates {
                            if let Ok(cand_str) = serde_json::to_string(&cand) {
                                let _ = self.event_tx.send(PeerEvent::LocalSignal(SignalMessagePayload {
                                    peer_key: self.peer_key.clone(),
                                    sig_type: "ice-candidate".to_string(),
                                    data: cand_str,
                                }));
                            }
                        }
                    }
                    return Ok(());
                }
            }
        }

        // Step 2: Glare & Perfect Negotiation collision handling (Bölüm 4.2.1)
        let offer_collision = self.making_offer.load(Ordering::SeqCst)
            || self.pc().signaling_state() != RTCSignalingState::Stable;
        let ignore = !self.is_polite && offer_collision;
        self.ignore_offer.store(ignore, Ordering::SeqCst);

        if ignore {
            info!("[Peer {}] Glare detected: impolite peer ignoring offer collision", self.peer_key);
            return Ok(());
        }

        if offer_collision {
            info!("[Peer {}] Glare detected: polite peer rolling back offer to accept incoming offer cleanly", self.peer_key);
            self.reset_peer_connection().await?;
        }

        // Step 3: Apply remote offer
        let pc = self.pc();
        let desc = RTCSessionDescription::offer(sdp.to_string())?;
        pc.set_remote_description(desc).await?;
        self.has_remote_description.store(true, Ordering::SeqCst);
        *self.last_applied_remote_offer_sdp.lock() = Some(sdp.to_string());

        let remote_ufrag = extract_ice_ufrag(sdp);
        if let Some(ref ufrag) = remote_ufrag {
            *self.remote_ice_ufrag.lock() = Some(ufrag.clone());
        }

        info!(
            "[Peer {}] Remote offer applied successfully (ufrag: {})",
            self.peer_key,
            remote_ufrag.as_deref().unwrap_or("unknown")
        );

        // Step 4: Drain early ICE candidates matching remote ufrag (Bölüm 4.3)
        let candidates: Vec<RTCIceCandidateInit> = self.early_ice_candidates.lock().drain(..).collect();
        for cand in candidates {
            if let Some(ref expected_ufrag) = remote_ufrag {
                if let Some(ref cand_ufrag) = cand.username_fragment {
                    if cand_ufrag != expected_ufrag {
                        continue;
                    }
                }
            }
            let _ = pc.add_ice_candidate(cand).await;
        }

        // Step 5: Create and set local answer
        let answer = pc.create_answer(None).await?;
        pc.set_local_description(answer.clone()).await?;
        *self.local_answer_sdp.lock() = Some(answer.sdp.clone());

        let local_ufrag = extract_ice_ufrag(&answer.sdp).unwrap_or_else(|| "unknown".into());
        info!("[Peer {}] Answer generated and sent (ufrag: {})", self.peer_key, local_ufrag);
        info!("[Peer {}] Local Answer SDP:\n{}", self.peer_key, answer.sdp);

        let _ = self.event_tx.send(PeerEvent::LocalSignal(SignalMessagePayload {
            peer_key: self.peer_key.clone(),
            sig_type: "sdp-answer".to_string(),
            data: answer.sdp,
        }));

        Ok(())
    }

    pub async fn handle_answer(&self, sdp: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let pc = self.pc();
        let current_state = pc.signaling_state();
        let last_ans = self.last_applied_remote_answer_sdp.lock().clone();

        // Step 1: Idempotency check for duplicate answers (Bölüm 4.2.3)
        // Bağlantı "stable" durumdayken gelen ve son uygulanan yanıtla aynı olan yanıt sessizce yok sayılır.
        if current_state == RTCSignalingState::Stable {
            if let Some(ref last) = last_ans {
                if last == sdp {
                    tracing::debug!("[Peer {}] Duplicate answer received in Stable state; ignoring safely", self.peer_key);
                    return Ok(());
                }
            }
            tracing::debug!("[Peer {}] Late answer received in Stable state; ignoring safely", self.peer_key);
            return Ok(());
        }

        // Step 2: Apply remote answer
        info!("[Peer {}] Applying Remote Answer SDP:\n{}", self.peer_key, sdp);
        let desc = RTCSessionDescription::answer(sdp.to_string())?;
        if let Err(e) = pc.set_remote_description(desc).await {
            warn!("[Peer {}] Failed to set_remote_description(answer): {}", self.peer_key, e);
            return Err(Box::new(e));
        }

        self.has_remote_description.store(true, Ordering::SeqCst);
        *self.last_applied_remote_answer_sdp.lock() = Some(sdp.to_string());

        let remote_ufrag = extract_ice_ufrag(sdp);
        if let Some(ref ufrag) = remote_ufrag {
            *self.remote_ice_ufrag.lock() = Some(ufrag.clone());
        }

        // Stop offer retry immediately (Bölüm 4.2.5)
        self.offer_retry_cancel.notify_waiters();

        info!(
            "[Peer {}] Remote answer applied successfully (ufrag: {})",
            self.peer_key,
            remote_ufrag.as_deref().unwrap_or("unknown")
        );

        // Step 3: Drain early ICE candidates matching remote ufrag (Bölüm 4.3)
        let candidates: Vec<RTCIceCandidateInit> = self.early_ice_candidates.lock().drain(..).collect();
        for cand in candidates {
            if let Some(ref expected_ufrag) = remote_ufrag {
                if let Some(ref cand_ufrag) = cand.username_fragment {
                    if cand_ufrag != expected_ufrag {
                        continue;
                    }
                }
            }
            let _ = pc.add_ice_candidate(cand).await;
        }

        Ok(())
    }

    pub async fn handle_ice_candidate(&self, cand_json: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let cand_init: IceCandidateJson = serde_json::from_str(cand_json)?;
        let rtc_cand = RTCIceCandidateInit {
            candidate: cand_init.candidate,
            sdp_mid: cand_init.sdp_mid,
            sdp_mline_index: cand_init.sdp_mline_index,
            username_fragment: cand_init.username_fragment,
        };

        if self.has_remote_description.load(Ordering::SeqCst) {
            let remote_ufrag = self.remote_ice_ufrag.lock().clone();
            if let Some(ref expected_ufrag) = remote_ufrag {
                if let Some(ref cand_ufrag) = rtc_cand.username_fragment {
                    if cand_ufrag != expected_ufrag {
                        // Candidate from mismatched/obsolete generation, safely discard
                        return Ok(());
                    }
                }
            }
            let _ = self.pc().add_ice_candidate(rtc_cand).await;
        } else {
            self.early_ice_candidates.lock().push(rtc_cand);
        }

        Ok(())
    }

    pub async fn send_audio_sample(&self, sample: &webrtc::media::Sample) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        if self.is_connected.load(Ordering::Relaxed) {
            self.audio_track.write_sample(sample).await?;
        }
        Ok(())
    }

    pub async fn send_video_sample(&self, sample: &webrtc::media::Sample) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        if self.is_connected.load(Ordering::Relaxed) {
            self.video_track.write_sample(sample).await?;
        }
        Ok(())
    }

    pub async fn send_control_message(&self, msg: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let lock = self.control_channel.lock().await;
        if let Some(dc) = &*lock {
            if dc.ready_state() == RTCDataChannelState::Open {
                dc.send_text(msg).await?;
                return Ok(());
            }
        }
        Err("DataChannel is not open".into())
    }

    pub fn is_control_open(&self) -> bool {
        // Synchronous check if data channel is present and ready
        if let Ok(lock) = self.control_channel.try_lock() {
            if let Some(dc) = &*lock {
                return dc.ready_state() == RTCDataChannelState::Open;
            }
        }
        false
    }

    pub async fn ensure_gamestream_channels(self: &Arc<Self>) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let pc = self.pc();
        let tcp_dc = pc.create_data_channel(
            "runa-gs-tcp",
            Some(RTCDataChannelInit {
                ordered: Some(true),
                ..Default::default()
            }),
        ).await?;
        *self.gs_tcp_channel.lock().await = Some(Arc::clone(&tcp_dc));
        Self::setup_gs_dc_handlers(&tcp_dc, &self.peer_key, "runa-gs-tcp", self.event_tx.clone()).await;

        let udp_dc = pc.create_data_channel(
            "runa-gs-udp",
            Some(RTCDataChannelInit {
                ordered: Some(false),
                max_retransmits: Some(0),
                ..Default::default()
            }),
        ).await?;
        *self.gs_udp_channel.lock().await = Some(Arc::clone(&udp_dc));
        Self::setup_gs_dc_handlers(&udp_dc, &self.peer_key, "runa-gs-udp", self.event_tx.clone()).await;

        self.start_negotiation().await?;
        Ok(())
    }

    pub async fn send_gamestream_tcp(&self, payload: &[u8]) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let lock = self.gs_tcp_channel.lock().await;
        if let Some(dc) = &*lock {
            if dc.ready_state() == RTCDataChannelState::Open {
                let bytes = Bytes::copy_from_slice(payload);
                dc.send(&bytes).await?;
                return Ok(());
            }
        }
        Err("runa-gs-tcp channel is not open".into())
    }

    pub async fn send_gamestream_udp(&self, payload: &[u8]) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let lock = self.gs_udp_channel.lock().await;
        if let Some(dc) = &*lock {
            if dc.ready_state() == RTCDataChannelState::Open {
                let bytes = Bytes::copy_from_slice(payload);
                dc.send(&bytes).await?;
                return Ok(());
            }
        }
        Err("runa-gs-udp channel is not open".into())
    }

    pub async fn send_ping(&self) {
        let now_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis() as i64;
        let ping_msg = format!("{{\"type\":\"ping\",\"t\":{}}}", now_ms);
        let _ = self.send_control_message(&ping_msg).await;
    }

    pub async fn close(&self) {
        self.is_closed.store(true, Ordering::SeqCst);
        self.is_connected.store(false, Ordering::SeqCst);
        let _ = self.pc().close().await;
    }
}
