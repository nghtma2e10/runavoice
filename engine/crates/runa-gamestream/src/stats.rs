use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameStreamStats {
    pub is_active: bool,
    pub bytes_sent: u64,
    pub bytes_received: u64,
    pub packets_sent: u64,
    pub packets_received: u64,
    pub packets_lost: u64,
    pub loss_rate_percent: f64,
    pub rtt_ms: f64,
    pub is_high_loss: bool,
}

pub struct StatsCollector {
    pub bytes_sent: Arc<AtomicU64>,
    pub bytes_received: Arc<AtomicU64>,
    pub packets_sent: Arc<AtomicU64>,
    pub packets_received: Arc<AtomicU64>,
    pub packets_lost: Arc<AtomicU64>,
    last_rx: Arc<AtomicU64>,
    last_lost: Arc<AtomicU64>,
    pub loss_rate: Arc<Mutex<f64>>,
    pub rtt_ms: Arc<Mutex<f64>>,
    pub consecutive_high_loss_seconds: Arc<AtomicU64>,
}

impl Default for StatsCollector {
    fn default() -> Self {
        Self::new()
    }
}

impl StatsCollector {
    pub fn new() -> Self {
        Self {
            bytes_sent: Arc::new(AtomicU64::new(0)),
            bytes_received: Arc::new(AtomicU64::new(0)),
            packets_sent: Arc::new(AtomicU64::new(0)),
            packets_received: Arc::new(AtomicU64::new(0)),
            packets_lost: Arc::new(AtomicU64::new(0)),
            last_rx: Arc::new(AtomicU64::new(0)),
            last_lost: Arc::new(AtomicU64::new(0)),
            loss_rate: Arc::new(Mutex::new(0.0)),
            rtt_ms: Arc::new(Mutex::new(0.0)),
            consecutive_high_loss_seconds: Arc::new(AtomicU64::new(0)),
        }
    }

    pub fn record_bytes_sent(&self, n: usize) {
        self.bytes_sent.fetch_add(n as u64, Ordering::Relaxed);
    }

    pub fn record_bytes_received(&self, n: usize) {
        self.bytes_received.fetch_add(n as u64, Ordering::Relaxed);
    }

    pub fn record_packet_sent(&self, bytes: usize) {
        self.packets_sent.fetch_add(1, Ordering::Relaxed);
        self.bytes_sent.fetch_add(bytes as u64, Ordering::Relaxed);
    }

    pub fn record_packet_received(&self, bytes: usize) {
        self.packets_received.fetch_add(1, Ordering::Relaxed);
        self.bytes_received.fetch_add(bytes as u64, Ordering::Relaxed);
    }

    pub fn record_packet_lost(&self) {
        self.packets_lost.fetch_add(1, Ordering::Relaxed);
    }

    pub fn set_rtt_ms(&self, rtt: f64) {
        *self.rtt_ms.lock() = rtt;
    }

    /// Evaluates 1-second interval loss rate and checks 10-second high loss threshold (> 5%)
    pub fn tick_loss_watchdog(&self) -> bool {
        let current_rx = self.packets_received.load(Ordering::Relaxed);
        let current_lost = self.packets_lost.load(Ordering::Relaxed);

        let last_rx = self.last_rx.swap(current_rx, Ordering::Relaxed);
        let last_lost = self.last_lost.swap(current_lost, Ordering::Relaxed);

        let rx_diff = current_rx.saturating_sub(last_rx);
        let lost_diff = current_lost.saturating_sub(last_lost);
        let total = rx_diff + lost_diff;

        let rate = if total > 0 {
            (lost_diff as f64 * 100.0) / (total as f64)
        } else {
            0.0
        };

        *self.loss_rate.lock() = rate;

        if rate > 5.0 {
            let count = self.consecutive_high_loss_seconds.fetch_add(1, Ordering::Relaxed) + 1;
            count >= 10
        } else {
            self.consecutive_high_loss_seconds.store(0, Ordering::Relaxed);
            false
        }
    }

    pub fn snapshot(&self, is_active: bool) -> GameStreamStats {
        let is_high_loss = self.consecutive_high_loss_seconds.load(Ordering::Relaxed) >= 10;
        GameStreamStats {
            is_active,
            bytes_sent: self.bytes_sent.load(Ordering::Relaxed),
            bytes_received: self.bytes_received.load(Ordering::Relaxed),
            packets_sent: self.packets_sent.load(Ordering::Relaxed),
            packets_received: self.packets_received.load(Ordering::Relaxed),
            packets_lost: self.packets_lost.load(Ordering::Relaxed),
            loss_rate_percent: *self.loss_rate.lock(),
            rtt_ms: *self.rtt_ms.lock(),
            is_high_loss,
        }
    }
}
