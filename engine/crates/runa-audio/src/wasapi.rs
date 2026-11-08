use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use parking_lot::Mutex;
use tracing::{debug, error, info, warn};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, PROPERTYKEY, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::*;
use windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc;
use windows::Win32::System::Com::*;
use windows::Win32::System::Threading::{
    AvRevertMmThreadCharacteristics, AvSetMmThreadCharacteristicsW, CreateEventW, WaitForSingleObject,
};
use crate::mixer::AudioMasterMixer;
use crate::pipeline::AudioInputPipeline;

pub struct AudioDeviceInfo {
    pub id: String,
    pub name: String,
    pub is_default: bool,
}

pub struct WasapiDeviceManager;

impl WasapiDeviceManager {
    /// Enumerate active capture or render devices
    pub fn list_devices(data_flow: EDataFlow) -> Vec<AudioDeviceInfo> {
        let mut result = Vec::new();

        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);

            let enumerator: Result<IMMDeviceEnumerator, _> =
                CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL);

            let enumerator = match enumerator {
                Ok(e) => e,
                Err(err) => {
                    warn!("Failed to create MMDeviceEnumerator: {:?}", err);
                    return result;
                }
            };

            let default_id = enumerator
                .GetDefaultAudioEndpoint(data_flow, eConsole)
                .ok()
                .and_then(|dev| dev.GetId().ok())
                .map(|p| p.to_string().unwrap_or_default())
                .unwrap_or_default();

            if let Ok(collection) = enumerator.EnumAudioEndpoints(data_flow, DEVICE_STATE_ACTIVE) {
                if let Ok(count) = collection.GetCount() {
                    for i in 0..count {
                        if let Ok(device) = collection.Item(i) {
                            let id = device
                                .GetId()
                                .ok()
                                .map(|p| p.to_string().unwrap_or_default())
                                .unwrap_or_default();
                            let is_def = id == default_id;

                            let mut name = id.clone();
                            if let Ok(prop_store) = device.OpenPropertyStore(STGM_READ) {
                                let key = PROPERTYKEY {
                                    fmtid: windows::core::GUID::from_u128(0xa45c254e_df1c_4efd_8020_67d146a850e0),
                                    pid: 14,
                                };
                                if let Ok(val) = prop_store.GetValue(&key) {
                                    if let Ok(pwstr) = PropVariantToStringAlloc(&val) {
                                        let friendly = pwstr.to_string().unwrap_or_default();
                                        if !friendly.is_empty() {
                                            name = friendly;
                                        }
                                    }
                                }
                            }

                            result.push(AudioDeviceInfo {
                                id,
                                name,
                                is_default: is_def,
                            });
                        }
                    }
                }
            }
        }

        result
    }

    pub fn list_input_devices() -> Vec<AudioDeviceInfo> {
        Self::list_devices(eCapture)
    }

    pub fn list_output_devices() -> Vec<AudioDeviceInfo> {
        Self::list_devices(eRender)
    }

    pub fn get_device(data_flow: EDataFlow, wanted_id: &str) -> Option<IMMDevice> {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            let enumerator: IMMDeviceEnumerator =
                CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;

            if !wanted_id.is_empty() {
                let wide_id: Vec<u16> = wanted_id.encode_utf16().chain(std::iter::once(0)).collect();
                if let Ok(dev) = enumerator.GetDevice(PCWSTR(wide_id.as_ptr())) {
                    return Some(dev);
                }
            }

            enumerator.GetDefaultAudioEndpoint(data_flow, eConsole).ok()
        }
    }
}

/// MMCSS (Multimedia Class Scheduler Service) Pro Audio RAII guard
pub struct MmcssGuard {
    task_handle: HANDLE,
}

impl MmcssGuard {
    pub fn join_pro_audio() -> Option<Self> {
        unsafe {
            let task_name: Vec<u16> = "Pro Audio\0".encode_utf16().collect();
            let mut task_index = 0u32;
            let handle = AvSetMmThreadCharacteristicsW(
                PCWSTR(task_name.as_ptr()),
                &mut task_index,
            );
            if let Ok(h) = handle {
                if !h.is_invalid() {
                    debug!("MMCSS Pro Audio thread priority registered successfully");
                    return Some(Self { task_handle: h });
                }
            }
        }
        None
    }
}

