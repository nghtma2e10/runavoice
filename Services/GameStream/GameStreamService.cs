using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RunaVoice_WinUI.Models;
using SIPSorcery.Net;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Oyun Akışı oturum durum makinesi ve GS_ protokol yöneticisi (PDF Bölüm 3, 6, 7, 8, 11)
    /// </summary>
    public class GameStreamService : IDisposable
    {
        private readonly WebRtcService _webrtc;
        private readonly StorageService _storage;
        private readonly ScreenCaptureService _screenCapture;
        private readonly AudioEngine _audio;

        private IStreamHost _streamHost;
        private IStreamClient _streamClient;
        public bool IsMockMode { get; private set; } = false;

        private readonly ConcurrentDictionary<string, GameStreamPeerPresence> _activePeers = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastInviteTimes = new();
        private readonly object _sessionLock = new();

        public GameStreamCapabilities LocalCapabilities { get; private set; } = new();
        public bool IsModalOpen { get; private set; } = false;
        public bool IsPrivacyCurtainEnabled { get; set; } = true; // Her oturum varsayılan açık başlar (PDF Bölüm 9.2)

        public GameStreamPhase Phase { get; private set; } = GameStreamPhase.Idle;
        public GameStreamRole Role { get; private set; } = GameStreamRole.None;
        public string? ActiveSessionId { get; private set; }
        public string? ActiveRemotePeerKey { get; private set; }
        public string? ActiveRemotePeerName { get; private set; }
        public string? ActiveGameName { get; private set; }
        public string? PlayingWithPeerName { get; private set; }

        public GameStreamTunnel? ActiveTunnel { get; private set; }
        public GameStreamQualityProfile? ActiveQualityProfile { get; private set; }
        private PrivacyCurtain? _privacyCurtain;
        private InjectedKeyGuard? _keyGuard;

        public string? ActiveGameExePath { get; private set; }
        public List<string> ActiveExtraProcesses { get; private set; } = new();

        private int _originalOpusBitrate = 0;
        private bool _wasScreenSharing = false;

        public IStreamHost StreamHost => _streamHost;
        public IStreamClient StreamClient => _streamClient;

        // Olaylar
        public event Action? PresenceUpdated;
        public event Action<GameStreamPhase, string>? PhaseChanged;
        public event Action<GsInviteMessage, string, string>? InviteReceived;
        public event Action<string>? InviteCancelled;
        public event Action<GsResponseMessage>? ResponseReceived;
        public event Action<string>? SessionEnded;
        public event Action<bool>? CurtainChanged;
        public event Action? HighLossWarning;
        public event Action<string, bool, string?>? PeerGamingStatusChanged;
        public event Action<string, string>? RemoteStateReceived;

        public GameStreamService(WebRtcService webrtc, StorageService storage, ScreenCaptureService screenCapture, AudioEngine audio)
        {
            _webrtc = webrtc;
            _storage = storage;
            _screenCapture = screenCapture;
            _audio = audio;

            string[] args = Environment.GetCommandLineArgs();
            bool isGsA = args.Any(a => a.Contains("gsA", StringComparison.OrdinalIgnoreCase) || a.Equals("--mock-host", StringComparison.OrdinalIgnoreCase));
            bool isGsB = args.Any(a => a.Contains("gsB", StringComparison.OrdinalIgnoreCase) || a.Equals("--mock-client", StringComparison.OrdinalIgnoreCase));
            bool mockAll = args.Any(a => a.Equals("--mock-gamestream", StringComparison.OrdinalIgnoreCase));

            if (isGsA || mockAll)
            {
                IsMockMode = true;
                _streamHost = new MockStreamHost { MockCanHost = true, MockCanPlay = true };
                _streamClient = new MockStreamClient { MockCanPlay = true };
            }
            else if (isGsB)
            {
                IsMockMode = true;
                _streamHost = new MockStreamHost { MockCanHost = false, MockCanPlay = true };
                _streamClient = new MockStreamClient { MockCanPlay = true };
            }
            else
            {
                _streamHost = new SunshineManager();
                _streamClient = new MoonlightManager();
            }

            _webrtc.OnGameStreamMessage += HandleGameStreamMessage;
            _webrtc.OnControlChannelOpened += HandleControlChannelOpened;
            _webrtc.OnGameStreamTcpChannelOpened += HandleIncomingTcpChannel;

            _streamClient.ProcessExited += (exitCode, reason) =>
            {
                if (Phase == GameStreamPhase.Playing)
                {
                    _ = CloseActiveSessionAsync(reason ?? "user");
                }
            };
        }

        public async Task InitializeAsync()
        {
            await RefreshCapabilitiesAsync();

            try
            {
                await _streamHost.CleanupStaleClientsAsync(_storage.Settings.GameStreamTrustedPlayers);
                await _streamHost.RestoreResolutionAsync();
            }
            catch { }
        }

        public async Task RefreshCapabilitiesAsync()
        {
            try
            {
                var hostCaps = await _streamHost.DetectAsync();
                var clientCaps = await _streamClient.DetectAsync();

                var mergedCodecs = new HashSet<string>(hostCaps.Codecs.Concat(clientCaps.Codecs));
                if (mergedCodecs.Count == 0)
                {
                    mergedCodecs.Add("HEVC");
                    mergedCodecs.Add("H264");
                }

                LocalCapabilities = new GameStreamCapabilities
                {
                    CanHost = hostCaps.CanHost,
                    CanPlay = clientCaps.CanPlay,
                    HostReason = hostCaps.HostReason,
                    PlayReason = clientCaps.PlayReason,
                    SunshineVersion = hostCaps.SunshineVersion,
                    MoonlightVersion = clientCaps.MoonlightVersion,
                    Codecs = mergedCodecs.ToList()
                };

                if (IsModalOpen)
                {
                    BroadcastPresence();
                }
            }
            catch (Exception)
            {
                LocalCapabilities = new GameStreamCapabilities
                {
                    CanHost = false,
                    CanPlay = false,
                    HostReason = "Yetenekler algılanırken hata oluştu",
                    PlayReason = "Yetenekler algılanırken hata oluştu"
                };
            }
        }

        public void SetModalOpen(bool open)
        {
            IsModalOpen = open;
            if (open)
            {
                IsPrivacyCurtainEnabled = true;
                _ = RefreshCapabilitiesAsync();
                BroadcastPresence();
            }
            else
            {
                BroadcastPresence();
                _activePeers.Clear();
                PresenceUpdated?.Invoke();
            }
        }

        public IReadOnlyList<GameStreamPeerPresence> GetActivePeers()
        {
            return _activePeers.Values.Where(p => p.IsOpen).ToList();
        }

        public void ResetRoomPresence()
        {
            _activePeers.Clear();
            IsModalOpen = false;
            PresenceUpdated?.Invoke();
        }

        public void RemovePeer(string peerKey)
        {
            if (_activePeers.TryRemove(peerKey, out _))
            {
                PresenceUpdated?.Invoke();
                PeerGamingStatusChanged?.Invoke(peerKey, false, null);
            }

            if (ActiveRemotePeerKey == peerKey && Phase != GameStreamPhase.Idle)
            {
                // Biri odadan ayrılır / bağlantı kopar: Karşı taraf 10 sn bekler, sonra left_room / timeout ile temizlik (PDF Bölüm 11)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(10000);
                        if (ActiveRemotePeerKey == peerKey && Phase != GameStreamPhase.Idle)
                        {
                            await CloseActiveSessionAsync("left_room");
                        }
                    }
                    catch { }
                });
            }
        }

        private void HandleControlChannelOpened(string peerKey)
        {
            if (IsModalOpen)
            {
                SendPresenceTo(peerKey);
            }
        }

        public void BroadcastPresence()
        {
            var msg = new GsPresenceMessage
            {
                Open = IsModalOpen,
                CanHost = LocalCapabilities.CanHost,
                CanPlay = LocalCapabilities.CanPlay,
                Busy = Phase != GameStreamPhase.Idle,
                Codecs = LocalCapabilities.Codecs,
                Version = 1,
                PlayingWith = Phase != GameStreamPhase.Idle ? PlayingWithPeerName : null
            };

            _webrtc.BroadcastGameStream(msg);
        }

        public void SendPresenceTo(string peerKey)
        {
            var msg = new GsPresenceMessage
            {
                Open = IsModalOpen,
                CanHost = LocalCapabilities.CanHost,
                CanPlay = LocalCapabilities.CanPlay,
                Busy = Phase != GameStreamPhase.Idle,
                Codecs = LocalCapabilities.Codecs,
                Version = 1,
                PlayingWith = Phase != GameStreamPhase.Idle ? PlayingWithPeerName : null
            };

            _webrtc.SendGameStreamTo(peerKey, msg);
        }

        // =====================================================================
        // DAVET PROTOKOLÜ (PDF Bölüm 6, 11)
        // =====================================================================

        public bool CanSendInviteTo(string targetPeerKey, out string? reason)
        {
            if (Phase != GameStreamPhase.Idle)
            {
                reason = "Zaten aktif veya bekleyen bir Oyun Akışı oturumunuz var.";
                return false;
            }

            if (!LocalCapabilities.CanHost)
            {
                reason = "Bu bilgisayar host olamıyor (Sunshine veya donanım kodlayıcı eksik).";
                return false;
            }

            if (_lastInviteTimes.TryGetValue(targetPeerKey, out var lastTime))
            {
                double elapsed = (DateTime.UtcNow - lastTime).TotalSeconds;
                if (elapsed < 10.0)
                {
                    reason = $"Aynı oyuncuya tekrar davet göndermek için {Math.Ceiling(10.0 - elapsed)} sn bekleyin.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        public bool SendInvite(string targetPeerKey, string targetPeerName, string gameName, string exePath, List<string>? extraProcesses, string resolution, int fps, string codec, int bitrateKbps, out string? error)
        {
            if (!CanSendInviteTo(targetPeerKey, out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(gameName) || gameName.Length > 80)
            {
                error = "Oyun adı geçersiz (1-80 karakter olmalıdır).";
                return false;
            }

            bitrateKbps = Math.Clamp(bitrateKbps, 500, 50000);
            if (resolution != "1080p" && resolution != "720p") resolution = "1080p";
            if (fps != 30 && fps != 60) fps = 60;

            byte[] sidBytes = new byte[16];
            RandomNumberGenerator.Fill(sidBytes);
            string sid = Convert.ToHexString(sidBytes).ToLowerInvariant();

            lock (_sessionLock)
            {
                Phase = GameStreamPhase.Inviting;
                Role = GameStreamRole.Host;
                ActiveSessionId = sid;
                ActiveRemotePeerKey = targetPeerKey;
                ActiveRemotePeerName = targetPeerName;
                ActiveGameName = gameName;
                ActiveGameExePath = exePath;
                ActiveExtraProcesses = extraProcesses ?? new();
                PlayingWithPeerName = targetPeerName;
            }

            _lastInviteTimes[targetPeerKey] = DateTime.UtcNow;

            var invite = new GsInviteMessage
            {
                SessionId = sid,
                Game = gameName,
                Resolution = resolution,
                Fps = fps,
                Codec = codec,
                BitrateKbps = bitrateKbps,
                Privacy = IsPrivacyCurtainEnabled,
                ExpireSeconds = 60
            };

            _webrtc.SendGameStreamTo(targetPeerKey, invite);
            BroadcastPresence();
            PhaseChanged?.Invoke(GameStreamPhase.Inviting, $"{targetPeerName} oyuncusuna davet gönderildi, yanıt bekleniyor...");
            return true;
        }

        public void CancelInvite()
        {
            string? sid;
            string? remoteKey;
            lock (_sessionLock)
            {
                if (Phase != GameStreamPhase.Inviting) return;
                sid = ActiveSessionId;
                remoteKey = ActiveRemotePeerKey;
                Phase = GameStreamPhase.Idle;
                Role = GameStreamRole.None;
                ActiveSessionId = null;
                ActiveRemotePeerKey = null;
                ActiveRemotePeerName = null;
                ActiveGameName = null;
                ActiveGameExePath = null;
                ActiveExtraProcesses.Clear();
                PlayingWithPeerName = null;
            }

            if (!string.IsNullOrEmpty(remoteKey) && !string.IsNullOrEmpty(sid))
            {
                _webrtc.SendGameStreamTo(remoteKey, new GsInviteCancelMessage { SessionId = sid });
            }

            BroadcastPresence();
            PhaseChanged?.Invoke(GameStreamPhase.Idle, "Boşta");
        }

        public void AcceptInvite(string sessionId, string hostPeerKey, string hostPeerName)
        {
            lock (_sessionLock)
            {
                Phase = GameStreamPhase.Pairing;
                Role = GameStreamRole.Player;
                ActiveSessionId = sessionId;
                ActiveRemotePeerKey = hostPeerKey;
                ActiveRemotePeerName = hostPeerName;
                PlayingWithPeerName = hostPeerName;
            }

            _webrtc.SendGameStreamTo(hostPeerKey, new GsResponseMessage
            {
                SessionId = sessionId,
                Accept = true,
                Reason = "accepted"
            });

            BroadcastPresence();
            PhaseChanged?.Invoke(GameStreamPhase.Pairing, "Eşleştiriliyor (PIN oluşturuluyor)...");

            // Oyuncu tarafı: 4 haneli PIN üret ve host'a ilet (PDF Bölüm 6 & 11)
            string pin = Random.Shared.Next(1000, 9999).ToString();
            _webrtc.SendGameStreamTo(hostPeerKey, new GsPairPinMessage
            {
                SessionId = sessionId,
                Pin = pin
            });
        }

        public void RejectInvite(string sessionId, string hostPeerKey, string reason = "declined")
        {
            lock (_sessionLock)
            {
                if (ActiveSessionId == sessionId)
                {
                    Phase = GameStreamPhase.Idle;
                    Role = GameStreamRole.None;
                    ActiveSessionId = null;
                    ActiveRemotePeerKey = null;
                    ActiveRemotePeerName = null;
                    PlayingWithPeerName = null;
                }
            }

            _webrtc.SendGameStreamTo(hostPeerKey, new GsResponseMessage
            {
                SessionId = sessionId,
                Accept = false,
                Reason = reason
            });

            BroadcastPresence();
            PhaseChanged?.Invoke(GameStreamPhase.Idle, "Boşta");
        }

        // =====================================================================
        // TÜNEL VE OTURUM BAŞLATMA ZİNCİRİ (PDF Bölüm 6, 7, 8, 11)
        // =====================================================================

        private void HandleIncomingTcpChannel(string fromPeerKey, RTCDataChannel channel)
        {
            if (fromPeerKey != ActiveRemotePeerKey) return;

            if (ActiveTunnel != null)
            {
                ActiveTunnel.SetTcpChannel(channel);
            }
            else
            {
                ActiveTunnel = new GameStreamTunnel(_webrtc, fromPeerKey, isHost: Role == GameStreamRole.Host, channel);
                ActiveTunnel.OnHighLossWarning += () => HighLossWarning?.Invoke();
                ActiveTunnel.Start();
            }

            if (Role == GameStreamRole.Player)
            {
                SetPhase(GameStreamPhase.Probing, "Hız ölçülüyor...");
            }
        }

        private async Task HostStartTunnelAndProbeAsync()
        {
            if (ActiveRemotePeerKey == null || ActiveSessionId == null) return;

            SetPhase(GameStreamPhase.Tunneling, "Tünel hazırlanıyor...");

            // Host mock ise test echo sunucularını başlatır (PDF Bölüm 14)
            if (_streamHost is MockStreamHost mockHost)
            {
                mockHost.StartEchoServers();
            }

            // Host runa-gs-tcp kanalını açar (PDF Bölüm 7.2)
            var tcpChannel = await _webrtc.CreateGameStreamDataChannelAsync(ActiveRemotePeerKey);

            ActiveTunnel = new GameStreamTunnel(_webrtc, ActiveRemotePeerKey, isHost: true, tcpChannel);
            ActiveTunnel.OnHighLossWarning += () => HighLossWarning?.Invoke();
            ActiveTunnel.Start();

            SetPhase(GameStreamPhase.Probing, "Hız ölçülüyor...");

            // Bant genişliği ölçümü (PDF Bölüm 8.1)
            var probe = new BandwidthProbe(_webrtc, ActiveRemotePeerKey, ActiveSessionId);
            int capacity = await probe.RunHostProbeAsync();

            var profile = BandwidthProbe.CalculateRecommendedProfile(
                capacity,
                LocalCapabilities.Codecs,
                _storage.Settings.GameStreamMaxBitrateKbps);

            ActiveQualityProfile = profile;

            // Host hazırlıklarını yap (Bölüm 8.6, 9)
            SetPhase(GameStreamPhase.Launching, "Oyun başlatılıyor...");

            // RUNA'nın kendi kullanımını kısma (PDF Bölüm 8.6 & 4)
            _originalOpusBitrate = _storage.Settings.OpusBitrateKbps;
            _storage.Settings.OpusBitrateKbps = Math.Min(48, _originalOpusBitrate);
            _webrtc.RefreshCodecSettings();

            if (_screenCapture.IsSharing)
            {
                _wasScreenSharing = true;
                _screenCapture.StopCapture();
            }

            // Çözünürlük ayarı
            await _streamHost.SetResolutionAsync(profile.Width, profile.Height, profile.Fps);

            // Gizlilik perdesi ve Tuş Koruması (PDF Bölüm 9.1)
            if (IsPrivacyCurtainEnabled)
            {
                _privacyCurtain = new PrivacyCurtain();
                _privacyCurtain.SetAllowedGame(ActiveGameExePath ?? "", ActiveExtraProcesses);
                _privacyCurtain.OnCurtainVisibilityChanged += (visible) =>
                {
                    if (ActiveRemotePeerKey != null && ActiveSessionId != null)
                    {
                        _webrtc.SendGameStreamTo(ActiveRemotePeerKey, new GsCurtainMessage { SessionId = ActiveSessionId, On = visible });
                    }
                    CurtainChanged?.Invoke(visible);
                };
                _privacyCurtain.Start();
            }

            _keyGuard = new InjectedKeyGuard();
            _keyGuard.OnEmergencyStopTriggered += () =>
            {
                _ = CloseActiveSessionAsync("host_hotkey");
            };
            _keyGuard.Install();

            // Oyuncuya kesin parametrelerle GS_START gönder (PDF Bölüm 6)
            _webrtc.SendGameStreamTo(ActiveRemotePeerKey, new GsStartMessage
            {
                SessionId = ActiveSessionId,
                App = ActiveGameName ?? "Oyun",
                Resolution = profile.Resolution,
                Fps = profile.Fps,
                Codec = profile.Codec,
                BitrateKbps = profile.BitrateKbps
            });

            SetPhase(GameStreamPhase.Playing, $"Oynanıyor: {ActiveGameName} ({profile.SummaryText})");
            BroadcastPresence();
        }

        // =====================================================================
        // MESAJ İŞLEME VE DOĞRULAMA (PDF Bölüm 6)
        // =====================================================================

        private async void HandleGameStreamMessage(string fromPeerKey, string fromPeerName, string msgType, JsonElement root)
        {
            try
            {
                if (root.GetRawText().Length > 8192) return;

                switch (msgType)
                {
                    case GsPresenceMessage.MsgType:
                        HandlePresenceMessage(fromPeerKey, fromPeerName, root);
                        break;
                    case GsInviteMessage.MsgType:
                        HandleInviteMessage(fromPeerKey, fromPeerName, root);
                        break;
                    case GsInviteCancelMessage.MsgType:
                        HandleInviteCancelMessage(fromPeerKey, root);
                        break;
                    case GsResponseMessage.MsgType:
                        HandleResponseMessage(fromPeerKey, root);
                        break;
                    case GsPairPinMessage.MsgType:
                        await HandlePairPinMessageAsync(fromPeerKey, root);
                        break;
                    case GsPairResultMessage.MsgType:
                        HandlePairResultMessage(fromPeerKey, root);
                        break;
                    case GsStartMessage.MsgType:
                        await HandleStartMessageAsync(fromPeerKey, root);
                        break;
                    case GsStateMessage.MsgType:
                        HandleStateMessage(fromPeerKey, root);
                        break;
                    case GsCurtainMessage.MsgType:
                        HandleCurtainMessage(fromPeerKey, root);
                        break;
                    case GsEndMessage.MsgType:
                        HandleEndMessage(fromPeerKey, root);
                        break;
                }
            }
            catch { }
        }

        private async Task HandlePairPinMessageAsync(string fromPeerKey, JsonElement root)
        {
            if (fromPeerKey != ActiveRemotePeerKey || Role != GameStreamRole.Host) return;

            string pin = root.TryGetProperty("pin", out var p) ? p.GetString() ?? "" : "";
            if (pin.Length != 4) return;

            bool paired = await _streamHost.PairPinAsync(pin, $"RUNA-{ActiveRemotePeerName}");

            _webrtc.SendGameStreamTo(fromPeerKey, new GsPairResultMessage
            {
                SessionId = ActiveSessionId ?? "",
                Ok = paired,
                Error = paired ? null : "Eşleştirme başarısız"
            });

            if (paired)
            {
                await HostStartTunnelAndProbeAsync();
            }
            else
            {
                await CloseActiveSessionAsync("pair_error");
            }
        }

        private void HandlePairResultMessage(string fromPeerKey, JsonElement root)
        {
            if (fromPeerKey != ActiveRemotePeerKey || Role != GameStreamRole.Player) return;

            bool ok = root.TryGetProperty("ok", out var pOk) && pOk.GetBoolean();
            if (ok)
            {
                SetPhase(GameStreamPhase.Tunneling, "Tünel hazırlanıyor...");
            }
            else
            {
                _ = CloseActiveSessionAsync("pair_error");
            }
        }

        private async Task HandleStartMessageAsync(string fromPeerKey, JsonElement root)
        {
            if (fromPeerKey != ActiveRemotePeerKey || Role != GameStreamRole.Player) return;

            var startMsg = JsonSerializer.Deserialize<GsStartMessage>(root.GetRawText());
            if (startMsg == null) return;

            SetPhase(GameStreamPhase.Launching, "Moonlight başlatılıyor...");

            int width = startMsg.Resolution == "720p" ? 1280 : 1920;
            int height = startMsg.Resolution == "720p" ? 720 : 1080;

            bool started = await _streamClient.StartStreamAsync(
                "127.0.0.77",
                startMsg.App,
                width,
                height,
                startMsg.Fps,
                startMsg.BitrateKbps,
                startMsg.Codec,
                fullscreen: true);

            if (started)
            {
                SetPhase(GameStreamPhase.Playing, $"Oynanıyor: {startMsg.App}");
                BroadcastPresence();
            }
            else
            {
                await CloseActiveSessionAsync("moonlight_start_error");
            }
        }

        private void HandlePresenceMessage(string fromPeerKey, string fromPeerName, JsonElement root)
        {
            bool open = root.TryGetProperty("open", out var pOpen) && pOpen.GetBoolean();
            bool canHost = root.TryGetProperty("canHost", out var pHost) && pHost.GetBoolean();
            bool canPlay = root.TryGetProperty("canPlay", out var pPlay) && pPlay.GetBoolean();
            bool busy = root.TryGetProperty("busy", out var pBusy) && pBusy.GetBoolean();
            string? playingWith = root.TryGetProperty("playingWith", out var pWith) ? pWith.GetString() : null;
            int version = root.TryGetProperty("v", out var pVer) && pVer.TryGetInt32(out int v) ? v : 1;

            var codecs = new List<string>();
            if (root.TryGetProperty("codecs", out var pCodecs) && pCodecs.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pCodecs.EnumerateArray())
                {
                    string? c = el.GetString();
                    if (!string.IsNullOrEmpty(c)) codecs.Add(c);
                }
            }

            if (!open)
            {
                _activePeers.TryRemove(fromPeerKey, out _);
                PeerGamingStatusChanged?.Invoke(fromPeerKey, false, null);
            }
            else
            {
                var presence = _activePeers.GetOrAdd(fromPeerKey, _ => new GameStreamPeerPresence
                {
                    PeerKey = fromPeerKey,
                    PeerName = fromPeerName
                });

                presence.PeerName = fromPeerName;
                presence.IsOpen = open;
                presence.CanHost = canHost;
                presence.CanPlay = canPlay;
                presence.IsBusy = busy;
                presence.PlayingWith = playingWith;
                presence.Codecs = codecs;
                presence.ProtocolVersion = version;

                PeerGamingStatusChanged?.Invoke(fromPeerKey, busy, playingWith);
            }

            PresenceUpdated?.Invoke();
        }

        private void HandleInviteMessage(string fromPeerKey, string fromPeerName, JsonElement root)
        {
            var invite = JsonSerializer.Deserialize<GsInviteMessage>(root.GetRawText());
            if (invite == null || string.IsNullOrWhiteSpace(invite.SessionId)) return;

            if (invite.Game.Length > 80) return;

            if (Phase != GameStreamPhase.Idle)
            {
                var busyResp = new GsResponseMessage
                {
                    SessionId = invite.SessionId,
                    Accept = false,
                    Reason = "busy"
                };
                _webrtc.SendGameStreamTo(fromPeerKey, busyResp);
                return;
            }

            if (!LocalCapabilities.CanPlay)
            {
                var noMlResp = new GsResponseMessage
                {
                    SessionId = invite.SessionId,
                    Accept = false,
                    Reason = "no_moonlight"
                };
                _webrtc.SendGameStreamTo(fromPeerKey, noMlResp);
                return;
            }

            lock (_sessionLock)
            {
                Phase = GameStreamPhase.Invited;
                Role = GameStreamRole.Player;
                ActiveSessionId = invite.SessionId;
                ActiveRemotePeerKey = fromPeerKey;
                ActiveRemotePeerName = fromPeerName;
                ActiveGameName = invite.Game;
            }

            InviteReceived?.Invoke(invite, fromPeerKey, fromPeerName);
        }

        private void HandleInviteCancelMessage(string fromPeerKey, JsonElement root)
        {
            string sid = root.TryGetProperty("sid", out var pSid) ? pSid.GetString() ?? "" : "";
            lock (_sessionLock)
            {
                if (Phase == GameStreamPhase.Invited && ActiveSessionId == sid && ActiveRemotePeerKey == fromPeerKey)
                {
                    Phase = GameStreamPhase.Idle;
                    Role = GameStreamRole.None;
                    ActiveSessionId = null;
                    ActiveRemotePeerKey = null;
                    ActiveRemotePeerName = null;
                    ActiveGameName = null;
                }
            }

            InviteCancelled?.Invoke(sid);
        }

        private void HandleResponseMessage(string fromPeerKey, JsonElement root)
        {
            var resp = JsonSerializer.Deserialize<GsResponseMessage>(root.GetRawText());
            if (resp == null) return;

            lock (_sessionLock)
            {
                if (Phase != GameStreamPhase.Inviting || ActiveSessionId != resp.SessionId || ActiveRemotePeerKey != fromPeerKey)
                {
                    return;
                }

                if (resp.Accept)
                {
                    Phase = GameStreamPhase.Pairing;
                }
                else
                {
                    Phase = GameStreamPhase.Idle;
                    Role = GameStreamRole.None;
                    ActiveSessionId = null;
                    ActiveRemotePeerKey = null;
                    ActiveRemotePeerName = null;
                    ActiveGameName = null;
                    PlayingWithPeerName = null;
                }
            }

            BroadcastPresence();
            ResponseReceived?.Invoke(resp);
        }

        private void HandleCurtainMessage(string fromPeerKey, JsonElement root)
        {
            if (fromPeerKey != ActiveRemotePeerKey) return;
            bool on = root.TryGetProperty("on", out var pOn) && pOn.GetBoolean();
            CurtainChanged?.Invoke(on);
        }

        private void HandleEndMessage(string fromPeerKey, JsonElement root)
        {
            if (fromPeerKey != ActiveRemotePeerKey) return;
            string reason = root.TryGetProperty("reason", out var pR) ? pR.GetString() ?? "user" : "user";
            _ = CloseActiveSessionAsync(reason);
        }

        public async Task CloseActiveSessionAsync(string reason)
        {
            string? sid;
            string? remoteKey;
            string? remoteName;
            bool wasHost;

            lock (_sessionLock)
            {
                if (Phase == GameStreamPhase.Idle) return;
                Phase = GameStreamPhase.Ending;
                sid = ActiveSessionId;
                remoteKey = ActiveRemotePeerKey;
                remoteName = ActiveRemotePeerName;
                wasHost = Role == GameStreamRole.Host;
            }

            PhaseChanged?.Invoke(GameStreamPhase.Ending, "Oturum sonlandırılıyor...");

            if (!string.IsNullOrEmpty(remoteKey) && !string.IsNullOrEmpty(sid))
            {
                _webrtc.SendGameStreamTo(remoteKey, new GsEndMessage
                {
                    SessionId = sid,
                    Reason = reason
                });
            }

            _phaseTimeoutCts?.Cancel();
            _phaseTimeoutCts?.Dispose();
            _phaseTimeoutCts = null;

            // Tüneli kapat
            ActiveTunnel?.Dispose();
            ActiveTunnel = null;

            // Gizlilik perdesi ve tuş kancasını temizle (Faz 5)
            _privacyCurtain?.Dispose();
            _privacyCurtain = null;

            _keyGuard?.Dispose();
            _keyGuard = null;

            // Moonlight'ı durdur
            try
            {
                await _streamClient.StopStreamAsync("127.0.0.77");
            }
            catch { }

            // Host temizlikleri (PDF Bölüm 11)
            if (wasHost)
            {
                // Ekran çözünürlüğünü geri yükle
                await _streamHost.RestoreResolutionAsync();

                // RUNA Opus bitrate ve ekran paylaşımını eski haline getir
                if (_originalOpusBitrate > 0)
                {
                    _storage.Settings.OpusBitrateKbps = _originalOpusBitrate;
                    _webrtc.RefreshCodecSettings();
                    _originalOpusBitrate = 0;
                }

                if (_wasScreenSharing)
                {
                    _wasScreenSharing = false;
                }

                // Geçici eşleşmeyi kaldır (eğer güvenilen listesinde değilse)
                if (!string.IsNullOrEmpty(remoteName))
                {
                    string lowerName = remoteName.ToLowerInvariant();
                    if (!_storage.Settings.GameStreamTrustedPlayers.Contains(lowerName))
                    {
                        await _streamHost.UnpairClientAsync(remoteName);
                    }
                }

                if (_streamHost is MockStreamHost mockHost)
                {
                    mockHost.StopEchoServers();
                }
            }

            lock (_sessionLock)
            {
                Phase = GameStreamPhase.Idle;
                Role = GameStreamRole.None;
                ActiveSessionId = null;
                ActiveRemotePeerKey = null;
                ActiveRemotePeerName = null;
                ActiveGameName = null;
                ActiveGameExePath = null;
                ActiveExtraProcesses.Clear();
                PlayingWithPeerName = null;
                ActiveQualityProfile = null;
            }

            BroadcastPresence();
            PhaseChanged?.Invoke(GameStreamPhase.Idle, "Boşta");
            SessionEnded?.Invoke(reason);
        }

        private CancellationTokenSource? _phaseTimeoutCts;

        private void ArmPhaseTimeout(GameStreamPhase phase)
        {
            _phaseTimeoutCts?.Cancel();
            _phaseTimeoutCts?.Dispose();
            _phaseTimeoutCts = null;

            int timeoutSec = phase switch
            {
                GameStreamPhase.Pairing => 30,    // Pairing 30 sn (PDF Bölüm 11)
                GameStreamPhase.Tunneling => 15,  // Tunneling 15 sn (PDF Bölüm 11)
                GameStreamPhase.Probing => 10,    // Probing 10 sn (PDF Bölüm 11)
                GameStreamPhase.Launching => 60,  // Launching 60 sn (PDF Bölüm 11)
                _ => 0
            };

            if (timeoutSec > 0)
            {
                _phaseTimeoutCts = new CancellationTokenSource();
                var token = _phaseTimeoutCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(timeoutSec), token);
                        if (!token.IsCancellationRequested && Phase == phase)
                        {
                            await CloseActiveSessionAsync("timeout");
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
        }

        public void SetPhase(GameStreamPhase phase, string info)
        {
            lock (_sessionLock)
            {
                Phase = phase;
            }

            ArmPhaseTimeout(phase);

            // Her geçişte GS_STATE gönderilir (PDF Bölüm 6 & 11)
            if (!string.IsNullOrEmpty(ActiveRemotePeerKey) && !string.IsNullOrEmpty(ActiveSessionId))
            {
                _webrtc.SendGameStreamTo(ActiveRemotePeerKey, new GsStateMessage
                {
                    SessionId = ActiveSessionId,
                    Phase = phase.ToString().ToLowerInvariant(),
                    Info = info
                });
            }

            PhaseChanged?.Invoke(phase, info);
        }

        private void HandleStateMessage(string fromPeerKey, JsonElement root)
        {
            if (fromPeerKey != ActiveRemotePeerKey) return;
            string sid = root.TryGetProperty("sid", out var pSid) ? pSid.GetString() ?? "" : "";
            if (sid != ActiveSessionId) return;

            string phase = root.TryGetProperty("phase", out var pPhase) ? pPhase.GetString() ?? "" : "";
            string info = root.TryGetProperty("info", out var pInfo) ? pInfo.GetString() ?? "" : "";

            RemoteStateReceived?.Invoke(phase, info);
        }

        public void Dispose()
        {
            _webrtc.OnGameStreamMessage -= HandleGameStreamMessage;
            _webrtc.OnControlChannelOpened -= HandleControlChannelOpened;
            _webrtc.OnGameStreamTcpChannelOpened -= HandleIncomingTcpChannel;
            _ = CloseActiveSessionAsync("user");
        }
    }
}
