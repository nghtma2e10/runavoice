use std::ptr;
use std::thread;
use std::time::Duration;
use runa_engine::ffi::*;

unsafe extern "C" fn dummy_event_cb(
    _user: *mut std::ffi::c_void,
    _kind: u32,
    _data: *const u8,
    _len: usize,
) {}

/// T1: FFI duman testi, tokio baglami OLMADAN (duz #[test], ayri std::thread).
/// runa_engine_create ve tum komutlar denenir; hicbiri RUNA_ERR_PANIC (3) dondurmemeli.
#[test]
fn test_t1_ffi_smoke_without_tokio_context() {
    let handle = thread::spawn(|| {
        let mut engine_ptr: *mut RunaEngine = ptr::null_mut();
        let config_json = b"{\"log_dir\":\"\",\"log_level\":\"info\"}\0";

        // Tokio baglami olmayan duz std::thread'den create cagiriyoruz:
        let res = unsafe {
            runa_engine_create(
                config_json.as_ptr(),
                config_json.len() - 1,
                Some(dummy_event_cb),
                ptr::null_mut(),
                &mut engine_ptr,
            )
        };

        assert_ne!(res, RUNA_ERR_PANIC, "T1 FAILED: runa_engine_create returned RUNA_ERR_PANIC (no reactor running)");
        assert_eq!(res, RUNA_OK, "T1: runa_engine_create failed with code {}", res);
        assert!(!engine_ptr.is_null());

        let commands = [
            r#"{"cmd":"ping","seq":1}"#,
            r#"{"cmd":"audio.start_room"}"#,
            r#"{"cmd":"audio.stop_room"}"#,
            r#"{"cmd":"peer.ensure","peer_key":"peer_test_01","display_name":"TestPeer"}"#,
            r#"{"cmd":"signal.remote","from_peer":"peer_test_01","signal_type":"offer","sdp":"v=0\r\no=test 1 1 IN IP4 127.0.0.1"}"#,
            r#"{"cmd":"dc.broadcast","channel":"runa-control","payload":"{}"}"#,
            r#"{"cmd":"share.start","source_id":"invalid_source","width":1920,"height":1080,"fps":30}"#,
            r#"{"cmd":"share.stop"}"#,
            r#"{"cmd":"watch.start","stream_id":"test_stream"}"#,
            r#"{"cmd":"watch.stop","stream_id":"test_stream"}"#,
            r#"{"cmd":"gs.tunnel.start","role":"client","bind_ip":"127.0.0.1"}"#,
            r#"{"cmd":"gs.tunnel.stop"}"#,
            r#"{"cmd":"gs.probe.run","target_peer":"peer_test_01"}"#,
        ];

        for cmd_str in commands {
            let cmd_bytes = cmd_str.as_bytes();
            let c_res = unsafe {
                runa_engine_command(engine_ptr, cmd_bytes.as_ptr(), cmd_bytes.len())
            };
            assert_ne!(c_res, RUNA_ERR_PANIC, "T1 FAILED: Command '{}' caused RUNA_ERR_PANIC!", cmd_str);
        }

        let queries = [
            r#"{"query":"audio.devices"}"#,
            r#"{"query":"gs.is_active"}"#,
            r#"{"query":"gs.stats"}"#,
        ];

        for q_str in queries {
            let q_bytes = q_str.as_bytes();
            let mut out_buf = [0u8; 4096];
            let mut out_len = 0usize;
            let q_res = unsafe {
                runa_engine_query(
                    engine_ptr,
                    q_bytes.as_ptr(),
                    q_bytes.len(),
                    out_buf.as_mut_ptr(),
                    out_buf.len(),
                    &mut out_len,
                )
            };
            assert_ne!(q_res, RUNA_ERR_PANIC, "T1 FAILED: Query '{}' caused RUNA_ERR_PANIC!", q_str);
        }

        unsafe {
            runa_engine_destroy(engine_ptr);
        }
    });

    handle.join().expect("T1 thread panicked!");
}

/// T2: Eşzamanlı komutlar: 4 iş parçacığından aynı anda, 200 tur. Kilitlenme, yarış, panic kontrolü.
#[test]
fn test_t2_concurrent_commands() {
    let mut engine_ptr: *mut RunaEngine = ptr::null_mut();
    let config_json = b"{\"log_dir\":\"\",\"log_level\":\"info\"}\0";

    let res = unsafe {
        runa_engine_create(
            config_json.as_ptr(),
            config_json.len() - 1,
            Some(dummy_event_cb),
            ptr::null_mut(),
            &mut engine_ptr,
        )
    };
    assert_eq!(res, RUNA_OK, "T2: create failed: {}", res);
    assert!(!engine_ptr.is_null());

    let raw_engine = engine_ptr as usize;
    let mut threads = Vec::new();

    for t_id in 0..4 {
        let th = thread::spawn(move || {
            let ptr = raw_engine as *mut RunaEngine;
            for i in 0..200 {
                let cmd_str = match (t_id + i) % 4 {
                    0 => r#"{"cmd":"ping","seq":1}"#,
                    1 => r#"{"cmd":"dc.broadcast","channel":"runa-control","payload":"{}"}"#,
                    2 => r#"{"cmd":"peer.ensure","peer_key":"peer_concurrent","display_name":"Concurrent"}"#,
                    _ => r#"{"cmd":"audio.apply_settings","settings":{"input_gain":100}}"#,
                };
                let bytes = cmd_str.as_bytes();
                let ret = unsafe { runa_engine_command(ptr, bytes.as_ptr(), bytes.len()) };
                assert_ne!(ret, RUNA_ERR_PANIC, "T2: Thread {} iter {} caused RUNA_ERR_PANIC", t_id, i);
            }
        });
        threads.push(th);
    }

    for th in threads {
        th.join().expect("T2 concurrent thread panicked");
    }

    unsafe {
        runa_engine_destroy(engine_ptr);
    }
}

