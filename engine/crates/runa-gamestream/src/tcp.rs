use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc;
use tracing::{debug, info, warn};

pub const TCP_FRAME_OPEN: u8 = 1;
pub const TCP_FRAME_DATA: u8 = 2;
pub const TCP_FRAME_CLOSE: u8 = 3;

pub const MAX_TCP_DATA_CHUNK: usize = 16384; // 16 KB (Section 7.2)
pub const ALLOWED_TCP_PORTS: [u16; 3] = [47984, 47989, 48010];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TcpFrame {
    pub frame_type: u8,
    pub conn_id: u32,
    pub port: u16,
    pub payload: Vec<u8>,
}

impl TcpFrame {
    pub fn new(frame_type: u8, conn_id: u32, port: u16, payload: Vec<u8>) -> Self {
        Self {
            frame_type,
            conn_id,
            port,
            payload,
        }
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut buf = Vec::with_capacity(7 + self.payload.len());
        buf.push(self.frame_type);
        buf.extend_from_slice(&self.conn_id.to_le_bytes());
        buf.extend_from_slice(&self.port.to_le_bytes());
        buf.extend_from_slice(&self.payload);
        buf
    }

    pub fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 7 {
            return None;
        }
        let frame_type = bytes[0];
        let conn_id = u32::from_le_bytes([bytes[1], bytes[2], bytes[3], bytes[4]]);
        let port = u16::from_le_bytes([bytes[5], bytes[6]]);
        let payload = bytes[7..].to_vec();

        Some(Self {
            frame_type,
            conn_id,
            port,
            payload,
        })
    }
}

pub struct TcpSessionManager {
    is_host: bool,
    active: Arc<AtomicBool>,
    next_conn_id: Arc<AtomicU32>,
    // Channel to send encoded frames out over WebRTC DataChannel (runa-gs-tcp)
    outgoing_tx: mpsc::Sender<Vec<u8>>,
    // Active TCP client streams: conn_id -> writer channel
    active_sessions: Arc<Mutex<HashMap<u32, mpsc::Sender<Vec<u8>>>>>,
    handle: tokio::runtime::Handle,
}

impl TcpSessionManager {
    pub fn new(is_host: bool, outgoing_tx: mpsc::Sender<Vec<u8>>, handle: tokio::runtime::Handle) -> Self {
        Self {
            is_host,
            active: Arc::new(AtomicBool::new(true)),
            next_conn_id: Arc::new(AtomicU32::new(1)),
            outgoing_tx,
            active_sessions: Arc::new(Mutex::new(HashMap::new())),
            handle,
        }
    }

    pub fn is_host(&self) -> bool {
        self.is_host
    }

    /// Handles incoming frame received from remote peer via runa-gs-tcp DataChannel
    pub async fn handle_incoming_frame(&self, raw_bytes: &[u8]) {
        let frame = match TcpFrame::decode(raw_bytes) {
            Some(f) => f,
            None => {
                warn!("Received malformed TCP frame (length: {})", raw_bytes.len());
                return;
            }
        };

        if self.is_host {
            self.handle_host_incoming_frame(frame).await;
        } else {
            self.handle_client_incoming_frame(frame).await;
        }
    }

    async fn handle_host_incoming_frame(&self, frame: TcpFrame) {
        match frame.frame_type {
            TCP_FRAME_OPEN => {
                // Verify port is in allowed list
                if !ALLOWED_TCP_PORTS.contains(&frame.port) {
                    warn!("Host rejected TCP open to unauthorized port {}", frame.port);
                    let close_frame = TcpFrame::new(TCP_FRAME_CLOSE, frame.conn_id, frame.port, Vec::new());
                    let _ = self.outgoing_tx.send(close_frame.encode()).await;
                    return;
                }

                let target_addr: SocketAddr = match format!("127.0.0.1:{}", frame.port).parse() {
                    Ok(addr) => addr,
                    Err(e) => {
                        warn!("Failed to parse target address for port {}: {}", frame.port, e);
                        return;
                    }
                };
                debug!("Host connecting to local Sunshine service at {}", target_addr);

                match TcpStream::connect(target_addr).await {
                    Ok(stream) => {
                        let (mut reader, mut writer) = stream.into_split();
                        let (write_tx, mut write_rx) = mpsc::channel::<Vec<u8>>(128);

                        self.active_sessions.lock().insert(frame.conn_id, write_tx);

                        let conn_id = frame.conn_id;
                        let port = frame.port;
                        let outgoing_tx = self.outgoing_tx.clone();
                        let active = self.active.clone();
                        let sessions = self.active_sessions.clone();

                        // Writer task: forward incoming WebRTC data to local Sunshine TCP stream
                        self.handle.spawn(async move {
                            while let Some(data) = write_rx.recv().await {
                                if writer.write_all(&data).await.is_err() {
                                    break;
                                }
                            }
                            let _ = writer.shutdown().await;
                        });

                        // Reader task: read from local Sunshine TCP stream and forward to WebRTC
                        self.handle.spawn(async move {
                            let mut buf = vec![0u8; MAX_TCP_DATA_CHUNK];
                            while active.load(Ordering::Relaxed) {
                                match reader.read(&mut buf).await {
                                    Ok(0) => break, // EOF
                                    Ok(n) => {
                                        let data_frame = TcpFrame::new(TCP_FRAME_DATA, conn_id, port, buf[..n].to_vec());
                                        if outgoing_tx.send(data_frame.encode()).await.is_err() {
                                            break;
                                        }
                                    }
                                    Err(_) => break,
                                }
                            }

                            sessions.lock().remove(&conn_id);
                            let close_frame = TcpFrame::new(TCP_FRAME_CLOSE, conn_id, port, Vec::new());
                            let _ = outgoing_tx.send(close_frame.encode()).await;
                        });
                    }
                    Err(e) => {
                        warn!("Host failed to connect to Sunshine on port {}: {}", frame.port, e);
                        let close_frame = TcpFrame::new(TCP_FRAME_CLOSE, frame.conn_id, frame.port, Vec::new());
                        let _ = self.outgoing_tx.send(close_frame.encode()).await;
                    }
                }
            }
            TCP_FRAME_DATA => {
                let tx_opt = self.active_sessions.lock().get(&frame.conn_id).cloned();
                if let Some(tx) = tx_opt {
                    let _ = tx.send(frame.payload).await;
                }
            }
            TCP_FRAME_CLOSE => {
                self.active_sessions.lock().remove(&frame.conn_id);
            }
            _ => {
                warn!("Unknown TCP frame type {}", frame.frame_type);
            }
        }
    }

