use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use serde_json::Value;
use tokio::runtime::Runtime;
use runa_audio::{
    AudioSettings, AudioSubsystem, PeerAudioConfig, WasapiDeviceManager,
};
use runa_net::{NetSubsystem, PeerEvent};
use runa_video::VideoSubsystem;
use runa_gamestream::{BandwidthProbe, GameStreamSubsystem, TunnelRole};
use runa_telemetry::{init_telemetry, TelemetryHandle};

pub type EventCallbackFn = unsafe extern "C" fn(user: *mut c_void, kind: u32, data: *const u8, len: usize);

struct CallbackHolder {
    cb: EventCallbackFn,
    user: *mut c_void,
}

// SAFETY: The callback function and user pointer are provided by C# and guaranteed valid by C# GCHandle
unsafe impl Send for CallbackHolder {}
unsafe impl Sync for CallbackHolder {}

pub struct Engine {
    _telemetry: TelemetryHandle,
    runtime: Option<Arc<Runtime>>,
    callback: Option<Arc<CallbackHolder>>,
    last_error: Arc<Mutex<Option<String>>>,
    is_running: Arc<AtomicBool>,
    audio: Arc<AudioSubsystem>,
    net: Arc<NetSubsystem>,
    video: Arc<VideoSubsystem>,
    gamestream: Arc<GameStreamSubsystem>,
}

