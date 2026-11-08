use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use tokio::sync::mpsc;
use tracing::info;

use crate::stats::{GameStreamStats, StatsCollector};
use crate::tcp::TcpSessionManager;
use crate::udp::UdpSessionManager;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TunnelRole {
    Host,
    Client,
}

pub struct GameStreamTunnel {
    pub role: TunnelRole,
    pub is_active: Arc<AtomicBool>,
    pub stats: Arc<StatsCollector>,
    pub tcp_manager: Arc<TcpSessionManager>,
    pub udp_manager: Arc<UdpSessionManager>,
    // WebRTC outgoing channels
    pub tcp_outgoing_rx: Arc<Mutex<Option<mpsc::Receiver<Vec<u8>>>>>,
    pub udp_outgoing_rx: Arc<Mutex<Option<mpsc::Receiver<Vec<u8>>>>>,
    task_handles: Arc<Mutex<Vec<tokio::task::JoinHandle<()>>>>,
    handle: tokio::runtime::Handle,
}

impl GameStreamTunnel {
    pub fn new(role: TunnelRole, handle: tokio::runtime::Handle) -> Self {
        let (tcp_tx, tcp_rx) = mpsc::channel::<Vec<u8>>(512);
        let (udp_tx, udp_rx) = mpsc::channel::<Vec<u8>>(1024);

        let is_host = role == TunnelRole::Host;
        let stats = Arc::new(StatsCollector::new());

        let tcp_manager = Arc::new(TcpSessionManager::new(is_host, tcp_tx, handle.clone()));
        let udp_manager = Arc::new(UdpSessionManager::new(is_host, udp_tx, handle.clone()));

        Self {
            role,
            is_active: Arc::new(AtomicBool::new(false)),
            stats,
            tcp_manager,
            udp_manager,
            tcp_outgoing_rx: Arc::new(Mutex::new(Some(tcp_rx))),
            udp_outgoing_rx: Arc::new(Mutex::new(Some(udp_rx))),
            task_handles: Arc::new(Mutex::new(Vec::new())),
            handle,
        }
    }

    pub async fn start(&self, bind_ip: &str) {
        if self.is_active.swap(true, Ordering::SeqCst) {
            return; // already active
        }

        let mut handles = Vec::new();

        match self.role {
            TunnelRole::Host => {
                info!("Starting GameStream Host Tunnel...");
                let mut udp_handles = self.udp_manager.start_host_sockets().await;
                handles.append(&mut udp_handles);
            }
            TunnelRole::Client => {
                info!("Starting GameStream Client Tunnel on {}...", bind_ip);
                let mut tcp_handles = self.tcp_manager.start_client_listeners(bind_ip).await;
                let mut udp_handles = self.udp_manager.start_client_listeners(bind_ip).await;
                handles.append(&mut tcp_handles);
                handles.append(&mut udp_handles);
            }
        }

        // Stats watchdog loop (1 Hz)
        let stats = self.stats.clone();
        let is_active = self.is_active.clone();
        let watchdog_handle = self.handle.spawn(async move {
            while is_active.load(Ordering::Relaxed) {
                tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
                let is_high_loss = stats.tick_loss_watchdog();
                if is_high_loss {
                    tracing::warn!("GameStream High Packet Loss (>5%) detected for 10+ seconds!");
                }
            }
        });
        handles.push(watchdog_handle);

        *self.task_handles.lock() = handles;
    }

    pub async fn handle_incoming_tcp(&self, data: &[u8]) {
        self.stats.record_bytes_received(data.len());
        self.tcp_manager.handle_incoming_frame(data).await;
    }

    pub async fn handle_incoming_udp(&self, data: &[u8]) {
        self.stats.record_packet_received(data.len());
        self.udp_manager.handle_incoming_packet(data).await;
    }

    pub fn get_stats(&self) -> GameStreamStats {
        self.stats.snapshot(self.is_active.load(Ordering::Relaxed))
    }

    pub fn stop(&self) {
        if !self.is_active.swap(false, Ordering::SeqCst) {
            return;
        }

        info!("Stopping GameStream Tunnel...");
        self.tcp_manager.stop();
        self.udp_manager.stop();

        for h in self.task_handles.lock().drain(..) {
            h.abort();
        }
    }
}

impl Drop for GameStreamTunnel {
    fn drop(&mut self) {
        self.stop();
    }
}
