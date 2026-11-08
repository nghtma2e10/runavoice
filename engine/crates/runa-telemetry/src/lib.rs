use std::path::Path;
use tracing_appender::non_blocking::WorkerGuard;
use tracing_subscriber::{layer::SubscriberExt, util::SubscriberInitExt, EnvFilter, Layer};

pub struct TelemetryHandle {
    _guard: Option<WorkerGuard>,
}

pub fn init_telemetry(log_dir: Option<&str>, log_level: &str) -> TelemetryHandle {
    let filter_str = if log_level.contains("webrtc_ice") {
        log_level.to_string()
    } else {
        format!("{},webrtc_ice=error,webrtc_sctp=error", log_level)
    };

    let filter = EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| EnvFilter::new(&filter_str));

    let (file_layer, guard) = if let Some(dir) = log_dir {
        let p = Path::new(dir);
        let _ = std::fs::create_dir_all(p);
        let file_appender = tracing_appender::rolling::daily(p, "engine.log");
        let (non_blocking, guard) = tracing_appender::non_blocking(file_appender);
        let layer = tracing_subscriber::fmt::layer()
            .with_ansi(false)
            .with_writer(non_blocking)
            .boxed();
        (Some(layer), Some(guard))
    } else {
        (None, None)
    };

    let stdout_layer = tracing_subscriber::fmt::layer()
        .with_ansi(true)
        .boxed();

    let subscriber = tracing_subscriber::registry()
        .with(filter)
        .with(stdout_layer);

    if let Some(fl) = file_layer {
        let _ = subscriber.with(fl).try_init();
    } else {
        let _ = subscriber.try_init();
    }

    tracing::info!("Runa Engine Telemetry initialized (level: {})", log_level);

    TelemetryHandle { _guard: guard }
}
