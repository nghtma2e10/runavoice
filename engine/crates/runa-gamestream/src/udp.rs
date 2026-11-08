use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicU16, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use tokio::net::UdpSocket;
use tokio::sync::mpsc;
use tracing::warn;

pub const UDP_MAGIC: u16 = 0x5247; // "RG" (Section 7.3)
pub const MAX_UDP_PAYLOAD: usize = 1150; // MTU safe payload limit (Section 7.3)
pub const ALLOWED_UDP_PORTS: [u16; 3] = [47998, 47999, 48000];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UdpPacket {
    pub magic: u16,
    pub port: u16,
    pub flow_id: u32,
    pub seq: u16,
    pub payload: Vec<u8>,
}

impl UdpPacket {
    pub fn new(port: u16, flow_id: u32, seq: u16, payload: Vec<u8>) -> Self {
        Self {
            magic: UDP_MAGIC,
            port,
            flow_id,
            seq,
            payload,
        }
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut buf = Vec::with_capacity(10 + self.payload.len());
        buf.extend_from_slice(&self.magic.to_le_bytes());
        buf.extend_from_slice(&self.port.to_le_bytes());
        buf.extend_from_slice(&self.flow_id.to_le_bytes());
        buf.extend_from_slice(&self.seq.to_le_bytes());
        buf.extend_from_slice(&self.payload);
        buf
    }

    pub fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 10 {
            return None;
        }
        let magic = u16::from_le_bytes([bytes[0], bytes[1]]);
        if magic != UDP_MAGIC {
            return None;
        }

        let port = u16::from_le_bytes([bytes[2], bytes[3]]);
        let flow_id = u32::from_le_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]);
        let seq = u16::from_le_bytes([bytes[8], bytes[9]]);
        let payload = bytes[10..].to_vec();

        Some(Self {
            magic,
            port,
            flow_id,
            seq,
            payload,
        })
    }
}

pub struct UdpSessionManager {
    is_host: bool,
    active: Arc<AtomicBool>,
    seq_counter: Arc<AtomicU16>,
    // Channel to send encoded 0x5247 packets over WebRTC unordered data channel (runa-gs-udp)
    outgoing_tx: mpsc::Sender<Vec<u8>>,
    // Host sockets: port -> UdpSocket
    host_sockets: Arc<Mutex<HashMap<u16, Arc<UdpSocket>>>>,
    // Client listeners: port -> UdpSocket
    client_sockets: Arc<Mutex<HashMap<u16, Arc<UdpSocket>>>>,
    // Client moonlight endpoint mapping: port -> SocketAddr
    moonlight_endpoints: Arc<Mutex<HashMap<u16, SocketAddr>>>,
    handle: tokio::runtime::Handle,
}

impl UdpSessionManager {
    pub fn new(is_host: bool, outgoing_tx: mpsc::Sender<Vec<u8>>, handle: tokio::runtime::Handle) -> Self {
        Self {
            is_host,
            active: Arc::new(AtomicBool::new(true)),
            seq_counter: Arc::new(AtomicU16::new(1)),
            outgoing_tx,
            host_sockets: Arc::new(Mutex::new(HashMap::new())),
            client_sockets: Arc::new(Mutex::new(HashMap::new())),
            moonlight_endpoints: Arc::new(Mutex::new(HashMap::new())),
            handle,
        }
    }

    pub fn is_host(&self) -> bool {
        self.is_host
    }

    pub async fn start_host_sockets(&self) -> Vec<tokio::task::JoinHandle<()>> {
        let mut handles = Vec::new();

        for &port in &ALLOWED_UDP_PORTS {
            let socket = match UdpSocket::bind("127.0.0.1:0").await {
                Ok(s) => Arc::new(s),
                Err(e) => {
                    warn!("Host failed to bind UDP loopback socket for port {}: {}", port, e);
                    continue;
                }
            };

            self.host_sockets.lock().insert(port, socket.clone());

            let outgoing_tx = self.outgoing_tx.clone();
            let active = self.active.clone();
            let seq_counter = self.seq_counter.clone();

            let handle = self.handle.spawn(async move {
                let mut buf = vec![0u8; 2048];
                while active.load(Ordering::Relaxed) {
                    match socket.recv_from(&mut buf).await {
                        Ok((n, _src)) => {
                            if n > MAX_UDP_PAYLOAD {
                                continue;
                            }
                            let seq = seq_counter.fetch_add(1, Ordering::Relaxed);
                            let packet = UdpPacket::new(port, 1, seq, buf[..n].to_vec());
                            if outgoing_tx.send(packet.encode()).await.is_err() {
                                break;
                            }
                        }
                        Err(_) => break,
                    }
                }
            });

            handles.push(handle);
        }

        handles
    }

