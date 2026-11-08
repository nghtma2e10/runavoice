use std::sync::Arc;
use tokio::sync::{mpsc, Mutex};
use tracing::info;
use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::MediaEngine;
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_init::RTCDataChannelInit;
use webrtc::data_channel::RTCDataChannel;
use webrtc::ice_transport::ice_candidate::RTCIceCandidateInit;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::interceptor::registry::Registry;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::media_config::{configure_media_engine, create_audio_track, create_video_track};

pub struct WebRtcEndpoint {
    pub name: String,
    pub is_polite: bool,
    pub pc: Arc<RTCPeerConnection>,
    pub audio_track: Arc<TrackLocalStaticSample>,
    pub video_track: Arc<TrackLocalStaticSample>,
    pub control_channel: Arc<Mutex<Option<Arc<RTCDataChannel>>>>,
    pub ice_candidates_tx: mpsc::UnboundedSender<RTCIceCandidateInit>,
    pub ice_candidates_rx: Arc<Mutex<mpsc::UnboundedReceiver<RTCIceCandidateInit>>>,
}

impl WebRtcEndpoint {
    pub async fn create(name: &str, is_polite: bool) -> Result<Self, Box<dyn std::error::Error + Send + Sync>> {
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
                    urls: vec!["stun:stun.l.google.com:19302".to_string()],
                    ..Default::default()
                },
            ],
            ..Default::default()
        };

        let pc = Arc::new(api.new_peer_connection(config).await?);

        // Standardized audio & video tracks via media_config
        let audio_track = create_audio_track(name);
        let video_track = create_video_track(name);

        pc.add_track(Arc::clone(&audio_track) as Arc<dyn TrackLocal + Send + Sync>).await?;
        pc.add_track(Arc::clone(&video_track) as Arc<dyn TrackLocal + Send + Sync>).await?;

        let control_channel = Arc::new(Mutex::new(None));
        let (cand_tx, cand_rx) = mpsc::unbounded_channel();
        let cand_tx_clone = cand_tx.clone();

        pc.on_ice_candidate(Box::new(move |candidate| {
            if let Some(cand) = candidate {
                if let Ok(json_cand) = cand.to_json() {
                    let _ = cand_tx_clone.send(json_cand);
                }
            }
            Box::pin(async {})
        }));

        let pc_name = name.to_string();
        pc.on_peer_connection_state_change(Box::new(move |state: RTCPeerConnectionState| {
            info!("[{}] PeerConnection state changed: {:?}", pc_name, state);
            Box::pin(async {})
        }));

        // Handle incoming data channel on the answering side
        let cc_slot = Arc::clone(&control_channel);
        pc.on_data_channel(Box::new(move |dc: Arc<RTCDataChannel>| {
            let label = dc.label().to_string();
            info!("Incoming DataChannel: {}", label);
            if label == "runa-control" {
                let cc_slot2 = Arc::clone(&cc_slot);
                if let Ok(h) = tokio::runtime::Handle::try_current() {
                    h.spawn(async move {
                        *cc_slot2.lock().await = Some(dc);
                    });
                }
            }
            Box::pin(async {})
        }));

        Ok(Self {
            name: name.to_string(),
            is_polite,
            pc,
            audio_track,
            video_track,
            control_channel,
            ice_candidates_tx: cand_tx,
            ice_candidates_rx: Arc::new(Mutex::new(cand_rx)),
        })
    }

    pub async fn create_data_channels(&self) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        // Create runa-control channel (ordered, reliable)
        let dc = self.pc.create_data_channel(
            "runa-control",
            Some(RTCDataChannelInit {
                ordered: Some(true),
                ..Default::default()
            }),
        ).await?;

        *self.control_channel.lock().await = Some(dc);
        Ok(())
    }
}
