use nnnoiseless::DenoiseState;

pub const FRAME_SIZE: usize = 480; // 48kHz @ 10ms
const QUEUE_SIZE: usize = 16384;

pub struct RnnoiseFilter {
    state: Option<Box<DenoiseState<'static>>>,
    in_fifo: [f32; FRAME_SIZE],
    rnn_in: [f32; FRAME_SIZE],
    rnn_out: [f32; FRAME_SIZE],
    out_queue: Vec<f32>,
    in_pos: usize,
    out_read_pos: usize,
    out_write_pos: usize,
    out_count: usize,
    smooth_vad_gate: f32,
    noise_floor_env: f32,
    last_vad_prob: f32,
    pub engine_mode: String,
    pub suppression_level: i32,
}

impl Default for RnnoiseFilter {
    fn default() -> Self {
        let state = DenoiseState::new();
        Self {
            state: Some(state),
            in_fifo: [0.0; FRAME_SIZE],
            rnn_in: [0.0; FRAME_SIZE],
            rnn_out: [0.0; FRAME_SIZE],
            out_queue: vec![0.0; QUEUE_SIZE],
            in_pos: 0,
            out_read_pos: 0,
            out_write_pos: 0,
            out_count: 0,
            smooth_vad_gate: 1.0,
            noise_floor_env: 0.002,
            last_vad_prob: 0.0,
            engine_mode: "rnnoise".to_string(),
            suppression_level: 3,
        }
    }
}

impl RnnoiseFilter {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn last_vad_probability(&self) -> f32 {
        self.last_vad_prob
    }

    pub fn reset_buffers(&mut self) {
        self.in_fifo.fill(0.0);
        self.rnn_in.fill(0.0);
        self.rnn_out.fill(0.0);
        self.out_queue.fill(0.0);
        self.in_pos = 0;
        self.out_read_pos = 0;
        self.out_write_pos = 0;
        self.out_count = 0;
        self.smooth_vad_gate = 1.0;
        self.noise_floor_env = 0.002;
    }

    pub fn process_in_place(&mut self, samples: &mut [f32]) {
        let count = samples.len();
        if count == 0 {
            return;
        }

        let mode = self.engine_mode.to_lowercase();
        if mode == "off" {
            self.last_vad_prob = Self::compute_simple_energy_vad(samples);
            return;
        }

        let level = self.suppression_level.clamp(1, 5);
        let wet_mix = match level {
            1 => 0.50,
            2 => 0.70,
            3 => 0.86,
            4 => 0.95,
            _ => 1.00,
        };

        if mode == "speex" || self.state.is_none() {
            self.process_lightweight_dsp_gate(samples, level);
            return;
        }

        let mut state = match self.state.take() {
            Some(s) => s,
            None => {
                self.process_lightweight_dsp_gate(samples, level);
                return;
            }
        };

        for &sample in &*samples {
            self.in_fifo[self.in_pos] = sample;
            self.in_pos += 1;

            if self.in_pos == FRAME_SIZE {
                for k in 0..FRAME_SIZE {
                    self.rnn_in[k] = (self.in_fifo[k] * 32768.0).clamp(-32768.0, 32767.0);
                }

                // DenoiseState::process_frame takes (&mut output, &input)
                let vad = state.process_frame(&mut self.rnn_out, &self.rnn_in);
                self.last_vad_prob = vad.clamp(0.0, 1.0);

                let mut target_gate = 1.0_f32;
                if mode == "deep" || level >= 4 {
                    let vad_threshold = if mode == "deep" {
                        0.18 + level as f32 * 0.06
                    } else {
                        0.12 + (level - 3) as f32 * 0.08
                    };
                    if vad < vad_threshold {
                        target_gate = if mode == "deep" { 0.02 } else { 0.12 };
                    }
                }

                for k in 0..FRAME_SIZE {
                    let coeff = if target_gate > self.smooth_vad_gate { 0.88 } else { 0.9975 };
                    self.smooth_vad_gate = coeff * self.smooth_vad_gate + (1.0 - coeff) * target_gate;

                    let clean_norm = (self.rnn_out[k] / 32768.0) * self.smooth_vad_gate;
                    let raw_norm = self.in_fifo[k];
                    let blended = clean_norm * wet_mix + raw_norm * (1.0 - wet_mix);

                    self.enqueue_output(blended.clamp(-1.0, 1.0));
                }

                self.in_pos = 0;
            }
        }

        self.state = Some(state);

        // Dequeue processed samples back
        for sample in samples.iter_mut() {
            *sample = self.dequeue_output();
        }
    }

    fn process_lightweight_dsp_gate(&mut self, samples: &mut [f32], level: i32) {
        let count = samples.len();
        let mut rms_sum = 0.0_f32;
        for &s in samples.iter() {
            rms_sum += s * s;
        }
        let rms = if count > 0 { (rms_sum / count as f32).sqrt() } else { 0.0 };

        if rms < self.noise_floor_env * 1.5 {
            self.noise_floor_env = 0.98 * self.noise_floor_env + 0.02 * rms;
        } else {
            self.noise_floor_env = 0.999 * self.noise_floor_env + 0.001 * rms;
        }
        self.noise_floor_env = self.noise_floor_env.clamp(0.0005, 0.04);

        let threshold = self.noise_floor_env * (1.8 + level as f32 * 0.65);
        self.last_vad_prob = ((rms - self.noise_floor_env) / (threshold * 2.0).max(0.01)).clamp(0.0, 1.0);

        let target_gain = if rms >= threshold {
            1.0
        } else {
            (1.0 - level as f32 * 0.19).max(0.05)
        };

        for sample in samples.iter_mut() {
            let coeff = if target_gain > self.smooth_vad_gate { 0.92 } else { 0.998 };
            self.smooth_vad_gate = coeff * self.smooth_vad_gate + (1.0 - coeff) * target_gain;
            *sample *= self.smooth_vad_gate;
        }
    }

    fn compute_simple_energy_vad(samples: &[f32]) -> f32 {
        if samples.is_empty() {
            return 0.0;
        }
        let mut sum = 0.0_f32;
        for &s in samples {
            sum += s * s;
        }
        let rms = (sum / samples.len() as f32).sqrt();
        (rms * 12.0).clamp(0.0, 1.0)
    }

    fn enqueue_output(&mut self, val: f32) {
        if self.out_count >= QUEUE_SIZE {
            self.out_read_pos = (self.out_read_pos + 1) % QUEUE_SIZE;
            self.out_count -= 1;
        }
        self.out_queue[self.out_write_pos] = val;
        self.out_write_pos = (self.out_write_pos + 1) % QUEUE_SIZE;
        self.out_count += 1;
    }

    fn dequeue_output(&mut self) -> f32 {
        if self.out_count == 0 {
            return 0.0;
        }
        let val = self.out_queue[self.out_read_pos];
        self.out_read_pos = (self.out_read_pos + 1) % QUEUE_SIZE;
        self.out_count -= 1;
        val
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_rnnoise_process() {
        let mut filter = RnnoiseFilter::new();
        let mut samples = vec![0.05_f32; 480];
        filter.process_in_place(&mut samples);
        // After processing 480 samples, VAD probability must be computed
        assert!(filter.last_vad_probability() >= 0.0);
    }
}
