using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Çoklu Broker Yedekli (EMQX -> HiveMQ -> Mosquitto) P2P Sinyalleşme, Oda Doğrulama,
    /// Kalıcı Oda Canlı Çevrimiçi İzleyici (Presence Watcher) ve Geçici Sohbet Senkronizasyon Servisi.
    /// </summary>
    public sealed class SignalingService : IDisposable
    {
        private static readonly string[] BrokerUrls =
        {
            "wss://broker.emqx.io:8084/mqtt",
            "wss://broker.hivemq.com:8884/mqtt",
            "wss://test.mosquitto.org:8081/mqtt"
        };

        private readonly MqttClientFactory _factory = new();
        private IMqttClient? _mqttClient;
        private IMqttClient? _watcherClient;
        private int _activeBrokerIndex = 0;
        private bool _brokerPinned = false; // oda kontrolü odayı belirli bir sunucuda buldu

        private string _currentRoom = "";
        private string _currentRoomTitle = "";
        private string _currentRoomActivity = "Sohbet ediliyor";
        private bool _currentIsPersistent = false;
        private UserProfile? _currentUser;

        private Timer? _heartbeatTimer;
        private Timer? _pruneTimer;
        private Timer? _watcherSweepTimer;
        private bool _isLeavingOrDisposed = false;

        // Sohbet tekilleştirme ve geçmiş önbelleği
        private readonly HashSet<string> _seenChatIds = new();
        private readonly List<ChatMessage> _roomChatHistory = new();
        private readonly object _chatLock = new();

        // Kalıcı oda canlı çevrimiçi sayacı: roomCode -> (peerKey -> lastSeenMs)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _watchedRoomPeers = new();

        public string ActiveBrokerUrl => BrokerUrls[_activeBrokerIndex % BrokerUrls.Length];
        public bool IsConnected => _mqttClient?.IsConnected == true;
        public string CurrentRoomCode => _currentRoom;
        public string CurrentRoomActivity => _currentRoomActivity;
        public string ActiveEngineName { get; set; } = "legacy";
        public int ActiveEngineProto => ActiveEngineName == "rust" ? 2 : 1;

        public ConcurrentDictionary<string, PeerPresenceSnapshot> Participants { get; } = new();

        // Olaylar
        public event Action<PeerPresenceSnapshot>? OnPeerJoined;
        public event Action<PeerPresenceSnapshot>? OnPeerUpdated;
        public event Action<string, string>? OnPeerLeft; // peerKey, peerName
        public event Action<ChatMessage>? OnChatMessageReceived;
        public event Action<string, int>? OnPollVoteReceived; // messageId, optionIndex (1 or 2)
        public event Action<string, string, string, string>? OnSignalReceived; // fromPeerKey, fromName, sigType, data
        public event Action<string, string, string, bool>? OnRoomMetadataReceived; // code, title, activity, isPersistent
        public event Action<string, int>? OnWatchedRoomOnlineChanged; // roomCode, onlineCount
        public event Action<string>? OnBrokerStatusChanged;

        // Bağlanma tek seferde bir kez yürür: sunucu değişimindeki "koptu" olayının yeniden bağlanması ile
        // JoinRoomAsync aynı anda istemci oluşturup birbirininkini atarsa, gönderebilen ama oda konusuna
        // abone olmayan bir istemci kalabiliyordu (karşı taraf bizi görür, biz onu hiç görmeyiz).
        private readonly SemaphoreSlim _connectLock = new(1, 1);

        public async Task<bool> ConnectAsync()
        {
            if (_mqttClient != null && _mqttClient.IsConnected) return true;

            await _connectLock.WaitAsync();
            try
            {
                if (_mqttClient != null && _mqttClient.IsConnected) return true;
                bool ok = await ConnectCoreAsync();
                // Odadaysak abonelik bağlantının parçasıdır; hangi yol bağlamış olursa olsun konu dinlenir
                if (ok && !string.IsNullOrEmpty(_currentRoom) && _currentUser != null)
                {
                    try { await SubscribeRoomTopicsAsync(); } catch { }
                }
                return ok;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task<bool> ConnectCoreAsync()
        {
            // Seçili sunucu (oda kontrolünün odayı bulduğu ya da odanın şu an kullandığı) iki kez denenir:
            // tek bir yavaş WebSocket/TLS el sıkışması yüzünden başka sunucuya geçmek, katılımcıları farklı
            // sunuculara bölüp birbirini hiç göremez hâle getirir.
            // (Lobide, oda bağlamı yokken ilk sunucu ölüyse gecikmesin diye yalnızca bir kez ve kısa sürede denenir.)
            bool stickToActive = _brokerPinned || !string.IsNullOrEmpty(_currentRoom);
            var order = new List<int> { _activeBrokerIndex % BrokerUrls.Length };
            if (stickToActive) order.Add(_activeBrokerIndex % BrokerUrls.Length);
            for (int k = 1; k < BrokerUrls.Length; k++) order.Add((_activeBrokerIndex + k) % BrokerUrls.Length);

            for (int n = 0; n < order.Count; n++)
            {
                int idx = order[n];
                string url = BrokerUrls[idx];
                int timeoutSec = stickToActive && n < 2 ? 6 : 4;

                try
                {
                    if (_mqttClient != null)
                    {
                        try
                        {
                            _mqttClient.ApplicationMessageReceivedAsync -= HandleIncomingMessage;
                            _mqttClient.Dispose();
                        }
                        catch { }
                    }

                    var client = _factory.CreateMqttClient();
                    _mqttClient = client;
                    var options = new MqttClientOptionsBuilder()
                        .WithWebSocketServer(o => o.WithUri(url))
                        .WithClientId($"runa_w3_{Guid.NewGuid():N}"[..20])
                        .WithCleanSession()
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(20))
                        .WithTimeout(TimeSpan.FromSeconds(timeoutSec))
                        .Build();

                    client.ApplicationMessageReceivedAsync += HandleIncomingMessage;
                    // Yalnızca hâlâ geçerli olan istemcinin kopması yeniden bağlanmayı tetikler;
                    // bilerek kapatılan/değiştirilen eski istemcinin olayı yok sayılır.
                    client.DisconnectedAsync += e => ReferenceEquals(client, _mqttClient)
                        ? HandleDisconnectedAsync(e)
                        : Task.CompletedTask;

                    await client.ConnectAsync(options);
                    _activeBrokerIndex = idx;
                    OnBrokerStatusChanged?.Invoke($"Bağlı ({new Uri(url).Host})");
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MQTT] Broker {url} failed: {ex.Message}");
                }
            }

            OnBrokerStatusChanged?.Invoke("Çevrimdışı (Yeniden deneniyor...)");
            return false;
        }

        private async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            if (_isLeavingOrDisposed || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;

            OnBrokerStatusChanged?.Invoke("Bağlantı koptu, yeniden bağlanılıyor...");
            await Task.Delay(1500);
            if (_isLeavingOrDisposed || string.IsNullOrEmpty(_currentRoom)) return;

            if (await ConnectAsync() && _mqttClient != null && _currentUser != null)
            {
                try
                {
                    await PublishPresenceAsync("PEER_JOIN");
                }
                catch { }
            }
        }

        /// <summary>
        /// Odanın şu an aktif olup olmadığını tüm sinyalleşme sunucularında AYNI ANDA sorgular.
        /// Odayı kuran kişi yedek bir sunucuya düşmüş olabilir; ilk yanıt veren sunucu kazanır ve
        /// <see cref="RoomCheckResult.BrokerIndex"/> ile döner (katılım o sunucu üzerinden yapılmalıdır).
        /// <paramref name="replyWaitMs"/> yanıt bekleme süresidir ve sunucuya bağlandıktan SONRA başlar;
        /// WebSocket/TLS bağlantısı tek başına 1-3 sn sürebildiği için bağlantı süresi bu bütçeden düşülmez.
        /// </summary>
        public async Task<RoomCheckResult> CheckRoomExistsAsync(string roomCode, int replyWaitMs = 6000)
        {
            var clean = StorageService.NormalizeRoomCode(roomCode);
            if (string.IsNullOrWhiteSpace(clean))
                return new RoomCheckResult { Exists = false, Reason = "EMPTY_CODE" };

            var found = new TaskCompletionSource<RoomCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelAll = new CancellationTokenSource();

            var probes = Enumerable.Range(0, BrokerUrls.Length)
                .Select(i => ProbeBrokerForRoomAsync(i, clean, replyWaitMs, found, cancelAll.Token))
                .ToArray();
            var allDone = Task.WhenAll(probes);

            var winner = await Task.WhenAny(found.Task, allDone);
            cancelAll.Cancel(); // Diğer sunuculardaki sorguları bırak
            if (winner == found.Task) return found.Task.Result;

            return new RoomCheckResult { Exists = false, Reason = "NOT_FOUND" };
        }

        private async Task ProbeBrokerForRoomAsync(int brokerIndex, string clean, int replyWaitMs,
            TaskCompletionSource<RoomCheckResult> found, CancellationToken cancelAll)
        {
            string url = BrokerUrls[brokerIndex];
            using var probeClient = _factory.CreateMqttClient();
            int seenPeers = 0;

            try
            {
                var options = new MqttClientOptionsBuilder()
                    .WithWebSocketServer(o => o.WithUri(url))
                    .WithClientId($"probe_{Guid.NewGuid():N}"[..15])
                    .WithCleanSession()
                    .WithTimeout(TimeSpan.FromSeconds(6))
                    .Build();

                probeClient.ApplicationMessageReceivedAsync += e =>
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : "";

                        if (type is "ROOM_DISCOVERY_PONG" or "PRESENCE_HEARTBEAT" or "PEER_PING" or "PEER_JOIN" or "PEER_UPDATE")
                        {
                            Interlocked.Increment(ref seenPeers);
                            var peer = root.TryGetProperty("peerName", out var pn) ? pn.GetString() : "Katılımcı";
                            var title = root.TryGetProperty("roomTitle", out var rt) ? rt.GetString() : clean;
                            var act = root.TryGetProperty("activity", out var ac) ? ac.GetString() : "Sohbet ediliyor";
                            var isP = root.TryGetProperty("isPersistent", out var ip) && ip.GetBoolean();

                            found.TrySetResult(new RoomCheckResult
                            {
                                Exists = true,
                                PeerName = peer ?? "Katılımcı",
                                RoomTitle = string.IsNullOrWhiteSpace(title) ? clean : title!,
                                RoomActivity = string.IsNullOrWhiteSpace(act) ? "Sohbet ediliyor" : act!,
                                IsPersistent = isP,
                                OnlineCount = Math.Max(1, seenPeers),
                                BrokerIndex = brokerIndex
                            });
                        }
                    }
                    catch { }
                    return Task.CompletedTask;
                };

                await probeClient.ConnectAsync(options, cancelAll);
                await probeClient.SubscribeAsync($"runa/p2p/{clean}/presence", cancellationToken: cancelAll);

                var pingMsg = new MqttApplicationMessageBuilder()
                    .WithTopic($"runa/p2p/{clean}/presence")
                    .WithPayload(JsonSerializer.Serialize(new
                    {
                        type = "ROOM_DISCOVERY_PING",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }))
                    .Build();
                await probeClient.PublishAsync(pingMsg, cancelAll);

                // Yanıt (PONG) veya odadakilerin 2,5 sn'lik düzenli nabzı bu süre içinde gelir.
                // Açık sunucularda tek bir mesaj kaybolabildiği için süre yarılanınca sorgu bir kez daha gönderilir.
                int half = replyWaitMs / 2;
                await Task.WhenAny(found.Task, Task.Delay(half, cancelAll));
                if (!found.Task.IsCompleted)
                {
                    await probeClient.PublishAsync(pingMsg, cancelAll);
                    await Task.WhenAny(found.Task, Task.Delay(replyWaitMs - half, cancelAll));
                }
            }
            catch
            {
                // Bu sunucuya ulaşılamadı veya sorgu iptal edildi; diğer sunucular sonucu belirler
            }
            finally
            {
                try { if (probeClient.IsConnected) await probeClient.DisconnectAsync(); } catch { }
            }
        }

        /// <summary>
        /// Odaya katılmadan önce, odanın bulunduğu sunucuyu tercih edilen sunucu yapar.
        /// Ana bağlantı başka bir sunucudaysa bir sonraki <see cref="ConnectAsync"/> o sunucuya bağlanır.
        /// </summary>
        public async Task UseBrokerAsync(int brokerIndex)
        {
            if (brokerIndex < 0 || brokerIndex >= BrokerUrls.Length) return;
            _brokerPinned = true;
            if (brokerIndex == _activeBrokerIndex) return;
            _activeBrokerIndex = brokerIndex;
            await _connectLock.WaitAsync();
            try
            {
                // Önce referansı bırak: eski istemcinin "koptu" olayı geçerli istemci olmadığı için yeniden bağlanmayı tetiklemez
                var old = _mqttClient;
                _mqttClient = null;
                if (old != null)
                {
                    try
                    {
                        old.ApplicationMessageReceivedAsync -= HandleIncomingMessage;
                        if (old.IsConnected) await old.DisconnectAsync();
                        old.Dispose();
                    }
                    catch { }
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <summary>
        /// Lobideki Kalıcı Odaların canlı çevrimiçi sayısını gerçek zamanlı izler.
        /// </summary>
        public async Task StartWatchingPersistentRoomsAsync(IEnumerable<string> roomCodes)
        {
            var codes = roomCodes
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(StorageService.NormalizeRoomCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (codes.Count == 0)
            {
                await StopWatchingPersistentRoomsAsync();
                return;
            }

            try
            {
                if (_watcherClient == null || !_watcherClient.IsConnected)
                {
                    _watcherClient?.Dispose();
                    _watcherClient = _factory.CreateMqttClient();

                    var options = new MqttClientOptionsBuilder()
                        .WithWebSocketServer(o => o.WithUri(ActiveBrokerUrl))
                        .WithClientId($"watch_{Guid.NewGuid():N}"[..16])
                        .WithCleanSession()
                        .WithTimeout(TimeSpan.FromSeconds(4))
                        .Build();

                    _watcherClient.ApplicationMessageReceivedAsync += HandleWatcherMessageAsync;
                    await _watcherClient.ConnectAsync(options);
                }

                foreach (var code in codes)
                {
                    _watchedRoomPeers.TryAdd(code, new ConcurrentDictionary<string, long>());
                    await _watcherClient.SubscribeAsync($"runa/p2p/{code}/presence");

                    var ping = new MqttApplicationMessageBuilder()
                        .WithTopic($"runa/p2p/{code}/presence")
                        .WithPayload(JsonSerializer.Serialize(new
                        {
                            type = "ROOM_DISCOVERY_PING",
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }))
                        .Build();
                    await _watcherClient.PublishAsync(ping);
                }

                _watcherSweepTimer?.Dispose();
                _watcherSweepTimer = new Timer(_ => SweepWatchedRooms(), null, 3000, 3500);
            }
            catch { }
        }

        public async Task StopWatchingPersistentRoomsAsync()
        {
            _watcherSweepTimer?.Dispose();
            _watcherSweepTimer = null;
            _watchedRoomPeers.Clear();

            if (_watcherClient != null)
            {
                try
                {
                    if (_watcherClient.IsConnected) await _watcherClient.DisconnectAsync();
                    _watcherClient.Dispose();
                }
                catch { }
                _watcherClient = null;
            }
        }

        private Task HandleWatcherMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                string topic = e.ApplicationMessage.Topic; // runa/p2p/{CODE}/presence
                var parts = topic.Split('/');
                if (parts.Length < 4) return Task.CompletedTask;
                string roomCode = parts[2].ToUpperInvariant();

                var json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                string peerKey = ExtractPeerKey(root);
                if (string.IsNullOrEmpty(peerKey)) return Task.CompletedTask;

                var map = _watchedRoomPeers.GetOrAdd(roomCode, _ => new ConcurrentDictionary<string, long>());

                if (type == "PEER_LEAVE")
                {
                    map.TryRemove(peerKey, out _);
                }
                else if (type is "PEER_JOIN" or "PEER_PING" or "PRESENCE_HEARTBEAT" or "ROOM_DISCOVERY_PONG" or "PEER_UPDATE")
                {
                    map[peerKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                OnWatchedRoomOnlineChanged?.Invoke(roomCode, map.Count);
            }
            catch { }
            return Task.CompletedTask;
        }

        private void SweepWatchedRooms()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var kvp in _watchedRoomPeers)
            {
                string code = kvp.Key;
                var map = kvp.Value;
                bool changed = false;
                foreach (var p in map)
                {
                    if (now - p.Value > 8500)
                    {
                        if (map.TryRemove(p.Key, out _)) changed = true;
                    }
                }
                if (changed)
                {
                    OnWatchedRoomOnlineChanged?.Invoke(code, map.Count);
                }
            }
        }

        public async Task JoinRoomAsync(string roomCode, UserProfile profile, string roomTitle = "", bool isPersistent = false, string activity = "Sohbet ediliyor")
        {
            _isLeavingOrDisposed = false;
            _currentRoom = StorageService.NormalizeRoomCode(roomCode);
            _currentUser = profile;
            _currentRoomTitle = string.IsNullOrWhiteSpace(roomTitle) ? _currentRoom : roomTitle;
            _currentIsPersistent = isPersistent;
            _currentRoomActivity = string.IsNullOrWhiteSpace(activity) ? "Sohbet ediliyor" : activity;

            Participants.Clear();
            lock (_chatLock)
            {
                _seenChatIds.Clear();
                _roomChatHistory.Clear();
            }

            try
            {
                await ConnectAsync();
                if (_mqttClient == null || !_mqttClient.IsConnected) return;

                await SubscribeRoomTopicsAsync();

                // Katılım duyurusu ve geçmiş sohbet isteği
                await PublishPresenceAsync("PEER_JOIN");
                await RequestChatHistoryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Signaling] JoinRoomAsync error: {ex.Message}");
            }

            _heartbeatTimer?.Dispose();
            _heartbeatTimer = new Timer(async _ =>
            {
                try { await PublishPresenceAsync("PRESENCE_HEARTBEAT"); } catch { }
            }, null, 2500, 2500);

            _pruneTimer?.Dispose();
            _pruneTimer = new Timer(_ => PruneStalePeers(), null, 3000, 2500);
        }

        private async Task SubscribeRoomTopicsAsync()
        {
            if (_mqttClient == null || !_mqttClient.IsConnected || _currentUser == null || string.IsNullOrEmpty(_currentRoom)) return;

            string safeRoom = MqttKeyHelper.SanitizeTopicSegment(_currentRoom).ToUpperInvariant();
            string mySafeKey = MqttKeyHelper.SanitizeTopicSegment(_currentUser.PeerKey);
            string mySafeLower = MqttKeyHelper.SanitizeTopicSegment(_currentUser.Name);

            await _mqttClient.SubscribeAsync($"runa/p2p/{safeRoom}/presence");
            await _mqttClient.SubscribeAsync($"runa/p2p/{safeRoom}/sig/{mySafeKey}");
            if (!string.Equals(mySafeLower, mySafeKey, StringComparison.OrdinalIgnoreCase))
            {
                await _mqttClient.SubscribeAsync($"runa/p2p/{safeRoom}/sig/{mySafeLower}");
            }
            await _mqttClient.SubscribeAsync($"runa/p2p/{safeRoom}/chat");
        }

        public async Task LeaveRoomAsync()
        {
            _isLeavingOrDisposed = true;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _pruneTimer?.Dispose();
            _pruneTimer = null;

            if (_mqttClient != null && _mqttClient.IsConnected && !string.IsNullOrEmpty(_currentRoom) && _currentUser != null)
            {
                try
                {
                    string safeRoom = MqttKeyHelper.SanitizeTopicSegment(_currentRoom).ToUpperInvariant();
                    string mySafeKey = MqttKeyHelper.SanitizeTopicSegment(_currentUser.PeerKey);
                    string mySafeLower = MqttKeyHelper.SanitizeTopicSegment(_currentUser.Name);

                    await PublishPresenceAsync("PEER_LEAVE");
                    await _mqttClient.UnsubscribeAsync($"runa/p2p/{safeRoom}/presence");
                    await _mqttClient.UnsubscribeAsync($"runa/p2p/{safeRoom}/sig/{mySafeKey}");
                    if (!string.Equals(mySafeLower, mySafeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        await _mqttClient.UnsubscribeAsync($"runa/p2p/{safeRoom}/sig/{mySafeLower}");
                    }
                    await _mqttClient.UnsubscribeAsync($"runa/p2p/{safeRoom}/chat");
                }
                catch { }
            }

            _currentRoom = "";
            _brokerPinned = false;
            Participants.Clear();
            lock (_chatLock)
            {
                _seenChatIds.Clear();
                _roomChatHistory.Clear();
            }
        }

        public async Task SetRoomActivityAsync(string activity)
        {
            _currentRoomActivity = activity;
            await PublishPresenceAsync("PEER_UPDATE");
        }

        public async Task PublishPeerUpdateAsync()
        {
            await PublishPresenceAsync("PEER_UPDATE");
        }

        private async Task PublishPresenceAsync(string type)
        {
            if (_mqttClient == null || !_mqttClient.IsConnected || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;

            string avatarToShare = GetShareableAvatar(_currentUser);

            var payload = JsonSerializer.Serialize(new
            {
                type,
                peerKey = _currentUser.PeerKey,
                peerName = _currentUser.Name,
                fromLower = _currentUser.Name.Trim().ToLowerInvariant(),
                voiceSessionId = _currentUser.VoiceSessionId,
                avatar = avatarToShare,
                isMuted = _currentUser.IsMuted,
                isDeafened = _currentUser.IsDeafened,
                isScreenSharing = _currentUser.IsScreenSharing,
                roomTitle = _currentRoomTitle,
                activity = _currentRoomActivity,
                isPersistent = _currentIsPersistent,
                engine = ActiveEngineName,
                proto = ActiveEngineProto,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"runa/p2p/{_currentRoom}/presence")
                .WithPayload(payload)
                .Build();

            try { await _mqttClient.PublishAsync(msg); } catch { }
        }

        public async Task SendSignalAsync(string targetPeerKey, string sigType, string dataPayload)
        {
            if (_mqttClient == null || !_mqttClient.IsConnected || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;

            var payload = JsonSerializer.Serialize(new
            {
                type = sigType,
                fromKey = _currentUser.PeerKey,
                from = _currentUser.Name,
                fromLower = _currentUser.Name.Trim().ToLowerInvariant(),
                fromVoiceSessionId = _currentUser.VoiceSessionId,
                to = targetPeerKey,
                data = dataPayload
            });

            string safeRoom = MqttKeyHelper.SanitizeTopicSegment(_currentRoom).ToUpperInvariant();
            string safeTarget = MqttKeyHelper.SanitizeTopicSegment(targetPeerKey);

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"runa/p2p/{safeRoom}/sig/{safeTarget}")
                .WithPayload(payload)
                .Build();

            try { await _mqttClient.PublishAsync(msg); } catch { }
        }

        public void RecordOwnChatMessage(ChatMessage msg)
        {
            lock (_chatLock)
            {
                _seenChatIds.Add(msg.Id);
                _roomChatHistory.Add(msg);
                if (_roomChatHistory.Count > 80) _roomChatHistory.RemoveAt(0);
            }
        }

        public async Task SendChatMessageAsync(ChatMessage chatMsg)
        {
            RecordOwnChatMessage(chatMsg);

            if (_mqttClient == null || !_mqttClient.IsConnected || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;

            var wireMsg = new ChatMessage
            {
                Id = chatMsg.Id,
                SenderName = _currentUser.Name,
                SenderAvatar = GetShareableAvatar(_currentUser),
                Text = chatMsg.Text.Length > ChatMessage.MaxWireTextLength ? chatMsg.Text[..ChatMessage.MaxWireTextLength] : chatMsg.Text,
                TimeString = chatMsg.TimeString,
                Timestamp = chatMsg.Timestamp,
                IsMe = false,
                MessageType = chatMsg.MessageType,
                PollData = chatMsg.PollData,
                FileAttachment = chatMsg.CreateWireAttachment(),
                LinkPreview = chatMsg.LinkPreview
            };

            var payload = JsonSerializer.Serialize(new
            {
                type = "CHAT_MSG",
                fromKey = _currentUser.PeerKey,
                from = _currentUser.Name,
                message = wireMsg
            });

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"runa/p2p/{_currentRoom}/chat")
                .WithPayload(payload)
                .Build();

            try { await _mqttClient.PublishAsync(msg); } catch { }
        }

        public async Task SendPollVoteAsync(string messageId, int optionIndex)
        {
            if (_mqttClient == null || !_mqttClient.IsConnected || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;

            var payload = JsonSerializer.Serialize(new
            {
                type = "POLL_VOTE",
                fromKey = _currentUser.PeerKey,
                msgId = messageId,
                option = optionIndex
            });

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"runa/p2p/{_currentRoom}/chat")
                .WithPayload(payload)
                .Build();

            try { await _mqttClient.PublishAsync(msg); } catch { }
        }

        private async Task RequestChatHistoryAsync()
        {
            if (_mqttClient == null || !_mqttClient.IsConnected || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;
            var payload = JsonSerializer.Serialize(new
            {
                type = "CHAT_HISTORY_REQUEST",
                fromKey = _currentUser.PeerKey
            });
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"runa/p2p/{_currentRoom}/chat")
                .WithPayload(payload)
                .Build();
            try { await _mqttClient.PublishAsync(msg); } catch { }
        }

        private async Task PublishChatHistorySyncAsync(string requesterKey)
        {
            if (_mqttClient == null || !_mqttClient.IsConnected || string.IsNullOrEmpty(_currentRoom) || _currentUser == null) return;

            List<ChatMessage> snapshot;
            lock (_chatLock)
            {
                if (_roomChatHistory.Count == 0) return;
                snapshot = _roomChatHistory.TakeLast(30).Select(m => new ChatMessage
                {
                    Id = m.Id,
                    SenderName = m.IsMe ? _currentUser.Name : m.SenderName,
                    SenderAvatar = m.IsMe ? GetShareableAvatar(_currentUser) : m.SenderAvatar,
                    Text = m.Text,
                    TimeString = m.TimeString,
                    Timestamp = m.Timestamp,
                    IsMe = false,
                    MessageType = m.MessageType,
                    LinkPreview = m.LinkPreview == null ? null : new LinkPreviewData
                    {
                        Url = m.LinkPreview.Url,
                        Title = m.LinkPreview.Title,
                        Description = m.LinkPreview.Description,
                        SiteName = m.LinkPreview.SiteName,
                        IsYouTube = m.LinkPreview.IsYouTube,
                        ThumbJpegBase64 = "" // Gecmis senkronizasyonunda agir kucuk resim iletilmez
                    }
                }).ToList();
            }

            var payload = JsonSerializer.Serialize(new
            {
                type = "CHAT_HISTORY_SYNC",
                toKey = requesterKey,
                messages = snapshot
            });

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"runa/p2p/{_currentRoom}/chat")
                .WithPayload(payload)
                .Build();

            try { await _mqttClient.PublishAsync(msg); } catch { }
        }

        private void PruneStalePeers()
        {
            if (string.IsNullOrEmpty(_currentRoom)) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var kvp in Participants)
            {
                if (now - kvp.Value.LastSeen > 8500)
                {
                    if (Participants.TryRemove(kvp.Key, out var removed))
                    {
                        OnPeerLeft?.Invoke(kvp.Key, removed.Name);
                    }
                }
            }
        }

        private static string ExtractPeerKey(JsonElement root)
        {
            if (root.TryGetProperty("peerKey", out var pk) && !string.IsNullOrWhiteSpace(pk.GetString()))
                return pk.GetString()!;
            if (root.TryGetProperty("fromKey", out var fk) && !string.IsNullOrWhiteSpace(fk.GetString()))
                return fk.GetString()!;

            string lower = root.TryGetProperty("fromLower", out var fl) ? fl.GetString() ?? "" : "";
            string sid = root.TryGetProperty("voiceSessionId", out var vs) ? vs.GetString() ?? "" :
                        (root.TryGetProperty("fromVoiceSessionId", out var fvs) ? fvs.GetString() ?? "" : "");

            if (!string.IsNullOrEmpty(lower) && !string.IsNullOrEmpty(sid))
                return $"{MqttKeyHelper.SanitizeTopicSegment(lower)}__{sid}";
            return MqttKeyHelper.SanitizeTopicSegment(lower);
        }

        private Task HandleIncomingMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                if (_currentUser == null || string.IsNullOrEmpty(_currentRoom)) return Task.CompletedTask;

                var topic = e.ApplicationMessage.Topic;
                var json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

                if (topic.EndsWith("/presence"))
                {
                    if (type == "ROOM_DISCOVERY_PING")
                    {
                        _ = PublishPresenceAsync("ROOM_DISCOVERY_PONG");
                        return Task.CompletedTask;
                    }

                    string peerKey = ExtractPeerKey(root);
                    if (string.IsNullOrEmpty(peerKey) || peerKey == _currentUser.PeerKey)
                        return Task.CompletedTask;

                    string peerName = root.TryGetProperty("peerName", out var pn) ? pn.GetString() ?? "Misafir" : "Misafir";

                    if (type is "PEER_JOIN" or "PEER_PING" or "PRESENCE_HEARTBEAT" or "ROOM_DISCOVERY_PONG" or "PEER_UPDATE")
                    {
                        string avatar = root.TryGetProperty("avatar", out var av) ? av.GetString() ?? "" : "";
                        bool isMuted = root.TryGetProperty("isMuted", out var im) && im.GetBoolean();
                        bool isDeafened = root.TryGetProperty("isDeafened", out var id) && id.GetBoolean();
                        bool isScreenSharing = root.TryGetProperty("isScreenSharing", out var iss) && iss.GetBoolean();
                        string roomTitle = root.TryGetProperty("roomTitle", out var rt) ? rt.GetString() ?? "" : "";
                        string activity = root.TryGetProperty("activity", out var ac) ? ac.GetString() ?? "" : "";
                        bool isPersistent = root.TryGetProperty("isPersistent", out var ip) && ip.GetBoolean();
                        string vsid = root.TryGetProperty("voiceSessionId", out var vs) ? vs.GetString() ?? "" : "";
                        string peerEngine = root.TryGetProperty("engine", out var pe) ? pe.GetString() ?? "legacy" : "legacy";
                        int peerProto = root.TryGetProperty("proto", out var pp) && pp.TryGetInt32(out int pr) ? pr : 1;

                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        bool isNewPeer = false;

                        var snapshot = new PeerPresenceSnapshot
                        {
                            PeerKey = peerKey,
                            Name = peerName,
                            VoiceSessionId = vsid,
                            Avatar = string.IsNullOrEmpty(avatar) ? "Assets/avatars/kaan.jpg" : avatar,
                            IsMuted = isMuted,
                            IsDeafened = isDeafened,
                            IsScreenSharing = isScreenSharing,
                            Engine = peerEngine,
                            Proto = peerProto,
                            LastSeen = now
                        };

                        var savedSnapshot = Participants.AddOrUpdate(
                            peerKey,
                            _ =>
                            {
                                isNewPeer = true;
                                return snapshot;
                            },
                            (_, existing) =>
                            {
                                if (string.IsNullOrEmpty(avatar))
                                    snapshot.Avatar = existing.Avatar;
                                return snapshot;
                            });

                        if (isNewPeer)
                        {
                            OnPeerJoined?.Invoke(savedSnapshot);
                            _ = PublishPresenceAsync("PEER_PING");
                        }
                        else
                        {
                            OnPeerUpdated?.Invoke(savedSnapshot);
                        }

                        if (!string.IsNullOrWhiteSpace(roomTitle) || !string.IsNullOrWhiteSpace(activity))
                        {
                            if (!string.IsNullOrWhiteSpace(roomTitle) && roomTitle != _currentRoom)
                                _currentRoomTitle = roomTitle;
                            if (!string.IsNullOrWhiteSpace(activity))
                                _currentRoomActivity = activity;
                            OnRoomMetadataReceived?.Invoke(_currentRoom, _currentRoomTitle, _currentRoomActivity, isPersistent);
                        }
                    }
                    else if (type == "PEER_LEAVE")
                    {
                        if (Participants.TryRemove(peerKey, out var removed))
                        {
                            OnPeerLeft?.Invoke(peerKey, removed.Name);
                        }
                    }
                }
                else if (topic.Contains("/sig/"))
                {
                    string fromKey = ExtractPeerKey(root);
                    if (string.IsNullOrEmpty(fromKey) || fromKey == _currentUser.PeerKey)
                        return Task.CompletedTask;

                    string fromName = root.TryGetProperty("from", out var fn) ? fn.GetString() ?? "Misafir" : "Misafir";

                    // Hem düz string hem de Electron'un gömülü JSON sinyal formatını destekle
                    if (root.TryGetProperty("data", out var dataProp))
                    {
                        if (dataProp.ValueKind == JsonValueKind.String)
                        {
                            OnSignalReceived?.Invoke(fromKey, fromName, type, dataProp.GetString() ?? "");
                        }
                        else if (dataProp.ValueKind == JsonValueKind.Object)
                        {
                            string innerType = dataProp.TryGetProperty("type", out var it) ? it.GetString() ?? "" : "";
                            if (innerType is "offer" or "answer")
                            {
                                string sdp = dataProp.TryGetProperty("sdp", out var sp) ? sp.GetString() ?? "" : "";
                                OnSignalReceived?.Invoke(fromKey, fromName, $"sdp-{innerType}", sdp);
                            }
                            else if (dataProp.TryGetProperty("candidate", out _))
                            {
                                OnSignalReceived?.Invoke(fromKey, fromName, "ice-candidate", dataProp.GetRawText());
                            }
                        }
                    }
                }
                else if (topic.EndsWith("/chat"))
                {
                    string fromKey = ExtractPeerKey(root);
                    if (fromKey == _currentUser.PeerKey) return Task.CompletedTask;

                    if (type == "CHAT_MSG" && root.TryGetProperty("message", out var mObj))
                    {
                        var msg = JsonSerializer.Deserialize<ChatMessage>(mObj.GetRawText());
                        if (msg != null)
                        {
                            // Gönderen adı, odada bilinen katılımcının adından alınır (mesaj içindeki ad taklit edilebilir)
                            if (!string.IsNullOrEmpty(fromKey) && Participants.TryGetValue(fromKey, out var sender))
                                msg.SenderName = sender.Name;
                            DeliverUniqueChatMessage(msg);
                        }
                    }
                    else if (type == "POLL_VOTE")
                    {
                        string msgId = root.TryGetProperty("msgId", out var mi) ? mi.GetString() ?? "" : "";
                        int opt = root.TryGetProperty("option", out var op) ? op.GetInt32() : 1;
                        if (!string.IsNullOrEmpty(msgId))
                        {
                            OnPollVoteReceived?.Invoke(msgId, opt);
                        }
                    }
                    else if (type == "CHAT_HISTORY_REQUEST" && !string.IsNullOrEmpty(fromKey))
                    {
                        _ = PublishChatHistorySyncAsync(fromKey);
                    }
                    else if (type == "CHAT_HISTORY_SYNC")
                    {
                        string toKey = root.TryGetProperty("toKey", out var tk) ? tk.GetString() ?? "" : "";
                        // Yalnızca bize yönelik geçmiş kabul edilir (hedefsiz yayınla herkese sahte geçmiş basılamasın)
                        if (toKey == _currentUser.PeerKey &&
                            root.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            int taken = 0;
                            foreach (var item in arr.EnumerateArray())
                            {
                                if (++taken > 30) break;
                                var msg = JsonSerializer.Deserialize<ChatMessage>(item.GetRawText());
                                if (msg != null) DeliverUniqueChatMessage(msg);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT Parse Error] {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Ağa gönderilebilecek avatar: küçük data URI ya da uygulamayla gelen göreli bir varlık yolu.
        /// Diskteki mutlak yol (kullanıcı adını içerir) asla paylaşılmaz.
        /// </summary>
        public static string GetShareableAvatar(UserProfile user)
        {
            if (!string.IsNullOrEmpty(user.AvatarDataUri)) return user.AvatarDataUri;
            string p = user.AvatarPath ?? "";
            if (p.Length == 0 || Path.IsPathRooted(p) || p.Contains("..")) return "";
            return p.Replace('\\', '/');
        }

        public void DeliverUniqueChatMessage(ChatMessage msg)
        {
            if (string.IsNullOrWhiteSpace(msg.Text) && msg.PollData == null && msg.FileAttachment == null) return;
            msg.SanitizeIncoming();
            lock (_chatLock)
            {
                if (!_seenChatIds.Add(msg.Id)) return;
                _roomChatHistory.Add(msg);
                if (_roomChatHistory.Count > 80) _roomChatHistory.RemoveAt(0);
            }
            OnChatMessageReceived?.Invoke(msg);
        }

        public void Dispose()
        {
            _isLeavingOrDisposed = true;
            _heartbeatTimer?.Dispose();
            _pruneTimer?.Dispose();
            _watcherSweepTimer?.Dispose();
            try { _mqttClient?.Dispose(); } catch { }
            try { _watcherClient?.Dispose(); } catch { }
        }
    }
}
