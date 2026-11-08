using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Eşleşme sonrası UDP üzerinden kademeli bant genişliği ölçümü (PDF Bölüm 8)
    /// 1 -> 2 -> 4 -> 6 -> 8 Mbps (her adım 500 ms)
    /// </summary>
    public class BandwidthProbe
    {
        private static readonly int[] ProbeRatesKbps = { 1000, 2000, 4000, 6000, 8000 };
        private const int StepDurationMs = 500;
        private const int PacketSize = 1150;

        private readonly WebRtcService _webrtc;
        private readonly string _remotePeerKey;
        private readonly string _sessionId;

        public event Action<int, double>? OnStepCompleted; // kbps, lossRate

        public BandwidthProbe(WebRtcService webrtc, string remotePeerKey, string sessionId)
        {
            _webrtc = webrtc;
            _remotePeerKey = remotePeerKey;
            _sessionId = sessionId;
        }

        /// <summary>
        /// Host tarafında 5 adımlı bant genişliği ölçümünü koşturur ve kapasiteyi döner (PDF Bölüm 8.1)
        /// </summary>
        public async Task<int> RunHostProbeAsync(CancellationToken ct = default)
        {
            int capacity = ProbeRatesKbps[0];
            byte[] dummyPayload = new byte[PacketSize - 10];
            Random.Shared.NextBytes(dummyPayload);

            for (int phase = 0; phase < ProbeRatesKbps.Length; phase++)
            {
                if (ct.IsCancellationRequested) break;

                int targetKbps = ProbeRatesKbps[phase];
                int targetBytes = (targetKbps * 1000 / 8) * StepDurationMs / 1000;
                int totalPackets = Math.Max(1, targetBytes / PacketSize);

                // Oyuncuya adım başlangıcını bildir
                _webrtc.SendGameStreamTo(_remotePeerKey, new GsProbeMessage
                {
                    SessionId = _sessionId,
                    Phase = phase,
                    Kbps = targetKbps
                });

                int packetsSent = 0;
                var sw = Stopwatch.StartNew();
                double intervalMs = (double)StepDurationMs / totalPackets;

                while (sw.ElapsedMilliseconds < StepDurationMs && packetsSent < totalPackets && !ct.IsCancellationRequested)
                {
                    byte[] packet = new byte[PacketSize];
                    BitConverter.TryWriteBytes(packet.AsSpan(0, 2), GameStreamTunnel.UdpMagic);
                    BitConverter.TryWriteBytes(packet.AsSpan(2, 2), (ushort)47998); // Probe portu
                    BitConverter.TryWriteBytes(packet.AsSpan(4, 4), 0xFFFFFFFF); // Probe flowId
                    BitConverter.TryWriteBytes(packet.AsSpan(8, 2), (ushort)packetsSent);
                    Buffer.BlockCopy(dummyPayload, 0, packet, 10, dummyPayload.Length);

                    _webrtc.SendGameStreamUdpRaw(_remotePeerKey, packet);
                    packetsSent++;

                    double targetElapsed = packetsSent * intervalMs;
                    double sleepMs = targetElapsed - sw.ElapsedMilliseconds;
                    if (sleepMs > 1.0)
                    {
                        await Task.Delay((int)sleepMs, ct);
                    }
                }

                // Kısa RTT beklemesi ve bir sonraki adıma geçiş
                await Task.Delay(60, ct);
                capacity = targetKbps;
                OnStepCompleted?.Invoke(targetKbps, 0.0);
            }

            return capacity;
        }

        /// <summary>
        /// Ölçülen kapasite C'ye ve codec yeteneklerine göre en uygun profili önerir (PDF Bölüm 8.2 & 8.3)
        /// </summary>
        public static GameStreamQualityProfile CalculateRecommendedProfile(int capacityKbps, List<string> commonCodecs, int userMaxBitrateKbps = 0)
        {
            // B = min(0.8 * C - 64, kullanıcıÜstSınırı); alt sınır 1500 kbps (PDF Bölüm 8.2)
            int recommendedBitrate = (int)(0.8 * capacityKbps - 64);
            if (userMaxBitrateKbps > 0)
            {
                recommendedBitrate = Math.Min(recommendedBitrate, userMaxBitrateKbps);
            }
            recommendedBitrate = Math.Max(1500, recommendedBitrate);

            // Codec seçimi: AV1 > HEVC > H264
            string selectedCodec = "H264";
            if (commonCodecs.Contains("AV1")) selectedCodec = "AV1";
            else if (commonCodecs.Contains("HEVC")) selectedCodec = "HEVC";

            // Çözünürlük ve FPS seçimi (PDF Bölüm 8.3)
            string res = "1080p";
            int width = 1920;
            int height = 1080;
            int fps = 60;

            if (recommendedBitrate >= 8000)
            {
                res = "1080p";
                width = 1920;
                height = 1080;
                fps = 60;
            }
            else if (recommendedBitrate >= 3500)
            {
                res = "1080p";
                width = 1920;
                height = 1080;
                fps = (selectedCodec == "H264") ? 30 : 60;
            }
            else if (recommendedBitrate >= 2000)
            {
                res = "720p";
                width = 1280;
                height = 720;
                fps = 60;
            }
            else
            {
                res = "720p";
                width = 1280;
                height = 720;
                fps = 30;
            }

            return new GameStreamQualityProfile
            {
                Resolution = res,
                Width = width,
                Height = height,
                Fps = fps,
                Codec = selectedCodec,
                BitrateKbps = recommendedBitrate
            };
        }
    }
}
