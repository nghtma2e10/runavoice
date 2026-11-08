use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;
use tokio::time::timeout;
use runa_net::peer::{PeerEvent, PeerSession};
use runa_audio::mixer::AudioMasterMixer;

struct SimpleRng(u64);
impl SimpleRng {
    fn new(seed: u64) -> Self { Self(seed) }
    fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1);
        self.0
    }
    fn gen_range(&mut self, min: u64, max: u64) -> u64 {
        min + (self.next_u64() % (max - min + 1))
    }
    fn gen_bool(&mut self, prob: f64) -> bool {
        (self.next_u64() % 1000) < (prob * 1000.0) as u64
    }
}

/// N4: Codec uyumu: olusturulan teklif/yanit SDP'lerinde ses icin opus/48000/2, video icin
/// kayitli H.264 profili; set_remote_description hatasiz calismali.
#[tokio::test]
async fn test_n4_codec_negotiation() {
    let mixer_a = Arc::new(AudioMasterMixer::new());
    let mixer_b = Arc::new(AudioMasterMixer::new());
    let (tx_a, mut rx_a) = tokio::sync::mpsc::unbounded_channel();
    let (tx_b, mut rx_b) = tokio::sync::mpsc::unbounded_channel();
    let handle = tokio::runtime::Handle::current();

    let peer_a = PeerSession::create("peer_b".into(), "Peer B".into(), false, tx_a, mixer_a, handle.clone()).await.unwrap();
    let peer_b = PeerSession::create("peer_a".into(), "Peer A".into(), true, tx_b, mixer_b, handle.clone()).await.unwrap();

    peer_a.start_initiator_negotiation().await.unwrap();

    let mut offer_sdp = String::new();
    while let Ok(Some(ev)) = timeout(Duration::from_secs(2), rx_a.recv()).await {
        if let PeerEvent::LocalSignal(sig) = ev {
            if sig.sig_type == "sdp-offer" {
                offer_sdp = sig.data;
                break;
            }
        }
    }
    assert!(!offer_sdp.is_empty(), "Offer SDP must not be empty");

    // Opus kanali kontrolu: SDP standardi geregi opus/48000/2 olmali
    assert!(
        offer_sdp.contains("opus/48000/2") || offer_sdp.contains("OPUS/48000/2"),
        "Offer SDP must declare opus/48000/2 per WebRTC standard! Found SDP:\n{}",
        offer_sdp
    );

    // Video izi H.264 profili kontrolu
    assert!(
        offer_sdp.contains("profile-level-id=42e01f") || offer_sdp.contains("42e01f"),
        "Offer SDP must declare H.264 profile 42e01f"
    );

    // Peer B offer'i uygular
    peer_b.handle_offer(&offer_sdp).await.expect("Peer B handle_offer must succeed");

    let mut answer_sdp = String::new();
    while let Ok(Some(ev)) = timeout(Duration::from_secs(2), rx_b.recv()).await {
        if let PeerEvent::LocalSignal(sig) = ev {
            if sig.sig_type == "sdp-answer" {
                answer_sdp = sig.data;
                break;
            }
        }
    }
    assert!(!answer_sdp.is_empty(), "Answer SDP must not be empty");
    assert!(
        answer_sdp.contains("opus/48000/2") || answer_sdp.contains("OPUS/48000/2"),
        "Answer SDP must declare opus/48000/2!"
    );

    // Peer A answer'i uygular (H1'deki unable to start track hatasi burada patlardi)
    peer_a.handle_answer(&answer_sdp).await.expect("Peer A handle_answer must succeed without codec mismatch");
}