impl Drop for MmcssGuard {
    fn drop(&mut self) {
        unsafe {
            if !self.task_handle.is_invalid() {
                let _ = AvRevertMmThreadCharacteristics(self.task_handle);
            }
        }
    }
}

/// WASAPI Capture Stream
pub struct WasapiCaptureStream {
    is_running: Arc<AtomicBool>,
    thread_handle: Option<std::thread::JoinHandle<()>>,
}

impl WasapiCaptureStream {
    pub fn start(
        device_id: String,
        pipeline: Arc<Mutex<AudioInputPipeline>>,
        outgoing_tx: tokio::sync::mpsc::UnboundedSender<Vec<i16>>,
        mixer: Option<Arc<AudioMasterMixer>>,
    ) -> Result<Self, Box<dyn std::error::Error + Send + Sync>> {
        let is_running = Arc::new(AtomicBool::new(true));
        let running_clone = Arc::clone(&is_running);
        let mixer_clone = mixer.clone();

        let thread = std::thread::Builder::new()
            .name("runa-wasapi-cap".to_string())
            .spawn(move || {
                unsafe {
                    let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
                    let _mmcss = MmcssGuard::join_pro_audio();

                    let device = match WasapiDeviceManager::get_device(eCapture, &device_id) {
                        Some(d) => d,
                        None => {
                            warn!("No capture device found for id: {}", device_id);
                            return;
                        }
                    };

                    let audio_client: IAudioClient = match device.Activate(CLSCTX_ALL, None) {
                        Ok(ac) => ac,
                        Err(e) => {
                            error!("Failed to activate IAudioClient for capture: {:?}", e);
                            return;
                        }
                    };

                    let mix_format_ptr = match audio_client.GetMixFormat() {
                        Ok(f) => f,
                        Err(e) => {
                            error!("Failed to get capture mix format: {:?}", e);
                            return;
                        }
                    };

                    let mix_format = &*mix_format_ptr;
                    let sample_rate = mix_format.nSamplesPerSec;
                    let channels = mix_format.nChannels as usize;
                    let bits_per_sample = mix_format.wBitsPerSample;

                    info!(
                        "Capture format: {} Hz, {} ch, {} bits",
                        sample_rate, channels, bits_per_sample
                    );

                    let h_event = match CreateEventW(None, false, false, None) {
                        Ok(ev) => ev,
                        Err(e) => {
                            error!("Failed to create capture event: {:?}", e);
                            return;
                        }
                    };

                    // 10 ms buffer (100,000 in 100ns units)
                    let buffer_duration = 100_000i64;
                    let init_res = audio_client.Initialize(
                        AUDCLNT_SHAREMODE_SHARED,
                        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                        buffer_duration,
                        0,
                        mix_format_ptr,
                        None,
                    );

                    if let Err(e) = init_res {
                        error!("Failed to initialize capture audio client: {:?}", e);
                        let _ = CloseHandle(h_event);
                        return;
                    }

                    if let Err(e) = audio_client.SetEventHandle(h_event) {
                        error!("Failed to set capture event handle: {:?}", e);
                        let _ = CloseHandle(h_event);
                        return;
                    }

                    let capture_client: IAudioCaptureClient = match audio_client.GetService() {
                        Ok(cc) => cc,
                        Err(e) => {
                            error!("Failed to get IAudioCaptureClient: {:?}", e);
                            let _ = CloseHandle(h_event);
                            return;
                        }
                    };

                    if let Err(e) = audio_client.Start() {
                        error!("Failed to start capture audio client: {:?}", e);
                        let _ = CloseHandle(h_event);
                        return;
                    }

                    info!("WASAPI capture started successfully");

                    let mut float_conv_buf = Vec::<f32>::with_capacity(960 * channels);

                    while running_clone.load(Ordering::Relaxed) {
                        let wait_res = WaitForSingleObject(h_event, 500);
                        if wait_res != WAIT_OBJECT_0 {
                            continue; // Timeout or error
                        }

                        let mut p_data: *mut u8 = std::ptr::null_mut();
                        let mut num_frames_read = 0u32;
                        let mut flags = 0u32;

                        while capture_client
                            .GetBuffer(
                                &mut p_data,
                                &mut num_frames_read,
                                &mut flags,
                                None,
                                None,
                            )
                            .is_ok()
                        {
                            if num_frames_read == 0 || p_data.is_null() {
                                let _ = capture_client.ReleaseBuffer(num_frames_read);
                                break;
                            }

                            float_conv_buf.clear();

                            // Ölçüm: bu geri çağrıda gelen ses süresi (mikrofon tamponu gecikmesi)
                            if let Some(ref mx) = mixer_clone {
                                mx.report_capture_chunk_ms(num_frames_read as f64 * 1000.0 / sample_rate.max(1) as f64);
                            }

                            // Convert raw buffer to 48kHz mono float
                            if (flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32) != 0 {
                                float_conv_buf.resize(num_frames_read as usize, 0.0);
                            } else if bits_per_sample == 32 {
                                let slice = std::slice::from_raw_parts(
                                    p_data as *const f32,
                                    (num_frames_read as usize) * channels,
                                );
                                for frame in slice.chunks_exact(channels) {
                                    // Average channels to mono
                                    let sum: f32 = frame.iter().copied().sum();
                                    float_conv_buf.push(sum / channels as f32);
                                }
                            } else if bits_per_sample == 16 {
                                let slice = std::slice::from_raw_parts(
                                    p_data as *const i16,
                                    (num_frames_read as usize) * channels,
                                );
                                for frame in slice.chunks_exact(channels) {
                                    let sum: f32 = frame.iter().map(|&s| s as f32 / 32768.0).sum();
                                    float_conv_buf.push(sum / channels as f32);
                                }
                            }

                            // Feed into pipeline
                            let mut pipe = pipeline.lock();
                            let (ready_frames, monitor, _speaking) = pipe.process_input_samples(
                                &mut float_conv_buf,
                                &[],
                                true,
                            );
                            drop(pipe);

                            for frame in ready_frames {
                                let _ = outgoing_tx.send(frame);
                            }

                            if let Some(m_samples) = monitor {
                                if let Some(ref mx) = mixer_clone {
                                    mx.play_local_aux(m_samples, false);
                                }
                            }

                            let _ = capture_client.ReleaseBuffer(num_frames_read);
                        }
                    }

                    let _ = audio_client.Stop();
                    let _ = CloseHandle(h_event);
                    info!("WASAPI capture stopped");
                }
            })?;

        Ok(Self {
            is_running,
            thread_handle: Some(thread),
        })
    }

