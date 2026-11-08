use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264, MIME_TYPE_OPUS, MIME_TYPE_VP8};
use webrtc::rtp_transceiver::rtp_codec::{
    RTCRtpCodecCapability, RTCRtpCodecParameters, RTPCodecType,
};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use std::sync::Arc;

pub const OPUS_MIME: &str = MIME_TYPE_OPUS;
pub const OPUS_CLOCK_RATE: u32 = 48000;
pub const OPUS_CHANNELS: u16 = 2; // WebRTC standard requires 2 channels in SDP declaration
pub const OPUS_FMTP: &str = "minptime=10;useinbandfec=1";

pub const H264_MIME: &str = MIME_TYPE_H264;
pub const H264_CLOCK_RATE: u32 = 90000;
pub const H264_FMTP: &str = "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";

pub const VP8_MIME: &str = MIME_TYPE_VP8;
pub const VP8_CLOCK_RATE: u32 = 90000;

/// Return standardized audio codec capability matching MediaEngine registration
pub fn audio_codec_capability() -> RTCRtpCodecCapability {
    RTCRtpCodecCapability {
        mime_type: OPUS_MIME.to_string(),
        clock_rate: OPUS_CLOCK_RATE,
        channels: OPUS_CHANNELS,
        sdp_fmtp_line: OPUS_FMTP.to_string(),
        rtcp_feedback: vec![],
    }
}

/// Return standardized video codec capability matching MediaEngine registration
pub fn video_codec_capability() -> RTCRtpCodecCapability {
    RTCRtpCodecCapability {
        mime_type: H264_MIME.to_string(),
        clock_rate: H264_CLOCK_RATE,
        channels: 0,
        sdp_fmtp_line: H264_FMTP.to_string(),
        rtcp_feedback: vec![],
    }
}

/// Create a new local audio track with standardized Opus capability
pub fn create_audio_track(peer_key: &str) -> Arc<TrackLocalStaticSample> {
    Arc::new(TrackLocalStaticSample::new(
        audio_codec_capability(),
        format!("{}_audio", peer_key),
        format!("{}_stream", peer_key),
    ))
}

/// Create a new local video track with standardized H.264 capability
pub fn create_video_track(peer_key: &str) -> Arc<TrackLocalStaticSample> {
    Arc::new(TrackLocalStaticSample::new(
        video_codec_capability(),
        format!("{}_video", peer_key),
        format!("{}_stream", peer_key),
    ))
}

/// Build MediaEngine with unified audio (Opus 48k/2) and video (H.264 profiles + VP8)
pub fn configure_media_engine(m: &mut MediaEngine) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    m.register_default_codecs()?;

    // Explicitly ensure standard Opus 48000/2 capability is registered
    m.register_codec(
        RTCRtpCodecParameters {
            capability: audio_codec_capability(),
            payload_type: 111,
            ..Default::default()
        },
        RTPCodecType::Audio,
    )?;

    // Explicitly ensure H.264 profile 42e01f packetization-mode=1 is registered
    m.register_codec(
        RTCRtpCodecParameters {
            capability: video_codec_capability(),
            payload_type: 125,
            ..Default::default()
        },
        RTPCodecType::Video,
    )?;

    // Ensure VP8 is registered for compatibility with C# legacy
    m.register_codec(
        RTCRtpCodecParameters {
            capability: RTCRtpCodecCapability {
                mime_type: VP8_MIME.to_string(),
                clock_rate: VP8_CLOCK_RATE,
                channels: 0,
                sdp_fmtp_line: String::new(),
                rtcp_feedback: vec![],
            },
            payload_type: 96,
            ..Default::default()
        },
        RTPCodecType::Video,
    )?;

    Ok(())
}