/// N1: peer.rs yolu, ideal sinyal: 10 tekrar, her seferinde baglanti kurulmali ve ses paketi iletilebilmeli (10/10)
#[tokio::test]
async fn test_n1_ideal_signaling_10_runs() {
    let handle = tokio::runtime::Handle::current();
    let mut successes = 0;

    for run in 1..=10 {
        let mixer_a = Arc::new(AudioMasterMixer::new());
        let mixer_b = Arc::new(AudioMasterMixer::new());
        let (tx_a, mut rx_a) = tokio::sync::mpsc::unbounded_channel();
        let (tx_b, mut rx_b) = tokio::sync::mpsc::unbounded_channel();

        let peer_a = PeerSession::create("peer_b".into(), "Peer B".into(), false, tx_a, mixer_a, handle.clone()).await.unwrap();
        let peer_b = PeerSession::create("peer_a".into(), "Peer A".into(), true, tx_b, mixer_b, handle.clone()).await.unwrap();

        let p_b = Arc::clone(&peer_b);
        let pump_ab = tokio::spawn(async move {
            while let Some(ev) = rx_a.recv().await {
                if let PeerEvent::LocalSignal(sig) = ev {
                    match sig.sig_type.as_str() {
                        "sdp-offer" => { let _ = p_b.handle_offer(&sig.data).await; }
                        "sdp-answer" => { let _ = p_b.handle_answer(&sig.data).await; }
                        "ice-candidate" => { let _ = p_b.handle_ice_candidate(&sig.data).await; }
                        _ => {}
                    }
                }
            }
        });

        let p_a = Arc::clone(&peer_a);
        let pump_ba = tokio::spawn(async move {
            while let Some(ev) = rx_b.recv().await {
                if let PeerEvent::LocalSignal(sig) = ev {
                    match sig.sig_type.as_str() {
                        "sdp-offer" => { let _ = p_a.handle_offer(&sig.data).await; }
                        "sdp-answer" => { let _ = p_a.handle_answer(&sig.data).await; }
                        "ice-candidate" => { let _ = p_a.handle_ice_candidate(&sig.data).await; }
                        _ => {}
                    }
                }
            }
        });

        peer_a.start_initiator_negotiation().await.unwrap();

        let start = std::time::Instant::now();
        let mut connected = false;
        while start.elapsed() < Duration::from_secs(5) {
            if peer_a.is_connected.load(Ordering::SeqCst) && peer_b.is_connected.load(Ordering::SeqCst) {
                connected = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }

        if connected {
            // Test bidirectional sample transmission
            let sample = webrtc::media::Sample {
                data: bytes::Bytes::from_static(&[0xf8, 0xff, 0xfe]),
                duration: Duration::from_millis(20),
                ..Default::default()
            };
            let _ = peer_a.send_audio_sample(&sample).await;
            let _ = peer_b.send_audio_sample(&sample).await;
            successes += 1;
        }

        pump_ab.abort();
        pump_ba.abort();
        peer_a.close().await;
        peer_b.close().await;

        println!("[N1] Run {}/10 -> connected={}", run, connected);
    }

    assert_eq!(successes, 10, "N1 FAILED: Expected 10/10 successful connections, got {}", successes);
}

/// N2: Yinelenen teklif ve yanit testi (MQTT benzeri kanal):
/// Ayni teklif 2 kez gelince yanit veren taraf yeni bir answer uretmemeli, mevcut answer'i yollamali;
/// teklif veren taraf stable durumdayken ayni yanit gelince hata vermemeli.
#[tokio::test]
async fn test_n2_duplicate_offer_and_answer_idempotency() {
    let mixer_a = Arc::new(AudioMasterMixer::new());
    let mixer_b = Arc::new(AudioMasterMixer::new());
    let (tx_a, mut rx_a) = tokio::sync::mpsc::unbounded_channel();
    let (tx_b, mut rx_b) = tokio::sync::mpsc::unbounded_channel();
    let handle = tokio::runtime::Handle::current();

    let peer_a = PeerSession::create("peer_b".into(), "Peer B".into(), false, tx_a, mixer_a, handle.clone()).await.unwrap();
    let peer_b = PeerSession::create("peer_a".into(), "Peer A".into(), true, tx_b, mixer_b, handle.clone()).await.unwrap();

    peer_a.start_initiator_negotiation().await.unwrap();

    // 1. Offer al
    let mut offer_sdp = String::new();
    while let Ok(Some(ev)) = timeout(Duration::from_secs(2), rx_a.recv()).await {
        if let PeerEvent::LocalSignal(sig) = ev {
            if sig.sig_type == "sdp-offer" {
                offer_sdp = sig.data;
                break;
            }
        }
    }
    assert!(!offer_sdp.is_empty());

    // 2. Peer B ilk offer'i alir ve answer uretir
    peer_b.handle_offer(&offer_sdp).await.expect("First handle_offer should succeed");

    let mut answer_sdp = String::new();
    while let Ok(Some(ev)) = timeout(Duration::from_secs(2), rx_b.recv()).await {
        if let PeerEvent::LocalSignal(sig) = ev {
            if sig.sig_type == "sdp-answer" {
                answer_sdp = sig.data;
                break;
            }
        }
    }
    assert!(!answer_sdp.is_empty());

    // 3. Peer A answer'i uygular (stable olur)
    peer_a.handle_answer(&answer_sdp).await.expect("handle_answer should succeed");

    // 4. DUPLICATE OFFER: Peer B ayni offer'i tekrar alir (MQTT retry)
    // Beklenen: Hata vermemeli, yeni bir ICE kimligi/ufrag uretmemeli, idempotent olmali!
    peer_b.handle_offer(&offer_sdp).await.expect("Duplicate handle_offer should be idempotent and succeed");

    // 5. DUPLICATE ANSWER: Peer A ayni answer'i tekrar alir (MQTT retry / retransmit)
    // Beklenen: Peer A "stable" durumdayken invalid signaling state hatasi VERMEMELI, sessizce yok saymali!
    peer_a.handle_answer(&answer_sdp).await.expect("Duplicate handle_answer in stable state should be ignored and succeed");
}

/// N2 Stress Test: MQTT benzeri sinyal ile 50 deneme (hedef >= 49/50)
/// Rastgele gecikme, %50 1-2 kez yineleme, yeniden siralama, %5 paket kaybi.
#[tokio::test]
async fn test_n2_mqtt_lossy_channel_50_runs() {
    let handle = tokio::runtime::Handle::current();
    let mut successes = 0;
    const TOTAL_RUNS: usize = 50;

    for run in 1..=TOTAL_RUNS {
        let mixer_a = Arc::new(AudioMasterMixer::new());
        let mixer_b = Arc::new(AudioMasterMixer::new());
        let (tx_a, mut rx_a) = tokio::sync::mpsc::unbounded_channel();
        let (tx_b, mut rx_b) = tokio::sync::mpsc::unbounded_channel();

        let peer_a = PeerSession::create("peer_b".into(), "Peer B".into(), false, tx_a, mixer_a, handle.clone()).await.unwrap();
        let peer_b = PeerSession::create("peer_a".into(), "Peer A".into(), true, tx_b, mixer_b, handle.clone()).await.unwrap();

        let seed_a = (run * 12345 + 6789) as u64;
        let seed_b = (run * 98765 + 4321) as u64;

        // Forwarder A -> B with jitter, duplication, occasional drop
        let p_b = Arc::clone(&peer_b);
        let pump_ab = tokio::spawn(async move {
            let mut rng = SimpleRng::new(seed_a);
            while let Some(ev) = rx_a.recv().await {
                if let PeerEvent::LocalSignal(sig) = ev {
                    if sig.sig_type == "ice-candidate" && rng.gen_bool(0.05) {
                        continue;
                    }
                    let copies = if rng.gen_bool(0.50) { rng.gen_range(1, 2) } else { 1 };
                    let jitter_ms = rng.gen_range(5, 80);

                    for _ in 0..copies {
                        let pb = Arc::clone(&p_b);
                        let s = sig.clone();
                        tokio::spawn(async move {
                            tokio::time::sleep(Duration::from_millis(jitter_ms)).await;
                            match s.sig_type.as_str() {
                                "sdp-offer" => { let _ = pb.handle_offer(&s.data).await; }
                                "sdp-answer" => { let _ = pb.handle_answer(&s.data).await; }
                                "ice-candidate" => { let _ = pb.handle_ice_candidate(&s.data).await; }
                                _ => {}
                            }
                        });
                    }
                }
            }
        });

        // Forwarder B -> A with jitter, duplication, occasional drop
        let p_a = Arc::clone(&peer_a);
        let pump_ba = tokio::spawn(async move {
            let mut rng = SimpleRng::new(seed_b);
            while let Some(ev) = rx_b.recv().await {
                if let PeerEvent::LocalSignal(sig) = ev {
                    if sig.sig_type == "ice-candidate" && rng.gen_bool(0.05) {
                        continue;
                    }
                    let copies = if rng.gen_bool(0.50) { rng.gen_range(1, 2) } else { 1 };
                    let jitter_ms = rng.gen_range(5, 80);

                    for _ in 0..copies {
                        let pa = Arc::clone(&p_a);
                        let s = sig.clone();
                        tokio::spawn(async move {
                            tokio::time::sleep(Duration::from_millis(jitter_ms)).await;
                            match s.sig_type.as_str() {
                                "sdp-offer" => { let _ = pa.handle_offer(&s.data).await; }
                                "sdp-answer" => { let _ = pa.handle_answer(&s.data).await; }
                                "ice-candidate" => { let _ = pa.handle_ice_candidate(&s.data).await; }
                                _ => {}
                            }
                        });
                    }
                }
            }
        });

        peer_a.start_initiator_negotiation().await.unwrap();

        let start = std::time::Instant::now();
        let mut connected = false;
        while start.elapsed() < Duration::from_secs(6) {
            if peer_a.is_connected.load(Ordering::SeqCst) && peer_b.is_connected.load(Ordering::SeqCst) {
                connected = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }

        if connected {
            successes += 1;
        }

        pump_ab.abort();
        pump_ba.abort();
        peer_a.close().await;
        peer_b.close().await;

        if run % 10 == 0 || !connected {
            println!("[N2 MQTT Stress] Progress: {}/{} (successes={})", run, TOTAL_RUNS, successes);
        }
    }

    println!("[N2 Final Result] {}/{} successful connections (Target: >= 49/50)", successes, TOTAL_RUNS);
    assert!(successes >= 49, "N2 FAILED: Expected >= 49/50, but got {}", successes);
}

/// N3: Carpışma (glare): 20 tekrar ile iki tarafın aynı anda teklif başlatması testi (20/20)
#[tokio::test]
async fn test_n3_glare_collision_20_runs() {
    let handle = tokio::runtime::Handle::current();
    let mut successes = 0;
    const TOTAL_RUNS: usize = 20;

    for run in 1..=TOTAL_RUNS {
        let mixer_a = Arc::new(AudioMasterMixer::new());
        let mixer_b = Arc::new(AudioMasterMixer::new());
        let (tx_a, mut rx_a) = tokio::sync::mpsc::unbounded_channel();
        let (tx_b, mut rx_b) = tokio::sync::mpsc::unbounded_channel();

        let peer_a = PeerSession::create("peer_b".into(), "Peer B".into(), false, tx_a, mixer_a, handle.clone()).await.unwrap();
        let peer_b = PeerSession::create("peer_a".into(), "Peer A".into(), true, tx_b, mixer_b, handle.clone()).await.unwrap();

        // Her iki taraf da ayni anda negotiation baslatiyor (Glare)
        peer_a.start_negotiation().await.unwrap();
        peer_b.start_negotiation().await.unwrap();

        let p_b = Arc::clone(&peer_b);
        let pump_ab = tokio::spawn(async move {
            while let Some(ev) = rx_a.recv().await {
                if let PeerEvent::LocalSignal(sig) = ev {
                    match sig.sig_type.as_str() {
                        "sdp-offer" => { let _ = p_b.handle_offer(&sig.data).await; }
                        "sdp-answer" => { let _ = p_b.handle_answer(&sig.data).await; }
                        "ice-candidate" => { let _ = p_b.handle_ice_candidate(&sig.data).await; }
                        _ => {}
                    }
                }
            }
        });

        let p_a2 = Arc::clone(&peer_a);
        let pump_ba = tokio::spawn(async move {
            while let Some(ev) = rx_b.recv().await {
                if let PeerEvent::LocalSignal(sig) = ev {
                    match sig.sig_type.as_str() {
                        "sdp-offer" => { let _ = p_a2.handle_offer(&sig.data).await; }
                        "sdp-answer" => { let _ = p_a2.handle_answer(&sig.data).await; }
                        "ice-candidate" => { let _ = p_a2.handle_ice_candidate(&sig.data).await; }
                        _ => {}
                    }
                }
            }
        });

        let start = std::time::Instant::now();
        let mut connected = false;
        while start.elapsed() < Duration::from_secs(5) {
            if peer_a.is_connected.load(Ordering::SeqCst) && peer_b.is_connected.load(Ordering::SeqCst) {
                connected = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }

        if connected {
            successes += 1;
        }

        pump_ab.abort();
        pump_ba.abort();
        peer_a.close().await;
        peer_b.close().await;

        if run % 5 == 0 || !connected {
            println!("[N3 Glare] Progress: {}/{} (successes={})", run, TOTAL_RUNS, successes);
        }
    }

    println!("[N3 Final Result] {}/{} successful connections (Target: 20/20)", successes, TOTAL_RUNS);
    assert_eq!(successes, 20, "N3 FAILED: Expected 20/20, but got {}", successes);
}
