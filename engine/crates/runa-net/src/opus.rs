use std::num::NonZeroU32;
use opus1::{Application, Bitrate, Channels, InbandFec, OpusDecoder, OpusEncoder, SampleRate};

pub struct OpusEncoderSession {
    encoder: OpusEncoder,
    frame_size: usize,
    configured_bitrate_kbps: u32,
}

impl OpusEncoderSession {
    pub fn new(sample_rate: u32, channels: u8, bitrate_kbps: u32, frame_ms: f32) -> Result<Self, String> {
        let sr = match sample_rate {
            8000 => SampleRate::Hz8000,
            12000 => SampleRate::Hz12000,
            16000 => SampleRate::Hz16000,
            24000 => SampleRate::Hz24000,
            48000 => SampleRate::Hz48000,
            _ => return Err(format!("Unsupported Opus sample rate: {}", sample_rate)),
        };

        let ch = match channels {
            1 => Channels::Mono,
            2 => Channels::Stereo,
            _ => return Err(format!("Unsupported Opus channel count: {}", channels)),
        };

        let app = if bitrate_kbps >= 128 {
            Application::Audio
        } else {
            Application::Voip
        };

        let mut encoder = OpusEncoder::new(sr, ch, app)
            .map_err(|e| format!("Failed to create Opus encoder: {:?}", e))?;

        let clamped_kbps = bitrate_kbps.clamp(24, 256);
        let bps = clamped_kbps * 1000;
        if let Some(nz) = NonZeroU32::new(bps) {
            encoder.set_bitrate(Bitrate::BitsPerSecond(nz));
        }

        let _ = encoder.set_complexity(7);
        encoder.set_inband_fec(InbandFec::On);
        let _ = encoder.set_packet_loss_perc(8);
        encoder.set_dtx(false);

        let frame_size = (sample_rate as f32 * (frame_ms / 1000.0)).round() as usize;

        Ok(Self {
            encoder,
            frame_size,
            configured_bitrate_kbps: clamped_kbps,
        })
    }

    pub fn set_bitrate(&mut self, bitrate_kbps: u32) {
        let clamped_kbps = bitrate_kbps.clamp(24, 256);
        if clamped_kbps != self.configured_bitrate_kbps {
            self.configured_bitrate_kbps = clamped_kbps;
            if let Some(nz) = NonZeroU32::new(clamped_kbps * 1000) {
                self.encoder.set_bitrate(Bitrate::BitsPerSecond(nz));
            }
        }
    }

    pub fn set_packet_loss_perc(&mut self, loss_pct: u8) {
        let _ = self.encoder.set_packet_loss_perc(loss_pct.min(100));
        // Section 11-O3: Opus in-band FEC is dynamic based on measured packet loss
        if loss_pct > 1 {
            self.encoder.set_inband_fec(InbandFec::On);
        } else {
            self.encoder.set_inband_fec(InbandFec::Off);
        }
    }

    pub fn encode(&mut self, pcm: &[i16], out_buf: &mut [u8]) -> Result<usize, String> {
        self.encoder.encode_i16(pcm, self.frame_size, out_buf)
            .map_err(|e| format!("Opus encode error: {:?}", e))
    }

    pub fn frame_size(&self) -> usize {
        self.frame_size
    }

    /// Opus algoritmik gecikmesi (kodlayıcının tamponladığı örnekler), ms — 48 kHz.
    pub fn lookahead_ms(&self) -> f64 {
        self.encoder.lookahead() as f64 / 48.0
    }

    /// Çerçeve (paketleme) süresi, ms — 48 kHz.
    pub fn frame_ms(&self) -> f64 {
        self.frame_size as f64 / 48.0
    }
}

pub struct OpusDecoderSession {
    decoder: OpusDecoder,
    frame_size: usize,
}

impl OpusDecoderSession {
    pub fn new(sample_rate: u32, channels: u8, frame_ms: f32) -> Result<Self, String> {
        let sr = match sample_rate {
            8000 => SampleRate::Hz8000,
            12000 => SampleRate::Hz12000,
            16000 => SampleRate::Hz16000,
            24000 => SampleRate::Hz24000,
            48000 => SampleRate::Hz48000,
            _ => return Err(format!("Unsupported Opus sample rate: {}", sample_rate)),
        };

        let ch = match channels {
            1 => Channels::Mono,
            2 => Channels::Stereo,
            _ => return Err(format!("Unsupported Opus channel count: {}", channels)),
        };

        let decoder = OpusDecoder::new(sr, ch)
            .map_err(|e| format!("Failed to create Opus decoder: {:?}", e))?;

        let frame_size = (sample_rate as f32 * (frame_ms / 1000.0)).round() as usize;

        Ok(Self { decoder, frame_size })
    }