    async fn handle_client_incoming_frame(&self, frame: TcpFrame) {
        match frame.frame_type {
            TCP_FRAME_DATA => {
                let tx_opt = self.active_sessions.lock().get(&frame.conn_id).cloned();
                if let Some(tx) = tx_opt {
                    let _ = tx.send(frame.payload).await;
                }
            }
            TCP_FRAME_CLOSE => {
                self.active_sessions.lock().remove(&frame.conn_id);
            }
            _ => {}
        }
    }

    /// Client/Player side: Starts TCP listeners on 127.0.0.77 (or 127.0.0.1 for local testing)
    pub async fn start_client_listeners(&self, bind_ip: &str) -> Vec<tokio::task::JoinHandle<()>> {
        let mut handles = Vec::new();

        for &port in &ALLOWED_TCP_PORTS {
            let addr_str = format!("{}:{}", bind_ip, port);
            let listener = match TcpListener::bind(&addr_str).await {
                Ok(l) => {
                    info!("Client TCP proxy listening on {}", addr_str);
                    l
                }
                Err(e) => {
                    // If 127.0.0.77 is not configured or in use, log and continue
                    warn!("Could not bind TCP proxy on {}: {}", addr_str, e);
                    continue;
                }
            };

            let outgoing_tx = self.outgoing_tx.clone();
            let active = self.active.clone();
            let next_conn_id = self.next_conn_id.clone();
            let sessions = self.active_sessions.clone();

            let handle_reader = self.handle.clone();
            let handle_writer = self.handle.clone();
            let handle = self.handle.spawn(async move {
                while active.load(Ordering::Relaxed) {
                    match listener.accept().await {
                        Ok((stream, peer_addr)) => {
                            debug!("Accepted Moonlight TCP connection from {} on port {}", peer_addr, port);
                            let conn_id = next_conn_id.fetch_add(1, Ordering::SeqCst);
                            let (mut reader, mut writer) = stream.into_split();
                            let (write_tx, mut write_rx) = mpsc::channel::<Vec<u8>>(128);

                            sessions.lock().insert(conn_id, write_tx);

                            // Send OPEN frame
                            let open_frame = TcpFrame::new(TCP_FRAME_OPEN, conn_id, port, Vec::new());
                            if outgoing_tx.send(open_frame.encode()).await.is_err() {
                                break;
                            }

                            let outgoing_tx_cl = outgoing_tx.clone();
                            let sessions_cl = sessions.clone();
                            let active_cl = active.clone();

                            // Reader task: read from Moonlight and send DATA frames to Host
                            handle_reader.spawn(async move {
                                let mut buf = vec![0u8; MAX_TCP_DATA_CHUNK];
                                while active_cl.load(Ordering::Relaxed) {
                                    match reader.read(&mut buf).await {
                                        Ok(0) => break,
                                        Ok(n) => {
                                            let data_frame = TcpFrame::new(TCP_FRAME_DATA, conn_id, port, buf[..n].to_vec());
                                            if outgoing_tx_cl.send(data_frame.encode()).await.is_err() {
                                                break;
                                            }
                                        }
                                        Err(_) => break,
                                    }
                                }

                                sessions_cl.lock().remove(&conn_id);
                                let close_frame = TcpFrame::new(TCP_FRAME_CLOSE, conn_id, port, Vec::new());
                                let _ = outgoing_tx_cl.send(close_frame.encode()).await;
                            });

                            // Writer task: forward incoming frames from Host to Moonlight
                            handle_writer.spawn(async move {
                                while let Some(data) = write_rx.recv().await {
                                    if writer.write_all(&data).await.is_err() {
                                        break;
                                    }
                                }
                                let _ = writer.shutdown().await;
                            });
                        }
                        Err(_) => break,
                    }
                }
            });

            handles.push(handle);
        }

        handles
    }

    pub fn stop(&self) {
        self.active.store(false, Ordering::SeqCst);
        self.active_sessions.lock().clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_tcp_frame_encode_decode_roundtrip() {
        let payload = b"GET /serverinfo HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n".to_vec();
        let frame = TcpFrame::new(TCP_FRAME_DATA, 42, 47989, payload.clone());

        let encoded = frame.encode();
        assert_eq!(encoded.len(), 7 + payload.len());

        let decoded = TcpFrame::decode(&encoded).expect("Decode failed");
        assert_eq!(decoded.frame_type, TCP_FRAME_DATA);
        assert_eq!(decoded.conn_id, 42);
        assert_eq!(decoded.port, 47989);
        assert_eq!(decoded.payload, payload);
    }
}