    pub async fn start_client_listeners(&self, bind_ip: &str) -> Vec<tokio::task::JoinHandle<()>> {
        let mut handles = Vec::new();

        for &port in &ALLOWED_UDP_PORTS {
            let addr_str = format!("{}:{}", bind_ip, port);
            let socket = match UdpSocket::bind(&addr_str).await {
                Ok(s) => Arc::new(s),
                Err(e) => {
                    warn!("Client failed to bind UDP listener on {}: {}", addr_str, e);
                    continue;
                }
            };

            self.client_sockets.lock().insert(port, socket.clone());

            let outgoing_tx = self.outgoing_tx.clone();
            let active = self.active.clone();
            let seq_counter = self.seq_counter.clone();
            let moonlight_endpoints = self.moonlight_endpoints.clone();

            let handle = self.handle.spawn(async move {
                let mut buf = vec![0u8; 2048];
                while active.load(Ordering::Relaxed) {
                    match socket.recv_from(&mut buf).await {
                        Ok((n, src)) => {
                            moonlight_endpoints.lock().insert(port, src);
                            if n > MAX_UDP_PAYLOAD {
                                continue;
                            }
                            let seq = seq_counter.fetch_add(1, Ordering::Relaxed);
                            let packet = UdpPacket::new(port, 1, seq, buf[..n].to_vec());
                            if outgoing_tx.send(packet.encode()).await.is_err() {
                                break;
                            }
                        }
                        Err(_) => break,
                    }
                }
            });

            handles.push(handle);
        }

        handles
    }

    pub async fn handle_incoming_packet(&self, raw_bytes: &[u8]) {
        let packet = match UdpPacket::decode(raw_bytes) {
            Some(p) => p,
            None => return,
        };

        if self.is_host {
            // Host forwards payload to Sunshine 127.0.0.1:{port}
            let socket_opt = self.host_sockets.lock().get(&packet.port).cloned();
            if let Some(socket) = socket_opt {
                if let Ok(target) = format!("127.0.0.1:{}", packet.port).parse::<SocketAddr>() {
                    let _ = socket.send_to(&packet.payload, target).await;
                }
            }
        } else {
            // Client forwards payload to Moonlight's saved local endpoint
            let socket_opt = self.client_sockets.lock().get(&packet.port).cloned();
            let ep_opt = self.moonlight_endpoints.lock().get(&packet.port).cloned();

            if let (Some(socket), Some(ep)) = (socket_opt, ep_opt) {
                let _ = socket.send_to(&packet.payload, ep).await;
            }
        }
    }

    pub fn stop(&self) {
        self.active.store(false, Ordering::SeqCst);
        self.host_sockets.lock().clear();
        self.client_sockets.lock().clear();
        self.moonlight_endpoints.lock().clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_udp_packet_encode_decode_roundtrip() {
        let payload = vec![0x12, 0x34, 0x56, 0x78];
        let packet = UdpPacket::new(47998, 999, 1234, payload.clone());

        let encoded = packet.encode();
        assert_eq!(encoded.len(), 10 + payload.len());

        let decoded = UdpPacket::decode(&encoded).expect("Decode failed");
        assert_eq!(decoded.magic, UDP_MAGIC);
        assert_eq!(decoded.port, 47998);
        assert_eq!(decoded.flow_id, 999);
        assert_eq!(decoded.seq, 1234);
        assert_eq!(decoded.payload, payload);
    }
}