    pub fn decode(&mut self, packet: Option<&[u8]>, out_pcm: &mut [f32], fec: bool) -> Result<usize, String> {
        match packet {
            Some(data) if !data.is_empty() => {
                if fec {
                    self.decoder.decode_fec(data, self.frame_size, out_pcm)
                        .map_err(|e| format!("Opus decode_fec error: {:?}", e))
                } else {
                    self.decoder.decode(data, self.frame_size, out_pcm)
                        .map_err(|e| format!("Opus decode error: {:?}", e))
                }
            }
            _ => {
                // Packet Loss Concealment (PLC)
                self.decoder.decode(&[], self.frame_size, out_pcm)
                    .map_err(|e| format!("Opus PLC decode error: {:?}", e))
            }
        }
    }

    pub fn frame_size(&self) -> usize {
        self.frame_size
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_opus_encode_decode_roundtrip() {
        let mut encoder = OpusEncoderSession::new(48000, 1, 64, 10.0).expect("encoder create failed");
        let mut decoder = OpusDecoderSession::new(48000, 1, 10.0).expect("decoder create failed");

        assert_eq!(encoder.frame_size(), 480);
        assert_eq!(decoder.frame_size(), 480);

        // Warm up and test steady-state transmission across 5 frames
        let mut ratio = 0.0f32;
        for frame_idx in 0..5 {
            let mut pcm_in = [0i16; 480];
            for i in 0..480 {
                let sample_num = frame_idx * 480 + i;
                let t = sample_num as f32 / 48000.0;
                pcm_in[i] = (f32::sin(2.0 * std::f32::consts::PI * 1000.0 * t) * 16384.0) as i16;
            }

            let mut packet = [0u8; 1500];
            let enc_bytes = encoder.encode(&pcm_in, &mut packet).expect("encode failed");
            assert!(enc_bytes > 0 && enc_bytes < 500, "Encoded bytes {} out of expected range", enc_bytes);

            let mut pcm_out = [0.0f32; 480];
            let dec_samples = decoder.decode(Some(&packet[..enc_bytes]), &mut pcm_out, false).expect("decode failed");
            assert_eq!(dec_samples, 480);

            if frame_idx >= 2 {
                let in_energy: f32 = pcm_in.iter().map(|&s| (s as f32 / 32768.0).powi(2)).sum();
                let out_energy: f32 = pcm_out.iter().map(|&s| s.powi(2)).sum();
                ratio = out_energy / in_energy;
            }
        }
        assert!((ratio - 1.0).abs() < 0.25, "Steady-state energy ratio {} outside acceptable Opus tolerance", ratio);
    }

    #[test]
    fn test_opus_packet_loss_concealment() {
        let mut encoder = OpusEncoderSession::new(48000, 1, 64, 10.0).unwrap();
        let mut decoder = OpusDecoderSession::new(48000, 1, 10.0).unwrap();

        let mut pcm_in = [0i16; 480];
        for i in 0..480 {
            let t = i as f32 / 48000.0;
            pcm_in[i] = (f32::sin(2.0 * std::f32::consts::PI * 440.0 * t) * 16384.0) as i16;
        }

        let mut packet = [0u8; 1500];
        let enc_bytes = encoder.encode(&pcm_in, &mut packet).unwrap();
        let mut pcm_out = [0.0f32; 480];
        let _ = decoder.decode(Some(&packet[..enc_bytes]), &mut pcm_out, false).unwrap();

        // Now simulate a lost packet (None) -> PLC
        let mut plc_out = [0.0f32; 480];
        let plc_samples = decoder.decode(None, &mut plc_out, false).expect("PLC decode failed");
        assert_eq!(plc_samples, 480);
        // PLC output should have continuous decaying waveform, not pure silence or NaN
        let plc_energy: f32 = plc_out.iter().map(|&s| s.powi(2)).sum();
        assert!(plc_energy > 0.001, "PLC output should not be zero energy");
    }
}
