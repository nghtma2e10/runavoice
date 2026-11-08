using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// WebRTC DataChannel (runa-gs-tcp) ve SRTP video kanalı (magic 0x5247) üzerinden
    /// port açmadan çalışan çift yönlü proxy tüneli (PDF Bölüm 7)
    /// </summary>
    public class GameStreamTunnel : IDisposable
    {
        public const ushort UdpMagic = 0x5247; // "RG" (PDF Bölüm 4 & 7.3)
        public const string TcpChannelLabel = "runa-gs-tcp";
        public const string ClientBindIp = "127.0.0.77";

        // İzinli portlar (PDF Bölüm 7.1)
        public static readonly int[] AllowedTcpPorts = { 47984, 47989, 48010 };
        public static readonly int[] AllowedUdpPorts = { 47998, 47999, 48000 };

        // TCP Çerçeve Tipleri
        public const byte TcpFrameOpen = 1;
        public const byte TcpFrameData = 2;
        public const byte TcpFrameClose = 3;
        public const int MaxTcpDataChunk = 16384; // En fazla 16 KB (PDF Bölüm 7.2)
        public const int MaxUdpPayload = 1150;    // MTU sınırı (PDF Bölüm 7.3)

        private readonly WebRtcService _webrtc;
        private readonly string _remotePeerKey;
        private readonly bool _isHost;
        private readonly CancellationTokenSource _cts = new();

        private RTCDataChannel? _tcpChannel;
        private uint _nextConnId = 1;
        private int _udpSeq = 0;

        // TCP Bağlantı eşlemeleri (connId -> TcpClient)
        private readonly ConcurrentDictionary<uint, TcpClientSession> _tcpSessions = new();

        // Host UDP soketleri (port -> UdpClient)
        private readonly ConcurrentDictionary<int, UdpClient> _hostUdpSockets = new();

        // İstemci tarafı: Moonlight'ın yerel soketleri (port -> UdpClient)
        private readonly ConcurrentDictionary<int, UdpClient> _clientUdpListeners = new();
        private readonly ConcurrentDictionary<int, IPEndPoint> _moonlightEndpoints = new();

        // TCP Dinleyicileri (yalnızca istemci tarafında 127.0.0.77 üzerinde)
        private readonly ConcurrentDictionary<int, TcpListener> _clientTcpListeners = new();

        public GameStreamTunnelStats Stats { get; } = new();

        private long _lastLossCalcTime = Environment.TickCount64;
        private long _lastPacketsLost = 0;
        private long _lastPacketsReceived = 0;
        private int _consecutiveHighLossSeconds = 0;

        public event Action? OnHighLossWarning; // 10 sn boyunca kayıp > %5 uyarısı (PDF Bölüm 8.5)

        public GameStreamTunnel(WebRtcService webrtc, string remotePeerKey, bool isHost, RTCDataChannel? tcpChannel = null)
        {
            _webrtc = webrtc;
            _remotePeerKey = remotePeerKey;
            _isHost = isHost;
            _tcpChannel = tcpChannel;

            if (_tcpChannel != null)
            {
                AttachTcpChannelHandlers(_tcpChannel);
            }

            _webrtc.OnGameStreamUdpPacketReceived += HandleIncomingUdpPacket;
        }

        public void SetTcpChannel(RTCDataChannel channel)
        {
            _tcpChannel = channel;
            AttachTcpChannelHandlers(_tcpChannel);
        }

        public void Start()
        {
            if (_isHost)
            {
                // Host tarafı: Sunshine'a iletim yapacak yerel UDP soketlerini aç
                foreach (int port in AllowedUdpPorts)
                {
                    try
                    {
                        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                        _hostUdpSockets[port] = udp;
                        _ = RunHostUdpReceiveLoopAsync(udp, port, _cts.Token);
                    }
                    catch { }
                }
            }
            else
            {
                // Oyuncu tarafı: 127.0.0.77 üzerinde TCP ve UDP portlarını dinle (PDF Bölüm 7.2 & 7.3)
                var bindIp = IPAddress.Parse(ClientBindIp);

                foreach (int port in AllowedTcpPorts)
                {
                    try
                    {
                        var listener = new TcpListener(bindIp, port);
                        listener.Start();
                        _clientTcpListeners[port] = listener;
                        _ = RunClientTcpAcceptLoopAsync(listener, port, _cts.Token);
                    }
                    catch { }
                }

                foreach (int port in AllowedUdpPorts)
                {
                    try
                    {
                        var udp = new UdpClient(new IPEndPoint(bindIp, port));
                        _clientUdpListeners[port] = udp;
                        _ = RunClientUdpReceiveLoopAsync(udp, port, _cts.Token);
                    }
                    catch { }
                }
            }

            // Periyodik kayıp oranı ve istatistik güncelleme döngüsü
            _ = RunStatsWatchdogAsync(_cts.Token);
        }

        // =====================================================================
        // TCP KATMANI (PDF Bölüm 7.2: runa-gs-tcp)
        // [1 bayt tip: OPEN/DATA/CLOSE][4 bayt connId][2 bayt port][veri]
        // =====================================================================

        private void AttachTcpChannelHandlers(RTCDataChannel dc)
        {
            dc.onmessage += (chan, proto, rawBytes) =>
            {
                if (rawBytes == null || rawBytes.Length < 7) return;

                byte type = rawBytes[0];
                uint connId = BitConverter.ToUInt32(rawBytes, 1);
                ushort port = BitConverter.ToUInt16(rawBytes, 5);

                if (_isHost)
                {
                    HandleHostTcpFrame(type, connId, port, rawBytes, 7, rawBytes.Length - 7);
                }
                else
                {
                    HandleClientTcpFrame(type, connId, port, rawBytes, 7, rawBytes.Length - 7);
                }
            };
        }

        private async void HandleHostTcpFrame(byte type, uint connId, ushort port, byte[] buffer, int offset, int count)
        {
            if (type == TcpFrameOpen)
            {
                // Yalnızca izinli portlara ve yalnızca 127.0.0.1'e bağlanır (PDF Bölüm 7.4)
                if (!Array.Exists(AllowedTcpPorts, p => p == port)) return;

                try
                {
                    var tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync(IPAddress.Loopback, port);

                    var session = new TcpClientSession(connId, port, tcpClient);
                    _tcpSessions[connId] = session;

                    _ = RunTcpSocketToChannelLoopAsync(session, _cts.Token);
                }
                catch
                {
                    SendTcpFrame(TcpFrameClose, connId, port, Array.Empty<byte>());
                }
            }
            else if (type == TcpFrameData)
            {
                if (_tcpSessions.TryGetValue(connId, out var session))
                {
                    try
                    {
                        await session.Client.GetStream().WriteAsync(buffer.AsMemory(offset, count), _cts.Token);
                        Stats.BytesReceived += count;
                    }
                    catch
                    {
                        CloseTcpSession(connId);
                    }
                }
            }
            else if (type == TcpFrameClose)
            {
                CloseTcpSession(connId);
            }
        }

        private async void HandleClientTcpFrame(byte type, uint connId, ushort port, byte[] buffer, int offset, int count)
        {
            if (type == TcpFrameData)
            {
                if (_tcpSessions.TryGetValue(connId, out var session))
                {
                    try
                    {
                        await session.Client.GetStream().WriteAsync(buffer.AsMemory(offset, count), _cts.Token);
                        Stats.BytesReceived += count;
                    }
                    catch
                    {
                        CloseTcpSession(connId);
                    }
                }
            }
            else if (type == TcpFrameClose)
            {
                CloseTcpSession(connId);
            }
        }

        private async Task RunClientTcpAcceptLoopAsync(TcpListener listener, int port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    uint connId = Interlocked.Increment(ref _nextConnId);

                    var session = new TcpClientSession(connId, port, client);
                    _tcpSessions[connId] = session;

                    // Host'a OPEN çerçevesi gönder
                    SendTcpFrame(TcpFrameOpen, connId, (ushort)port, Array.Empty<byte>());

                    _ = RunTcpSocketToChannelLoopAsync(session, ct);
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task RunTcpSocketToChannelLoopAsync(TcpClientSession session, CancellationToken ct)
        {
            byte[] buf = new byte[MaxTcpDataChunk];
            var stream = session.Client.GetStream();

            try
            {
                while (!ct.IsCancellationRequested && session.Client.Connected)
                {
                    // Geri basınç: DataChannel tampon miktarı eşiği aşarsa bekle (PDF Bölüm 7.2)
                    if (_tcpChannel != null && _tcpChannel.bufferedAmount > 65536)
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    int read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                    if (read <= 0) break;

                    SendTcpFrame(TcpFrameData, session.ConnId, (ushort)session.Port, buf.AsSpan(0, read).ToArray());
                    Stats.BytesSent += read;
                }
            }
            catch { }
            finally
            {
                CloseTcpSession(session.ConnId);
            }
        }

        private void SendTcpFrame(byte type, uint connId, ushort port, byte[] payload)
        {
            if (_tcpChannel == null || _tcpChannel.readyState != RTCDataChannelState.open) return;

            byte[] frame = new byte[7 + payload.Length];
            frame[0] = type;
            BitConverter.TryWriteBytes(frame.AsSpan(1, 4), connId);
            BitConverter.TryWriteBytes(frame.AsSpan(5, 2), port);
            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, frame, 7, payload.Length);
            }

            try
            {
                _tcpChannel.send(frame);
            }
            catch { }
        }

        private void CloseTcpSession(uint connId)
        {
            if (_tcpSessions.TryRemove(connId, out var session))
            {
                SendTcpFrame(TcpFrameClose, connId, (ushort)session.Port, Array.Empty<byte>());
                try { session.Client.Close(); } catch { }
                try { session.Client.Dispose(); } catch { }
            }
        }

        // =====================================================================
        // UDP KATMANI (PDF Bölüm 7.3: magic 0x5247)
        // [2 bayt magic 0x5247][2 bayt port][4 bayt flowId][2 bayt sıra][veri <= 1150]
        // =====================================================================

        private async Task RunClientUdpReceiveLoopAsync(UdpClient listener, int port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await listener.ReceiveAsync(ct);
                    _moonlightEndpoints[port] = result.RemoteEndPoint;

                    // Moonlight video paketleri MTU'ya uygundur; aşan datagram düşürülür (PDF 7.3)
                    if (result.Buffer.Length > MaxUdpPayload)
                    {
                        Stats.PacketsLost++;
                        continue;
                    }

                    ushort seq = (ushort)Interlocked.Increment(ref _udpSeq);
                    byte[] packet = BuildUdpPacket((ushort)port, 1, seq, result.Buffer);

                    if (_webrtc.SendGameStreamUdpRaw(_remotePeerKey, packet))
                    {
                        Stats.PacketsSent++;
                        Stats.BytesSent += packet.Length;
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task RunHostUdpReceiveLoopAsync(UdpClient socket, int port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveAsync(ct);
                    if (result.Buffer.Length > MaxUdpPayload)
                    {
                        Stats.PacketsLost++;
                        continue;
                    }

                    ushort seq = (ushort)Interlocked.Increment(ref _udpSeq);
                    byte[] packet = BuildUdpPacket((ushort)port, 1, seq, result.Buffer);

                    if (_webrtc.SendGameStreamUdpRaw(_remotePeerKey, packet))
                    {
                        Stats.PacketsSent++;
                        Stats.BytesSent += packet.Length;
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private void HandleIncomingUdpPacket(string fromPeerKey, byte[] packet)
        {
            if (fromPeerKey != _remotePeerKey || packet == null || packet.Length < 10) return;

            ushort magic = BitConverter.ToUInt16(packet, 0);
            if (magic != UdpMagic) return;

            ushort port = BitConverter.ToUInt16(packet, 2);
            uint flowId = BitConverter.ToUInt32(packet, 4);
            ushort seq = BitConverter.ToUInt16(packet, 8);
            int dataLen = packet.Length - 10;

            Stats.PacketsReceived++;
            Stats.BytesReceived += packet.Length;

            if (_isHost)
            {
                // Host tarafı: Sunshine 127.0.0.1:port'a gönder (PDF Bölüm 7.3)
                if (_hostUdpSockets.TryGetValue(port, out var socket))
                {
                    try
                    {
                        socket.Send(packet, dataLen, new IPEndPoint(IPAddress.Loopback, port));
                    }
                    catch { }
                }
            }
            else
            {
                // Oyuncu tarafı: Moonlight'ın yerel uç noktasına gönder (PDF Bölüm 7.3)
                if (_clientUdpListeners.TryGetValue(port, out var listener) &&
                    _moonlightEndpoints.TryGetValue(port, out var ep))
                {
                    try
                    {
                        byte[] payload = new byte[dataLen];
                        Buffer.BlockCopy(packet, 10, payload, 0, dataLen);
                        listener.Send(payload, payload.Length, ep);
                    }
                    catch { }
                }
            }
        }

        private static byte[] BuildUdpPacket(ushort port, uint flowId, ushort seq, byte[] payload)
        {
            byte[] packet = new byte[10 + payload.Length];
            BitConverter.TryWriteBytes(packet.AsSpan(0, 2), UdpMagic);
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), port);
            BitConverter.TryWriteBytes(packet.AsSpan(4, 4), flowId);
            BitConverter.TryWriteBytes(packet.AsSpan(8, 2), seq);
            Buffer.BlockCopy(payload, 0, packet, 10, payload.Length);
            return packet;
        }

        private async Task RunStatsWatchdogAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);

                long now = Environment.TickCount64;
                long rxDiff = Stats.PacketsReceived - _lastPacketsReceived;
                long lostDiff = Stats.PacketsLost - _lastPacketsLost;

                _lastPacketsReceived = Stats.PacketsReceived;
                _lastPacketsLost = Stats.PacketsLost;

                long total = rxDiff + lostDiff;
                double lossRate = total > 0 ? (lostDiff * 100.0) / total : 0.0;
                Stats.LossRatePercent = lossRate;
                Stats.RttMs = _webrtc.GetPeerRtt(_remotePeerKey);

                if (lossRate > 5.0)
                {
                    _consecutiveHighLossSeconds++;
                    if (_consecutiveHighLossSeconds >= 10)
                    {
                        _consecutiveHighLossSeconds = 0;
                        OnHighLossWarning?.Invoke();
                    }
                }
                else
                {
                    _consecutiveHighLossSeconds = 0;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();

            _webrtc.OnGameStreamUdpPacketReceived -= HandleIncomingUdpPacket;

            foreach (var kvp in _tcpSessions)
            {
                try { kvp.Value.Client.Close(); kvp.Value.Client.Dispose(); } catch { }
            }
            _tcpSessions.Clear();

            foreach (var listener in _clientTcpListeners.Values)
            {
                try { listener.Stop(); } catch { }
            }
            _clientTcpListeners.Clear();

            foreach (var udp in _clientUdpListeners.Values)
            {
                try { udp.Close(); udp.Dispose(); } catch { }
            }
            _clientUdpListeners.Clear();

            foreach (var udp in _hostUdpSockets.Values)
            {
                try { udp.Close(); udp.Dispose(); } catch { }
            }
            _hostUdpSockets.Clear();
        }

        private sealed class TcpClientSession
        {
            public uint ConnId { get; }
            public int Port { get; }
            public TcpClient Client { get; }

            public TcpClientSession(uint connId, int port, TcpClient client)
            {
                ConnId = connId;
                Port = port;
                Client = client;
            }
        }
    }
}