    pub fn stop(&mut self) {
        self.is_running.store(false, Ordering::SeqCst);
        if let Some(handle) = self.thread_handle.take() {
            let _ = handle.join();
        }
    }
}

impl Drop for WasapiCaptureStream {
    fn drop(&mut self) {
        self.stop();
    }
}

/// WASAPI Render Stream
pub struct WasapiRenderStream {
    is_running: Arc<AtomicBool>,
    thread_handle: Option<std::thread::JoinHandle<()>>,
}

impl WasapiRenderStream {
    pub fn start(
        device_id: String,
        mixer: Arc<AudioMasterMixer>,
    ) -> Result<Self, Box<dyn std::error::Error + Send + Sync>> {
        let is_running = Arc::new(AtomicBool::new(true));
        let running_clone = Arc::clone(&is_running);

        let thread = std::thread::Builder::new()
            .name("runa-wasapi-render".to_string())
            .spawn(move || {
                unsafe {
                    let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
                    let _mmcss = MmcssGuard::join_pro_audio();

                    let device = match WasapiDeviceManager::get_device(eRender, &device_id) {
                        Some(d) => d,
                        None => {
                            warn!("No render device found for id: {}", device_id);
                            return;
                        }
                    };

                    let audio_client: IAudioClient = match device.Activate(CLSCTX_ALL, None) {
                        Ok(ac) => ac,
                        Err(e) => {
                            error!("Failed to activate IAudioClient for render: {:?}", e);
                            return;
                        }
                    };

                    let mix_format_ptr = match audio_client.GetMixFormat() {
                        Ok(f) => f,
                        Err(e) => {
                            error!("Failed to get render mix format: {:?}", e);
                            return;
                        }
                    };

                    let h_event = match CreateEventW(None, false, false, None) {
                        Ok(ev) => ev,
                        Err(e) => {
                            error!("Failed to create render event: {:?}", e);
                            return;
                        }
                    };

                    let buffer_duration = 100_000i64; // 10 ms
                    let init_res = audio_client.Initialize(
                        AUDCLNT_SHAREMODE_SHARED,
                        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                        buffer_duration,
                        0,
                        mix_format_ptr,
                        None,
                    );

                    if let Err(e) = init_res {
                        error!("Failed to initialize render audio client: {:?}", e);
                        let _ = CloseHandle(h_event);
                        return;
                    }

                    if let Err(e) = audio_client.SetEventHandle(h_event) {
                        error!("Failed to set render event handle: {:?}", e);
                        let _ = CloseHandle(h_event);
                        return;
                    }

                    let buffer_frame_count = audio_client.GetBufferSize().unwrap_or(480);
                    // Ölçüm: cihaz kuyruğunda bekleyen en fazla ses (hoparlör tamponu)
                    let render_rate = (*mix_format_ptr).nSamplesPerSec.max(1);
                    mixer.report_playback_buffer_ms(buffer_frame_count as f64 * 1000.0 / render_rate as f64);

                    let render_client: IAudioRenderClient = match audio_client.GetService() {
                        Ok(rc) => rc,
                        Err(e) => {
                            error!("Failed to get IAudioRenderClient: {:?}", e);
                            let _ = CloseHandle(h_event);
                            return;
                        }
                    };

                    if let Err(e) = audio_client.Start() {
                        error!("Failed to start render audio client: {:?}", e);
                        let _ = CloseHandle(h_event);
                        return;
                    }

                    info!("WASAPI render started successfully (buffer: {} frames)", buffer_frame_count);

                    let mut stereo_render_buf = vec![0.0_f32; (buffer_frame_count as usize) * 2];

                    while running_clone.load(Ordering::Relaxed) {
                        let wait_res = WaitForSingleObject(h_event, 500);
                        if wait_res != WAIT_OBJECT_0 {
                            continue;
                        }

                        let padding = match audio_client.GetCurrentPadding() {
                            Ok(p) => p,
                            Err(_) => continue,
                        };

                        let available = buffer_frame_count.saturating_sub(padding);
                        if available == 0 {
                            continue;
                        }

                        let p_buf = match render_client.GetBuffer(available) {
                            Ok(p) => p,
                            Err(_) => continue,
                        };

                        let needed_floats = (available as usize) * 2;
                        if stereo_render_buf.len() < needed_floats {
                            stereo_render_buf.resize(needed_floats, 0.0);
                        }

                        mixer.render_stereo_samples(&mut stereo_render_buf[..needed_floats]);

                        // Write to WASAPI render buffer
                        std::ptr::copy_nonoverlapping(
                            stereo_render_buf.as_ptr() as *const u8,
                            p_buf,
                            needed_floats * std::mem::size_of::<f32>(),
                        );

                        let _ = render_client.ReleaseBuffer(available, 0);
                    }

                    let _ = audio_client.Stop();
                    let _ = CloseHandle(h_event);
                    info!("WASAPI render stopped");
                }
            })?;

        Ok(Self {
            is_running,
            thread_handle: Some(thread),
        })
    }

    pub fn stop(&mut self) {
        self.is_running.store(false, Ordering::SeqCst);
        if let Some(handle) = self.thread_handle.take() {
            let _ = handle.join();
        }
    }
}

impl Drop for WasapiRenderStream {
    fn drop(&mut self) {
        self.stop();
    }
}