/// T3: Oluştur/yok et döngüsü: 20 kez create -> kısa kullanım -> destroy, her biri ayrı iş parçacığından
#[test]
fn test_t3_create_destroy_lifecycle_loop() {
    for i in 0..20 {
        let th = thread::spawn(move || {
            let mut engine_ptr: *mut RunaEngine = ptr::null_mut();
            let config_json = b"{\"log_dir\":\"\",\"log_level\":\"info\"}\0";

            let res = unsafe {
                runa_engine_create(
                    config_json.as_ptr(),
                    config_json.len() - 1,
                    Some(dummy_event_cb),
                    ptr::null_mut(),
                    &mut engine_ptr,
                )
            };
            assert_eq!(res, RUNA_OK, "T3 iter {} create failed: {}", i, res);
            assert!(!engine_ptr.is_null());

            let ping = b"{\"cmd\":\"ping\"}";
            let c_res = unsafe { runa_engine_command(engine_ptr, ping.as_ptr(), ping.len()) };
            assert_eq!(c_res, RUNA_OK);

            thread::sleep(Duration::from_millis(10));

            unsafe {
                runa_engine_destroy(engine_ptr);
            }
        });
        th.join().expect("T3 thread panicked");
    }
}


/// T4: Panic hook testi: diag.panic komutu -> RUNA_ERR_PANIC, engine.log'da panic mesajı + konum,
/// runa_engine_last_error mesajı döndürür; motor ardından temiz kapanır.
#[test]
fn test_t4_panic_hook_and_last_error() {
    let handle = thread::spawn(|| {
        let mut engine_ptr: *mut RunaEngine = ptr::null_mut();
        let config_json = b"{\"log_dir\":\"\",\"log_level\":\"info\"}\0";

        let res = unsafe {
            runa_engine_create(
                config_json.as_ptr(),
                config_json.len() - 1,
                Some(dummy_event_cb),
                ptr::null_mut(),
                &mut engine_ptr,
            )
        };
        assert_eq!(res, RUNA_OK, "T4: create failed: {}", res);
        assert!(!engine_ptr.is_null());

        let panic_cmd = b"{\"cmd\":\"diag.panic\"}";
        let c_res = unsafe {
            runa_engine_command(engine_ptr, panic_cmd.as_ptr(), panic_cmd.len())
        };
        assert_eq!(c_res, RUNA_ERR_PANIC, "T4: diag.panic must return RUNA_ERR_PANIC");

        let mut err_buf = vec![0u8; 65536];
        let mut err_len = 0usize;
        let l_res = unsafe {
            runa_engine_last_error(
                engine_ptr,
                err_buf.as_mut_ptr(),
                err_buf.len(),
                &mut err_len,
            )
        };
        assert_eq!(l_res, RUNA_OK, "T4: runa_engine_last_error failed: {}", l_res);
        assert!(err_len > 0, "T4: err_len must be greater than 0");

        let err_str = std::str::from_utf8(&err_buf[..err_len]).expect("Valid UTF-8 in error message");
        println!("T4 last error: {}", err_str);
        assert!(
            err_str.contains("Intentional diagnostic panic via diag.panic command"),
            "T4: Expected diagnostic panic message, got: {}",
            err_str
        );

        unsafe {
            runa_engine_destroy(engine_ptr);
        }
    });

    handle.join().expect("T4 thread panicked unexpectedly");
}

/// T5: Oluşturma hatası mesajı: bozuk JSON config -> RUNA_ERR_GENERIC + runa_engine_last_create_error anlamlı metin
#[test]
fn test_t5_create_error_reporting() {
    let handle = thread::spawn(|| {
        let mut engine_ptr: *mut RunaEngine = ptr::null_mut();
        let bad_config = b"{ invalid json string }\0";

        let res = unsafe {
            runa_engine_create(
                bad_config.as_ptr(),
                bad_config.len() - 1,
                Some(dummy_event_cb),
                ptr::null_mut(),
                &mut engine_ptr,
            )
        };

        assert_eq!(res, RUNA_ERR_GENERIC, "T5: create with bad json must return RUNA_ERR_GENERIC");
        assert!(engine_ptr.is_null(), "T5: engine_ptr must remain null on create failure");

        let mut err_buf = [0u8; 1024];
        let mut err_len = 0usize;
        let c_res = unsafe {
            runa_engine_last_create_error(
                err_buf.as_mut_ptr(),
                err_buf.len(),
                &mut err_len,
            )
        };

        assert_eq!(c_res, RUNA_OK, "T5: runa_engine_last_create_error failed: {}", c_res);
        assert!(err_len > 0, "T5: err_len must be greater than 0");

        let err_str = std::str::from_utf8(&err_buf[..err_len]).expect("Valid UTF-8 in create error");
        println!("T5 last create error: {}", err_str);
        assert!(
            err_str.contains("Invalid JSON config"),
            "T5: Expected 'Invalid JSON config' in error, got: {}",
            err_str
        );
    });

    handle.join().expect("T5 thread panicked unexpectedly");
}