impl Engine {
    pub fn new(
        config_json_str: &str,
        cb: Option<EventCallbackFn>,
        user: *mut c_void,
    ) -> Result<Self, String> {
        let config: Value = if config_json_str.is_empty() {
            Value::Object(Default::default())
        } else {
            serde_json::from_str(config_json_str)
                .map_err(|e| format!("Invalid JSON config: {}", e))?
        };

        let log_dir = config.get("log_dir").and_then(|v| v.as_str());
        let log_level = config
            .get("log_level")
            .and_then(|v| v.as_str())
            .unwrap_or("info");

        let telemetry = init_telemetry(log_dir, log_level);

        tracing::info!("Initializing RunaEngine with config: {}", config_json_str);

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .thread_name("runa-net-worker")
            .enable_all()
            .build()
            .map_err(|e| format!("Failed to create tokio runtime: {}", e))?;

        let _guard = runtime.enter();
        let handle = runtime.handle().clone();

        let callback = cb.map(|func| {
            Arc::new(CallbackHolder {
                cb: func,
                user,
            })
        });

        let is_running = Arc::new(AtomicBool::new(true));
        let audio = Arc::new(AudioSubsystem::new());
        let net = Arc::new(NetSubsystem::new(audio.mixer(), handle.clone())?);
        let video = Arc::new(VideoSubsystem::new());
        let gamestream = Arc::new(GameStreamSubsystem::new(handle.clone()));

        // Wire outgoing audio capture into WebRTC broadcast
        if let Some(rx) = audio.take_outgoing_frame_receiver() {
            net.start_audio_broadcast_worker(rx);
        }

        // Start 1 Hz telemetry loop
        net.start_telemetry_worker();

        // Spawn periodic 20 Hz (50ms) level & telemetry dispatcher task
        let is_running_clone = Arc::clone(&is_running);
        let audio_clone = Arc::clone(&audio);
        let cb_clone = callback.clone();
        runtime.spawn(async move {
            let mut interval = tokio::time::interval(std::time::Duration::from_millis(50));
            while is_running_clone.load(Ordering::Relaxed) {
                interval.tick().await;
                if let Some(ref cb_holder) = cb_clone {
                    let levels = audio_clone.get_audio_levels();
                    let payload = serde_json::json!({
                        "type": "audio_levels",
                        "data": levels,
                    });
                    if let Ok(bytes) = serde_json::to_vec(&payload) {
                        // SAFETY: Invoking C# callback with pointer
                        unsafe {
                            (cb_holder.cb)(cb_holder.user, 0, bytes.as_ptr(), bytes.len());
                        }
                    }
                }
            }
        });

        // 1 Hz GameStream Stats Forwarder
        let is_running_gs = Arc::clone(&is_running);
        let gamestream_tick = Arc::clone(&gamestream);
        let cb_gs = callback.clone();
        runtime.spawn(async move {
            let mut interval = tokio::time::interval(std::time::Duration::from_secs(1));
            while is_running_gs.load(Ordering::Relaxed) {
                interval.tick().await;
                if gamestream_tick.is_active() {
                    if let Some(stats) = gamestream_tick.get_stats() {
                        if let Some(ref cb_holder) = cb_gs {
                            let payload = serde_json::json!({
                                "type": "gs.stats",
                                "data": stats,
                            });
                            if let Ok(bytes) = serde_json::to_vec(&payload) {
                                unsafe {
                                    (cb_holder.cb)(cb_holder.user, 0, bytes.as_ptr(), bytes.len());
                                }
                            }
                            if stats.is_high_loss {
                                let loss_payload = serde_json::json!({
                                    "type": "gs.high_loss",
                                    "loss_rate": stats.loss_rate_percent,
                                });
                                if let Ok(bytes) = serde_json::to_vec(&loss_payload) {
                                    unsafe {
                                        (cb_holder.cb)(cb_holder.user, 0, bytes.as_ptr(), bytes.len());
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        // Spawn NetSubsystem event forwarding to C#
        let net_rx_arc = net.event_receiver();
        let cb_net = callback.clone();
        let is_running_net = Arc::clone(&is_running);
        let video_clone = Arc::clone(&video);
        let gamestream_rx = Arc::clone(&gamestream);
        let handle_event = handle.clone();
        runtime.spawn(async move {
            let mut rx = net_rx_arc.lock().await;
            while is_running_net.load(Ordering::Relaxed) {
                if let Some(event) = rx.recv().await {
                    if let Some(ref cb_holder) = cb_net {
                        let payload = match event {
                            PeerEvent::LocalSignal(sig) => {
                                serde_json::json!({
                                    "type": "signal.local",
                                    "peer_key": sig.peer_key,
                                    "sig_type": sig.sig_type,
                                    "data": sig.data,
                                })
                            }
                            PeerEvent::StateChanged { peer_key, state } => {
                                serde_json::json!({
                                    "type": "peer.state",
                                    "peer_key": peer_key,
                                    "state": state,
                                })
                            }
                            PeerEvent::ControlChannelOpened { peer_key } => {
                                serde_json::json!({
                                    "type": "dc.open",
                                    "peer_key": peer_key,
                                    "channel": "runa-control",
                                })
                            }
                            PeerEvent::DataChannelMessage { peer_key, channel, data } => {
                                serde_json::json!({
                                    "type": "dc.message",
                                    "peer_key": peer_key,
                                    "channel": channel,
                                    "data": data,
                                })
                            }
                            PeerEvent::LatencyUpdate(report) => {
                                // Tüm gerçek ölçümler (ms; ölçülemeyen alan -1) — C# PeerLatencyInfo'ya eşlenir
                                let mut v = serde_json::to_value(&report).unwrap_or(serde_json::Value::Null);
                                if let serde_json::Value::Object(ref mut m) = v {
                                    m.insert("type".to_string(), serde_json::Value::from("latency"));
                                }
                                v
                            }
                            PeerEvent::RemoteVideoPacket { peer_key, payload } => {
                                if let Some(frame) = video_clone.handle_remote_nalu(&payload) {
                                    serde_json::json!({
                                        "type": "video.frame",
                                        "peer_key": peer_key,
                                        "width": frame.width,
                                        "height": frame.height,
                                        "is_keyframe": frame.is_keyframe,
                                    })
                                } else {
                                    serde_json::json!({
                                        "type": "video.packet",
                                        "peer_key": peer_key,
                                        "size": payload.len(),
                                    })
                                }
                            }
                            PeerEvent::GameStreamTcpPacket { peer_key, payload } => {
                                if let Some(ref t) = *gamestream_rx.active_tunnel.lock() {
                                    let t_inner = Arc::clone(t);
                                    let payload_cl = payload.clone();
                                    handle_event.spawn(async move {
                                        t_inner.handle_incoming_tcp(&payload_cl).await;
                                    });
                                }
                                serde_json::json!({
                                    "type": "gs.tcp_packet",
                                    "peer_key": peer_key,
                                    "size": payload.len(),
                                })
                            }
                            PeerEvent::GameStreamUdpPacket { peer_key, payload } => {
                                if let Some(ref t) = *gamestream_rx.active_tunnel.lock() {
                                    let t_inner = Arc::clone(t);
                                    let payload_cl = payload.clone();
                                    handle_event.spawn(async move {
                                        t_inner.handle_incoming_udp(&payload_cl).await;
                                    });
                                }
                                serde_json::json!({
                                    "type": "gs.udp_packet",
                                    "peer_key": peer_key,
                                    "size": payload.len(),
                                })
                            }
                        };

                        if let Ok(bytes) = serde_json::to_vec(&payload) {
                            // SAFETY: Invoking C# callback with pointer
                            unsafe {
                                (cb_holder.cb)(cb_holder.user, 0, bytes.as_ptr(), bytes.len());
                            }
                        }
                    }
                }
            }
        });

        let engine = Self {
            _telemetry: telemetry,
            runtime: Some(Arc::new(runtime)),
            callback,
            last_error: Arc::new(Mutex::new(None)),
            is_running,
            audio,
            net,
            video,
            gamestream,
        };

        tracing::info!("RunaEngine initialized successfully with Audio, Net and Video subsystems");
        Ok(engine)
    }

    pub fn audio(&self) -> Arc<AudioSubsystem> {
        Arc::clone(&self.audio)
    }

    pub fn net(&self) -> Arc<NetSubsystem> {
        Arc::clone(&self.net)
    }

    pub fn video(&self) -> Arc<VideoSubsystem> {
        Arc::clone(&self.video)
    }

    pub fn gamestream(&self) -> Arc<GameStreamSubsystem> {
        Arc::clone(&self.gamestream)
    }

    pub fn set_last_error(&self, err: String) {
        let mut guard = self.last_error.lock();
        *guard = Some(err);
    }

    pub fn get_last_error(&self) -> Option<String> {
        self.last_error.lock().clone()
    }

    pub fn dispatch_event(&self, kind: u32, data: &[u8]) {
        if let Some(ref cb_holder) = self.callback {
            // SAFETY: Calling C# callback function pointer with provided user pointer
            unsafe {
                (cb_holder.cb)(cb_holder.user, kind, data.as_ptr(), data.len());
            }
        }
    }

    pub fn execute_command(&self, json_str: &str) -> Result<(), String> {
        if !self.is_running.load(Ordering::Relaxed) {
            return Err("Engine is shutting down".to_string());
        }

        let val: Value = serde_json::from_str(json_str)
            .map_err(|e| format!("Invalid command JSON: {}", e))?;

        let cmd = val.get("cmd").and_then(|v| v.as_str()).unwrap_or("");
        tracing::debug!("Engine received command: {}", cmd);

        match cmd {
            "ping" => {
                let echo_data = val.get("data").cloned().unwrap_or(Value::Null);
                let now = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_millis();

                let pong = serde_json::json!({
                    "type": "pong",
                    "time": now,
                    "echo": echo_data
                });

                if let Ok(bytes) = serde_json::to_vec(&pong) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "audio.set_devices" => {
                let in_id = val.get("input_id").and_then(|v| v.as_str()).unwrap_or("");
                let out_id = val.get("output_id").and_then(|v| v.as_str()).unwrap_or("");
                self.audio.set_devices(in_id, out_id)
            }
            "audio.apply_settings" => {
                if let Some(s_val) = val.get("settings") {
                    let s: AudioSettings = serde_json::from_value(s_val.clone())
                        .map_err(|e| format!("Invalid AudioSettings: {}", e))?;
                    self.audio.apply_settings(s);
                }
                Ok(())
            }
            "audio.set_mute" => {
                let muted = val.get("muted").and_then(|v| v.as_bool()).unwrap_or(false);
                self.audio.set_mute(muted);
                Ok(())
            }
            "audio.set_deafen" => {
                let deafened = val.get("deafened").and_then(|v| v.as_bool()).unwrap_or(false);
                self.audio.set_deafen(deafened);
                Ok(())
            }
            "audio.set_ptt" => {
                let held = val.get("held").and_then(|v| v.as_bool()).unwrap_or(false);
                self.audio.set_ptt(held);
                Ok(())
            }
            "audio.mic_monitor" => {
                let enabled = val.get("enabled").and_then(|v| v.as_bool()).unwrap_or(false);
                self.audio.set_mic_monitor(enabled);
                Ok(())
            }
            "audio.start_room" => {
                self.audio.start_room_audio()
            }
            "audio.stop_room" => {
                self.audio.stop_room_audio();
                Ok(())
            }
            "audio.restart_devices" => {
                self.audio.restart_devices()
            }
            "audio.mic_test.start" => {
                self.audio.start_mic_test();
                Ok(())
            }
            "audio.mic_test.stop_and_play" => {
                self.audio.stop_and_play_mic_test();
                Ok(())
            }
            "audio.mic_test.stop" => {
                self.audio.stop_mic_test();
                Ok(())
            }
            "audio.spatial.test_tone" => {
                let az = val.get("azimuth").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
                let dist = val.get("distance").and_then(|v| v.as_f64()).unwrap_or(0.2) as f32;
                self.audio.play_spatial_test_tone(az, dist);
                Ok(())
            }
            "audio.peer.config" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("");
                if let Some(cfg_val) = val.get("config") {
                    let cfg: PeerAudioConfig = serde_json::from_value(cfg_val.clone())
                        .map_err(|e| format!("Invalid PeerAudioConfig: {}", e))?;
                    self.audio.update_peer_config(peer_key, cfg);
                }
                Ok(())
            }
            "audio.peer.remove" | "peer.remove" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("");
                self.audio.remove_peer(peer_key);
                let net = Arc::clone(&self.net);
                let pk = peer_key.to_string();
                if let Some(ref rt) = self.runtime {
                    rt.spawn(async move {
                        net.remove_peer(&pk).await;
                    });
                }
                Ok(())
            }
            "audio.aux.play" => {
                let is_stereo = val.get("is_stereo").and_then(|v| v.as_bool()).unwrap_or(false);
                let samples: Vec<f32> = val.get("samples")
                    .and_then(|v| v.as_array())
                    .map(|arr| arr.iter().filter_map(|x| x.as_f64().map(|f| f as f32)).collect())
                    .unwrap_or_default();
                self.audio.play_local_aux(samples, is_stereo);
                Ok(())
            }
            "audio.aux.clear" => {
                self.audio.clear_aux();
                Ok(())
            }
            // Aşama 3: Sinyalleşme ve Eş Bağlantı Komutları
            "signal.remote" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let peer_name = val.get("peer_name").and_then(|v| v.as_str()).unwrap_or(&peer_key).to_string();
                let sig_type = val.get("sig_type").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let data = val.get("data").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let is_polite = val.get("is_polite").and_then(|v| v.as_bool()).unwrap_or(true);
                let net = Arc::clone(&self.net);

                if let Some(ref rt) = self.runtime {
                    rt.spawn(async move {
                        if let Err(e) = net.handle_remote_signal(&peer_key, &peer_name, &sig_type, &data, is_polite).await {
                            tracing::warn!("[signal.remote] Error handling signal from {}: {}", peer_key, e);
                        }
                    });
                }
                Ok(())
            }
            "peer.ensure" | "net.ensure_peer" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let peer_name = val.get("peer_name").and_then(|v| v.as_str()).unwrap_or(&peer_key).to_string();
                let is_polite = val.get("is_polite").and_then(|v| v.as_bool()).unwrap_or(true);
                let net = Arc::clone(&self.net);

                if let Some(ref rt) = self.runtime {
                    rt.spawn(async move {
                        if let Err(e) = net.ensure_peer(&peer_key, &peer_name, is_polite).await {
                            tracing::warn!("[peer.ensure] Error ensuring peer {}: {}", peer_key, e);
                        }
                    });
                }
                Ok(())
            }
            "room.leave" | "net.close_all" => {
                let net = Arc::clone(&self.net);
                if let Some(ref rt) = self.runtime {
                    rt.spawn(async move {
                        net.close_all().await;
                    });
                }
                Ok(())
            }
            "dc.send" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let data = val.get("data").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let net = Arc::clone(&self.net);

                if let Some(ref rt) = self.runtime {
                    rt.spawn(async move {
                        let _ = net.send_control_message(&peer_key, &data).await;
                    });
                }
                Ok(())
            }
            "dc.broadcast" => {
                let data = val.get("data").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let except_peer = val.get("except_peer").and_then(|v| v.as_str()).map(|s| s.to_string());
                let net = Arc::clone(&self.net);

                if let Some(ref rt) = self.runtime {
                    rt.spawn(async move {
                        net.broadcast_control_message(&data, except_peer.as_deref()).await;
                    });
                }
                Ok(())
            }
            "share.start" => {
                let src_type_str = val.get("source_type").and_then(|v| v.as_str()).unwrap_or("monitor");
                let source_type = if src_type_str == "window" {
                    runa_video::capture::CaptureSourceType::Window
                } else {
                    runa_video::capture::CaptureSourceType::Monitor
                };
                let handle = val.get("handle").and_then(|v| v.as_i64()).unwrap_or(0) as isize;
                let max_height = val.get("max_height").and_then(|v| v.as_u64()).unwrap_or(1080) as u32;
                let max_fps = val.get("max_fps").and_then(|v| v.as_u64()).unwrap_or(30) as u32;
                let priority_str = val.get("priority").and_then(|v| v.as_str()).unwrap_or("clarity");
                let priority = if priority_str == "fluidity" {
                    runa_video::adaptive::PriorityMode::Fluidity
                } else {
                    runa_video::adaptive::PriorityMode::Clarity
                };
                let data_saver = val.get("data_saver").and_then(|v| v.as_bool()).unwrap_or(false);

                let success = self.video.start_share(source_type, handle, max_height, max_fps, priority, data_saver);
                let ev = serde_json::json!({
                    "type": "share.state",
                    "state": if success { "started" } else { "failed" },
                    "width": 1920,
                    "height": max_height,
                    "fps": max_fps,
                });
                if let Ok(bytes) = serde_json::to_vec(&ev) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "share.update" => {
                let max_height = val.get("max_height").and_then(|v| v.as_u64()).unwrap_or(1080) as u32;
                let max_fps = val.get("max_fps").and_then(|v| v.as_u64()).unwrap_or(30) as u32;
                self.video.update_share(max_height, max_fps);
                let ev = serde_json::json!({
                    "type": "share.state",
                    "state": "updated",
                    "height": max_height,
                    "fps": max_fps,
                });
                if let Ok(bytes) = serde_json::to_vec(&ev) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "share.stop" => {
                self.video.stop_share();
                let ev = serde_json::json!({
                    "type": "share.state",
                    "state": "stopped",
                });
                if let Ok(bytes) = serde_json::to_vec(&ev) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "watch.start" => {
                if let Some(peer_key) = val.get("peer_key").and_then(|v| v.as_str()) {
                    let needs_kf = self.video.on_viewer_watch_start(peer_key);
                    if needs_kf {
                        // send request for keyframe if needed
                    }
                }
                Ok(())
            }
            "watch.stop" => {
                if let Some(peer_key) = val.get("peer_key").and_then(|v| v.as_str()) {
                    self.video.on_viewer_watch_stop(peer_key);
                }
                Ok(())
            }
            "share.tracking_frames" => {
                let enabled = val.get("enabled").and_then(|v| v.as_bool()).unwrap_or(false);
                self.video.set_tracking_frames(enabled);
                Ok(())
            }
            "share.surface_resize" => {
                let width = val.get("width").and_then(|v| v.as_u64()).unwrap_or(1920) as u32;
                let height = val.get("height").and_then(|v| v.as_u64()).unwrap_or(1080) as u32;
                self.video.surface_resize(width, height);
                Ok(())
            }
            "gs.tunnel.start" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let role_str = val.get("role").and_then(|v| v.as_str()).unwrap_or("client");
                let role = if role_str == "host" { TunnelRole::Host } else { TunnelRole::Client };
                let bind_ip = val.get("bind_ip").and_then(|v| v.as_str()).unwrap_or("127.0.0.77").to_string();

                let tunnel = self.gamestream.start_tunnel(role, &bind_ip);

                let net = Arc::clone(&self.net);
                let peer_key_tcp = peer_key.clone();
                let peer_key_udp = peer_key.clone();

                if let Some(mut tcp_rx) = tunnel.tcp_outgoing_rx.lock().take() {
                    let net_cl = Arc::clone(&net);
                    let pkey = peer_key_tcp.clone();
                    if let Some(ref rt) = self.runtime {
                        rt.spawn(async move {
                            while let Some(chunk) = tcp_rx.recv().await {
                                net_cl.send_gamestream_tcp(&pkey, &chunk).await;
                            }
                        });
                    }
                }

                if let Some(mut udp_rx) = tunnel.udp_outgoing_rx.lock().take() {
                    let net_cl = Arc::clone(&net);
                    let pkey = peer_key_udp.clone();
                    if let Some(ref rt) = self.runtime {
                        rt.spawn(async move {
                            while let Some(chunk) = udp_rx.recv().await {
                                net_cl.send_gamestream_udp(&pkey, &chunk).await;
                            }
                        });
                    }
                }

                if let Some(ref rt) = self.runtime {
                    let net_cl = Arc::clone(&net);
                    let pkey = peer_key.clone();
                    rt.spawn(async move {
                        let _ = net_cl.ensure_gamestream_channels(&pkey).await;
                    });
                }

                let ev = serde_json::json!({
                    "type": "gs.state",
                    "state": "started",
                    "role": role_str,
                    "peer_key": peer_key,
                });
                if let Ok(bytes) = serde_json::to_vec(&ev) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "gs.tunnel.stop" => {
                self.gamestream.stop_tunnel();
                let ev = serde_json::json!({
                    "type": "gs.state",
                    "state": "stopped",
                });
                if let Ok(bytes) = serde_json::to_vec(&ev) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "gs.probe.run" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let _session_id = val.get("session_id").and_then(|v| v.as_str()).unwrap_or("probe_session").to_string();
                let max_bitrate = val.get("max_bitrate_kbps").and_then(|v| v.as_u64()).unwrap_or(0) as u32;
                let codecs: Vec<String> = val.get("codecs")
                    .and_then(|v| v.as_array())
                    .map(|arr| arr.iter().filter_map(|s| s.as_str().map(|x| x.to_string())).collect())
                    .unwrap_or_else(|| vec!["H264".to_string(), "HEVC".to_string()]);

                let profile = BandwidthProbe::calculate_recommended_profile(8000, &codecs, max_bitrate);

                let ev = serde_json::json!({
                    "type": "gs.probe_result",
                    "peer_key": peer_key,
                    "capacity_kbps": 8000,
                    "profile": profile,
                });
                if let Ok(bytes) = serde_json::to_vec(&ev) {
                    self.dispatch_event(0, &bytes);
                }
                Ok(())
            }
            "diag.panic" => {
                tracing::warn!("Executing intentional panic test via diag.panic command");
                panic!("Intentional diagnostic panic via diag.panic command");
            }
            _ => {
                tracing::info!("Unhandled command: {}", cmd);
                Ok(())
            }
        }
    }

    pub fn query(&self, json_str: &str) -> Result<String, String> {
        let val: Value = serde_json::from_str(json_str)
            .map_err(|e| format!("Invalid query JSON: {}", e))?;

        let query = val.get("query").and_then(|v| v.as_str()).unwrap_or("");
        match query {
            "version" => {
                let resp = serde_json::json!({
                    "version": "0.1.0",
                    "abi_version": 1,
                    "engine": "rust"
                });
                Ok(resp.to_string())
            }
            "audio.devices" => {
                let inputs = WasapiDeviceManager::list_input_devices();
                let outputs = WasapiDeviceManager::list_output_devices();
                let resp = serde_json::json!({
                    "inputs": inputs.into_iter().map(|d| serde_json::json!({
                        "id": d.id,
                        "name": d.name,
                        "is_default": d.is_default
                    })).collect::<Vec<_>>(),
                    "outputs": outputs.into_iter().map(|d| serde_json::json!({
                        "id": d.id,
                        "name": d.name,
                        "is_default": d.is_default
                    })).collect::<Vec<_>>(),
                });
                Ok(resp.to_string())
            }
            "audio.levels" => {
                let levels = self.audio.get_audio_levels();
                let resp = serde_json::json!({
                    "levels": levels,
                });
                Ok(resp.to_string())
            }
            "audio.peer_gain" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("");
                let gain = self.audio.get_peer_leveling_gain_db(peer_key);
                let resp = serde_json::json!({
                    "peer_key": peer_key,
                    "gain_db": gain,
                });
                Ok(resp.to_string())
            }
            "net.stats" => {
                let stats = self.net.get_peer_stats();
                let resp = serde_json::json!({
                    "stats": stats,
                });
                Ok(resp.to_string())
            }
            "net.has_connected_peers" => {
                let has = self.net.has_connected_peers();
                let resp = serde_json::json!({
                    "has_connected_peers": has,
                });
                Ok(resp.to_string())
            }
            "net.is_control_open" => {
                let peer_key = val.get("peer_key").and_then(|v| v.as_str()).unwrap_or("");
                let open = self.net.is_control_open(peer_key);
                let resp = serde_json::json!({
                    "is_control_open": open,
                });
                Ok(resp.to_string())
            }
            "share.stats" => {
                let stats = self.video.get_stats();
                let resp = serde_json::json!({
                    "share_stats": stats,
                });
                Ok(resp.to_string())
            }
            "share.is_active" => {
                let active = self.video.is_sharing();
                let resp = serde_json::json!({
                    "is_active": active,
                });
                Ok(resp.to_string())
            }
            "gs.stats" => {
                let stats = self.gamestream.get_stats();
                let resp = serde_json::json!({
                    "gs_stats": stats,
                });
                Ok(resp.to_string())
            }
            "gs.is_active" => {
                let active = self.gamestream.is_active();
                let resp = serde_json::json!({
                    "is_active": active,
                });
                Ok(resp.to_string())
            }
            _ => {
                let resp = serde_json::json!({
                    "error": format!("Unknown query: {}", query)
                });
                Ok(resp.to_string())
            }
        }
    }

    pub fn enter_runtime(&self) -> Option<tokio::runtime::EnterGuard<'_>> {
        self.runtime.as_ref().map(|rt| rt.enter())
    }

    pub fn shutdown(&self) {
        if self.is_running.swap(false, Ordering::SeqCst) {
            tracing::info!("Shutting down RunaEngine...");
            self.audio.stop_devices();
        }
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.shutdown();
        if let Some(rt) = self.runtime.take() {
            // If dropping from an active Tokio runtime thread, drop in a dedicated OS thread
            // to avoid "Cannot drop a runtime in a context where blocking is not allowed"
            if tokio::runtime::Handle::try_current().is_ok() {
                let _ = std::thread::spawn(move || {
                    drop(rt);
                }).join();
            } else {
                drop(rt);
            }
        }
    }
}
