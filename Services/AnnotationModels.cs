using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RunaVoice_WinUI.Services
{
    public static class AnnotationProtocol
    {
        public const int Scale = 10000; // normalize uzay: 0..10000
        public const int MaxPointsPerMessage = 64; // p dizisinde 128 tam sayı (64 u,v çifti)
        public const int MaxMessageBytes = 16384; // 16 KB

        // 8 renklik sabit palet: Gül Kırmızısı, Kehribar Sarısı, Zümrüt Yeşili, Camgöbeği, Canlı Mavi, Mor, Beyaz, Koyu Gri
        public static readonly uint[] Palette =
        {
            0xFFF43F5E, // 0: Rose
            0xFFF59E0B, // 1: Amber
            0xFF22C55E, // 2: Green
            0xFF06B6D4, // 3: Cyan
            0xFF3B82F6, // 4: Blue
            0xFFA855F7, // 5: Purple
            0xFFFFFFFF, // 6: White
            0xFF111827  // 7: Dark
        };

        // Kalınlıklar (video yüksekliğinin binde biri cinsinden: ‰)
        public static readonly int[] Widths = { 3, 5, 9 }; // İnce (%0.3), Orta (%0.5), Kalın (%0.9)

        /// <summary>
        /// Kararlı özet fonksiyonu ile peerKey'e göre varsayılan canlı renk indeksi belirler (0..5).
        /// </summary>
        public static int DefaultColorIndexFor(string peerKey)
        {
            if (string.IsNullOrEmpty(peerKey)) return 0;
            uint hash = 2166136261;
            foreach (char c in peerKey)
            {
                hash = (hash ^ c) * 16777619;
            }
            return (int)(hash % 6);
        }

        /// <summary>
        /// JSON ağacından gelen ANN_* mesajını doğrular ve ayrıştırır.
        /// Geçersiz veya aralık dışı veriler reddedilir (false döner).
        /// </summary>
        public static bool TryParse(JsonElement root, out AnnotationMessage msg)
        {
            msg = new AnnotationMessage();
            if (!root.TryGetProperty("t", out var tProp) || tProp.ValueKind != JsonValueKind.String)
                return false;

            string? t = tProp.GetString();
            if (string.IsNullOrEmpty(t) || !t.StartsWith("ANN_", StringComparison.Ordinal))
                return false;

            // Röle edildiyse gelebilecek origin alanları (isteğe bağlı)
            if (root.TryGetProperty("o", out var oProp) && oProp.ValueKind == JsonValueKind.String)
                msg.OriginKey = oProp.GetString();
            if (root.TryGetProperty("n", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                msg.OriginName = nProp.GetString();

            switch (t)
            {
                case "ANN_PERM":
                {
                    msg.Kind = AnnotationKind.Perm;
                    if (!root.TryGetProperty("on", out var onProp) ||
                        (onProp.ValueKind != JsonValueKind.True && onProp.ValueKind != JsonValueKind.False))
                        return false;
                    msg.PermOn = onProp.GetBoolean();

                    if (root.TryGetProperty("fade", out var fadeProp) && fadeProp.TryGetInt32(out int fadeVal))
                    {
                        if (fadeVal < 0 || fadeVal > 3600) return false;
                        msg.FadeSeconds = fadeVal;
                    }
                    else
                    {
                        msg.FadeSeconds = 8;
                    }

                    if (root.TryGetProperty("inv", out var invProp) &&
                        (invProp.ValueKind == JsonValueKind.True || invProp.ValueKind == JsonValueKind.False))
                    {
                        msg.InVideo = invProp.GetBoolean();
                    }
                    else
                    {
                        msg.InVideo = false;
                    }
                    return true;
                }

                case "ANN_BEGIN":
                {
                    msg.Kind = AnnotationKind.Begin;
                    if (!root.TryGetProperty("sid", out var sidProp) || !sidProp.TryGetInt32(out int sid) || sid <= 0)
                        return false;
                    msg.StrokeId = sid;

                    if (!root.TryGetProperty("ci", out var ciProp) || !ciProp.TryGetInt32(out int ci) || ci < 0 || ci > 7)
                        return false;
                    msg.ColorIndex = ci;

                    if (!root.TryGetProperty("w", out var wProp) || !wProp.TryGetInt32(out int w) || w < 1 || w > 20)
                        return false;
                    msg.Width = w;

                    if (!TryParsePoints(root, out var points))
                        return false;
                    msg.Points = points;
                    return true;
                }

                case "ANN_PTS":
                {
                    msg.Kind = AnnotationKind.Points;
                    if (!root.TryGetProperty("sid", out var sidProp) || !sidProp.TryGetInt32(out int sid) || sid <= 0)
                        return false;
                    msg.StrokeId = sid;

                    if (!TryParsePoints(root, out var points))
                        return false;
                    msg.Points = points;
                    return true;
                }

                case "ANN_END":
                {
                    msg.Kind = AnnotationKind.End;
                    if (!root.TryGetProperty("sid", out var sidProp) || !sidProp.TryGetInt32(out int sid) || sid <= 0)
                        return false;
                    msg.StrokeId = sid;
                    return true;
                }

                case "ANN_CLEAR":
                {
                    msg.Kind = AnnotationKind.Clear;
                    if (!root.TryGetProperty("scope", out var scProp) || scProp.ValueKind != JsonValueKind.String)
                        return false;

                    string? scope = scProp.GetString();
                    if (scope == "all")
                    {
                        msg.ClearAll = true;
                        return true;
                    }
                    if (scope == "mine")
                    {
                        msg.ClearAll = false;
                        return true;
                    }
                    return false;
                }

                case "ANN_XFORM":
                {
                    msg.Kind = AnnotationKind.Xform;
                    if (!root.TryGetProperty("f", out var fProp) || !fProp.TryGetUInt32(out uint fVal))
                        return false;
                    msg.FrameId = fVal;

                    if (!root.TryGetProperty("x", out var xProp) || xProp.ValueKind != JsonValueKind.Array)
                        return false;

                    int count = xProp.GetArrayLength();
                    if (count < 1 || count > 32)
                        return false;

                    var list = new List<AnnotationTransformItem>(count);
                    foreach (var elem in xProp.EnumerateArray())
                    {
                        if (!elem.TryGetProperty("o", out var elemO) || elemO.ValueKind != JsonValueKind.String)
                            return false;
                        string oStr = elemO.GetString() ?? "";
                        if (string.IsNullOrEmpty(oStr) || oStr.Length > 128)
                            return false;

                        if (!elem.TryGetProperty("sid", out var elemSid) || !elemSid.TryGetInt32(out int sid) || sid <= 0)
                            return false;

                        if (!elem.TryGetProperty("du", out var elemDu) || !elemDu.TryGetInt32(out int du) || du < -Scale || du > Scale)
                            return false;

                        if (!elem.TryGetProperty("dv", out var elemDv) || !elemDv.TryGetInt32(out int dv) || dv < -Scale || dv > Scale)
                            return false;

                        if (!elem.TryGetProperty("h", out var elemH) || !elemH.TryGetInt32(out int h) || (h != 0 && h != 1))
                            return false;

                        list.Add(new AnnotationTransformItem
                        {
                            OwnerKey = oStr,
                            StrokeId = sid,
                            DeltaU = du,
                            DeltaV = dv,
                            Hidden = h
                        });
                    }
                    msg.Transforms = list;
                    return true;
                }

                default:
                    // Bilinmeyen ANN_* alt tipleri sessizce yoksayılır (ileri uyumluluk)
                    return false;
            }
        }

        private static bool TryParsePoints(JsonElement root, out int[] points)
        {
            points = Array.Empty<int>();
            if (!root.TryGetProperty("p", out var pProp) || pProp.ValueKind != JsonValueKind.Array)
                return false;

            int len = pProp.GetArrayLength();
            // p uzunluğu çift ve 2..128 arası (1 ila 64 koordinat çifti)
            if (len < 2 || len > 128 || (len % 2) != 0)
                return false;

            var arr = new int[len];
            int idx = 0;
            foreach (var elem in pProp.EnumerateArray())
            {
                if (!elem.TryGetInt32(out int val))
                    return false;
                // Değerler normalize koordinat sınırında [0, 10000] olmalı, aralık dışıysa mesaj atılır
                if (val < 0 || val > Scale)
                    return false;
                arr[idx++] = val;
            }
            points = arr;
            return true;
        }

        /// <summary>
        /// SendControlJson'a verilecek nesneyi oluşturur.
        /// </summary>
        public static object ToWire(AnnotationMessage msg, bool includeOrigin)
        {
            switch (msg.Kind)
            {
                case AnnotationKind.Perm:
                    return new
                    {
                        t = "ANN_PERM",
                        on = msg.PermOn,
                        fade = msg.FadeSeconds,
                        inv = msg.InVideo
                    };

                case AnnotationKind.Begin:
                    if (includeOrigin && !string.IsNullOrEmpty(msg.OriginKey))
                    {
                        return new
                        {
                            t = "ANN_BEGIN",
                            sid = msg.StrokeId,
                            ci = msg.ColorIndex,
                            w = msg.Width,
                            p = msg.Points ?? Array.Empty<int>(),
                            o = msg.OriginKey,
                            n = msg.OriginName ?? ""
                        };
                    }
                    return new
                    {
                        t = "ANN_BEGIN",
                        sid = msg.StrokeId,
                        ci = msg.ColorIndex,
                        w = msg.Width,
                        p = msg.Points ?? Array.Empty<int>()
                    };

                case AnnotationKind.Points:
                    if (includeOrigin && !string.IsNullOrEmpty(msg.OriginKey))
                    {
                        return new
                        {
                            t = "ANN_PTS",
                            sid = msg.StrokeId,
                            p = msg.Points ?? Array.Empty<int>(),
                            o = msg.OriginKey
                        };
                    }
                    return new
                    {
                        t = "ANN_PTS",
                        sid = msg.StrokeId,
                        p = msg.Points ?? Array.Empty<int>()
                    };

                case AnnotationKind.End:
                    if (includeOrigin && !string.IsNullOrEmpty(msg.OriginKey))
                    {
                        return new
                        {
                            t = "ANN_END",
                            sid = msg.StrokeId,
                            o = msg.OriginKey
                        };
                    }
                    return new
                    {
                        t = "ANN_END",
                        sid = msg.StrokeId
                    };

                case AnnotationKind.Clear:
                    if (includeOrigin && !string.IsNullOrEmpty(msg.OriginKey))
                    {
                        return new
                        {
                            t = "ANN_CLEAR",
                            scope = msg.ClearAll ? "all" : "mine",
                            o = msg.OriginKey
                        };
                    }
                    return new
                    {
                        t = "ANN_CLEAR",
                        scope = msg.ClearAll ? "all" : "mine"
                    };

                case AnnotationKind.Xform:
                    return new
                    {
                        t = "ANN_XFORM",
                        f = msg.FrameId,
                        x = msg.Transforms?.Select(item => new
                        {
                            o = item.OwnerKey,
                            sid = item.StrokeId,
                            du = item.DeltaU,
                            dv = item.DeltaV,
                            h = item.Hidden
                        }).ToArray() ?? Array.Empty<object>()
                    };

                default:
                    return new { t = "ANN_UNKNOWN" };
            }
        }

#if DEBUG
        /// <summary>
        /// Faz 1 geçici doğrulama testi: Protokol ayrıştırma ve sınır kontrollerini test eder.
        /// </summary>
        public static bool RunSelfCheck()
        {
            void Assert(bool condition, string testName)
            {
                if (!condition)
                    throw new InvalidOperationException($"Annotation SelfCheck FAILED: {testName}");
            }

            // 1. Valid ANN_PERM
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_PERM\",\"on\":true,\"fade\":8,\"inv\":false}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_PERM parse");
                Assert(msg.Kind == AnnotationKind.Perm && msg.PermOn && msg.FadeSeconds == 8 && !msg.InVideo, "ANN_PERM values");
            }

            // 2. Valid ANN_BEGIN
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":17,\"ci\":2,\"w\":5,\"p\":[4120,3310,4135,3322]}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_BEGIN parse");
                Assert(msg.Kind == AnnotationKind.Begin && msg.StrokeId == 17 && msg.ColorIndex == 2 && msg.Width == 5, "ANN_BEGIN values");
                Assert(msg.Points != null && msg.Points.Length == 4, "ANN_BEGIN points length");
            }

            // 3. Valid ANN_BEGIN with relay origin
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":17,\"ci\":2,\"w\":5,\"p\":[4120,3310,4135,3322],\"o\":\"ali__a1b2c3d4e5\",\"n\":\"Ali\"}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_BEGIN relay parse");
                Assert(msg.OriginKey == "ali__a1b2c3d4e5" && msg.OriginName == "Ali", "ANN_BEGIN relay origin");
            }

            // 4. Valid ANN_PTS
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_PTS\",\"sid\":17,\"p\":[4150,3340,4171,3361,4199,3380]}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_PTS parse");
                Assert(msg.Kind == AnnotationKind.Points && msg.StrokeId == 17 && msg.Points?.Length == 6, "ANN_PTS values");
            }

            // 5. Valid ANN_END
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_END\",\"sid\":17}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_END parse");
                Assert(msg.Kind == AnnotationKind.End && msg.StrokeId == 17, "ANN_END values");
            }

            // 6. Valid ANN_CLEAR mine / all
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_CLEAR\",\"scope\":\"mine\"}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_CLEAR mine parse");
                Assert(msg.Kind == AnnotationKind.Clear && !msg.ClearAll, "ANN_CLEAR mine value");
            }
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_CLEAR\",\"scope\":\"all\"}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_CLEAR all parse");
                Assert(msg.Kind == AnnotationKind.Clear && msg.ClearAll, "ANN_CLEAR all value");
            }

            // 7. Invalid checks
            // ci > 7
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":8,\"w\":5,\"p\":[100,200]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject ci > 7");

            // ci < 0
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":-1,\"w\":5,\"p\":[100,200]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject ci < 0");

            // w < 1 or w > 20
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":2,\"w\":0,\"p\":[100,200]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject w < 1");
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":2,\"w\":21,\"p\":[100,200]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject w > 20");

            // p odd count
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":2,\"w\":5,\"p\":[100,200,300]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject odd p length");

            // p empty
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":2,\"w\":5,\"p\":[]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject empty p");

            // p > 10000
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":2,\"w\":5,\"p\":[100,10001]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject p > 10000");

            // p < 0
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":1,\"ci\":2,\"w\":5,\"p\":[-1,200]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject p < 0");

            // sid <= 0
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_BEGIN\",\"sid\":0,\"ci\":2,\"w\":5,\"p\":[100,200]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject sid <= 0");

            // invalid scope
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_CLEAR\",\"scope\":\"other\"}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject invalid clear scope");

            // 7b. Valid ANN_XFORM
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_XFORM\",\"f\":12345,\"x\":[{\"o\":\"ali__a1b2c3\",\"sid\":17,\"du\":0,\"dv\":-1834,\"h\":0}]}"))
            {
                Assert(TryParse(doc.RootElement, out var msg), "Valid ANN_XFORM parse");
                Assert(msg.Kind == AnnotationKind.Xform && msg.FrameId == 12345 && msg.Transforms?.Count == 1, "ANN_XFORM values");
                Assert(msg.Transforms![0].OwnerKey == "ali__a1b2c3" && msg.Transforms[0].StrokeId == 17 && msg.Transforms[0].DeltaV == -1834 && msg.Transforms[0].Hidden == 0, "ANN_XFORM transform item");
            }

            // 7c. Invalid ANN_XFORM checks
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_XFORM\",\"f\":12345,\"x\":[{\"o\":\"user1\",\"sid\":1,\"du\":0,\"dv\":0,\"h\":2}]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject ANN_XFORM with h=2");
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_XFORM\",\"f\":12345,\"x\":[{\"o\":\"user1\",\"sid\":1,\"du\":10001,\"dv\":0,\"h\":0}]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject ANN_XFORM with du > 10000");
            using (var doc = JsonDocument.Parse("{\"t\":\"ANN_XFORM\",\"f\":12345,\"x\":[{\"o\":\"user1\",\"sid\":0,\"du\":0,\"dv\":0,\"h\":0}]}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject ANN_XFORM with sid <= 0");

            // non-ANN
            using (var doc = JsonDocument.Parse("{\"t\":\"STREAM_START\",\"title\":\"test\"}"))
                Assert(!TryParse(doc.RootElement, out _), "Reject non-ANN");

            // Store checks: 8 open strokes limit per owner
            var store = new AnnotationStore();
            for (int i = 1; i <= 8; i++)
            {
                store.Apply("user1", "User 1", new AnnotationMessage
                {
                    Kind = AnnotationKind.Begin,
                    StrokeId = i,
                    ColorIndex = 1,
                    Width = 5,
                    Points = new[] { 100, 100, 200, 200 }
                });
            }
            Assert(store.Snapshot().Count == 8, "Store has 8 strokes");

            // 9th stroke should be rejected
            store.Apply("user1", "User 1", new AnnotationMessage
            {
                Kind = AnnotationKind.Begin,
                StrokeId = 9,
                ColorIndex = 1,
                Width = 5,
                Points = new[] { 100, 100, 200, 200 }
            });
            Assert(store.Snapshot().Count == 8, "9th open stroke rejected");

            System.Diagnostics.Debug.WriteLine("[AnnotationProtocol] Self-check PASSED successfully!");
            return true;
        }
#endif
    }

    public enum AnnotationKind
    {
        Perm,
        Begin,
        Points,
        End,
        Clear,
        Xform
    }

    /// <summary>
    /// ANN_XFORM mesajı içindeki tek bir çizgi kayma ve görünürlük öğesi.
    /// </summary>
    public sealed class AnnotationTransformItem
    {
        public string OwnerKey { get; set; } = "";
        public int StrokeId { get; set; }
        public int DeltaU { get; set; }
        public int DeltaV { get; set; }
        public int Hidden { get; set; } // 0: görünür, 1: gizli
    }

    /// <summary>
    /// Ayrıştırılmış ve doğrulanmış çizim mesajı.
    /// </summary>
    public sealed class AnnotationMessage
    {
        public AnnotationKind Kind { get; set; }
        public int StrokeId { get; set; }
        public int ColorIndex { get; set; }
        public int Width { get; set; }
        public int[]? Points { get; set; }
        public bool ClearAll { get; set; }
        public bool PermOn { get; set; }
        public int FadeSeconds { get; set; }
        public bool InVideo { get; set; }
        public string? OriginKey { get; set; }
        public string? OriginName { get; set; }

        // Görev 1: ANN_XFORM alanları
        public uint FrameId { get; set; }
        public List<AnnotationTransformItem>? Transforms { get; set; }
    }

    /// <summary>
    /// Tek bir çizgi nesnesi.
    /// </summary>
    public sealed class AnnotationStroke
    {
        public string OwnerKey = "";
        public string OwnerName = "";
        public int StrokeId;
        public int ColorIndex;
        public int Width;
        public List<int> Points = new(); // düz [u0, v0, u1, v1, ...] dizisi
        public bool Ended;
        public long LastUpdateTick;
        public long EndedTick;
        public double Opacity = 1.0;

        // Görev 1: Çizim Takibi (Content Tracking)
        public int OffsetU; // Normalize kayma (-10000..10000)
        public int OffsetV; // Normalize kayma (-10000..10000)
        public bool Hidden; // Görüş alanından çıktığında veya takip yitirildiğinde true
        public uint AnchorFrameId; // Takibin başladığı referans kare kimliği
    }

    /// <summary>
    /// Eş başına hız sınırı denetleyicisi (kayan 1 sn pencere: en fazla 900 nokta/sn ve 20 çizgi/sn).
    /// </summary>
    public sealed class AnnotationRateLimiter
    {
        private readonly object _lock = new();
        private readonly Queue<long> _strokeTimestamps = new();
        private readonly Queue<(long timestamp, int count)> _pointCounts = new();
        private int _pointsInWindow = 0;

        public bool CheckAndRecord(AnnotationKind kind, int pointCount)
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                long windowStart = now - 1000;

                while (_strokeTimestamps.Count > 0 && _strokeTimestamps.Peek() < windowStart)
                {
                    _strokeTimestamps.Dequeue();
                }

                while (_pointCounts.Count > 0 && _pointCounts.Peek().timestamp < windowStart)
                {
                    var old = _pointCounts.Dequeue();
                    _pointsInWindow -= old.count;
                }

                if (kind == AnnotationKind.Begin)
                {
                    if (_strokeTimestamps.Count >= 20)
                        return false;
                    _strokeTimestamps.Enqueue(now);
                }

                if (pointCount > 0)
                {
                    if (_pointsInWindow + pointCount > 900)
                        return false;
                    _pointCounts.Enqueue((now, pointCount));
                    _pointsInWindow += pointCount;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Thread-safe çizgi deposu (lock ile). Sınırları ve solmayı uygular.
    /// </summary>
    public sealed class AnnotationStore
    {
        private readonly object _lock = new();
        private readonly List<AnnotationStroke> _strokes = new();

        public event Action? Changed;
        public event Action<AnnotationStroke>? StrokeEnded;
        public event Action<string, int>? StrokeRemoved;

        public void Apply(string ownerKey, string ownerName, AnnotationMessage m)
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;

                switch (m.Kind)
                {
                    case AnnotationKind.Begin:
                    {
                        // Eş başına en fazla 8 açık (bitmemiş) çizgi
                        int openCount = 0;
                        for (int i = 0; i < _strokes.Count; i++)
                        {
                            if (_strokes[i].OwnerKey == ownerKey && !_strokes[i].Ended)
                                openCount++;
                        }
                        if (openCount >= 8)
                            return;

                        // Toplam en fazla 300 çizgi (en eskisi silinir)
                        while (_strokes.Count >= 300)
                        {
                            _strokes.RemoveAt(0);
                        }

                        var stroke = new AnnotationStroke
                        {
                            OwnerKey = ownerKey,
                            OwnerName = ownerName,
                            StrokeId = m.StrokeId,
                            ColorIndex = m.ColorIndex,
                            Width = m.Width,
                            Ended = false,
                            LastUpdateTick = now,
                            EndedTick = 0,
                            Opacity = 1.0
                        };

                        if (m.Points != null && m.Points.Length > 0)
                        {
                            // Çizgi başına en fazla 4000 nokta (8000 tamsayı)
                            int toAdd = Math.Min(m.Points.Length, 8000) & ~1;
                            for (int i = 0; i < toAdd; i++)
                                stroke.Points.Add(m.Points[i]);
                        }

                        _strokes.Add(stroke);
                        Changed?.Invoke();
                        break;
                    }

                    case AnnotationKind.Points:
                    {
                        var stroke = _strokes.LastOrDefault(s => s.OwnerKey == ownerKey && s.StrokeId == m.StrokeId && !s.Ended);
                        if (stroke != null && m.Points != null && m.Points.Length > 0)
                        {
                            int available = 8000 - stroke.Points.Count;
                            if (available > 0)
                            {
                                int toAdd = Math.Min(m.Points.Length, available) & ~1;
                                for (int i = 0; i < toAdd; i++)
                                    stroke.Points.Add(m.Points[i]);
                            }
                            stroke.LastUpdateTick = now;
                            Changed?.Invoke();
                        }
                        break;
                    }

                    case AnnotationKind.End:
                    {
                        var stroke = _strokes.LastOrDefault(s => s.OwnerKey == ownerKey && s.StrokeId == m.StrokeId && !s.Ended);
                        if (stroke != null)
                        {
                            stroke.Ended = true;
                            stroke.EndedTick = now;
                            stroke.LastUpdateTick = now;
                            Changed?.Invoke();
                            StrokeEnded?.Invoke(stroke);
                        }
                        break;
                    }

                    case AnnotationKind.Clear:
                    {
                        if (m.ClearAll)
                        {
                            ClearAllInternal();
                        }
                        else
                        {
                            ClearOwnerInternal(ownerKey);
                        }
                        break;
                    }
                }
            }
        }

        public void ClearOwner(string ownerKey)
        {
            lock (_lock)
            {
                ClearOwnerInternal(ownerKey);
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                ClearAllInternal();
            }
        }

        private void ClearOwnerInternal(string ownerKey)
        {
            var toRemove = _strokes.Where(s => s.OwnerKey == ownerKey).ToList();
            int removed = _strokes.RemoveAll(s => s.OwnerKey == ownerKey);
            if (removed > 0)
            {
                foreach (var s in toRemove) StrokeRemoved?.Invoke(s.OwnerKey, s.StrokeId);
                Changed?.Invoke();
            }
        }

        private void ClearAllInternal()
        {
            if (_strokes.Count > 0)
            {
                var toRemove = _strokes.ToList();
                _strokes.Clear();
                foreach (var s in toRemove) StrokeRemoved?.Invoke(s.OwnerKey, s.StrokeId);
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Solma ve kendiliğinden bitirme güncellemesi. Değişiklik varsa true döner.
        /// </summary>
        public bool Tick(long nowTick, int fadeSeconds)
        {
            lock (_lock)
            {
                bool changed = false;

                for (int i = _strokes.Count - 1; i >= 0; i--)
                {
                    var stroke = _strokes[i];

                    // 1) END gelmeyen çizgi, son noktadan 5 sn sonra kendiliğinden bitmiş sayılır
                    if (!stroke.Ended && (nowTick - stroke.LastUpdateTick >= 5000))
                    {
                        stroke.Ended = true;
                        stroke.EndedTick = nowTick;
                        changed = true;
                        StrokeEnded?.Invoke(stroke);
                    }

                    // 2) Solma: fadeSeconds > 0 ise EndedTick + fadeSeconds*1000 sonrasında opaklık 600 ms'de 1 -> 0 iner
                    if (stroke.Ended && fadeSeconds > 0)
                    {
                        long fadeStart = stroke.EndedTick + ((long)fadeSeconds * 1000L);
                        if (nowTick >= fadeStart)
                        {
                            long elapsed = nowTick - fadeStart;
                            if (elapsed >= 600)
                            {
                                var rem = _strokes[i];
                                _strokes.RemoveAt(i);
                                changed = true;
                                StrokeRemoved?.Invoke(rem.OwnerKey, rem.StrokeId);
                                continue;
                            }

                            double newOpacity = 1.0 - ((double)elapsed / 600.0);
                            if (newOpacity < 0.0) newOpacity = 0.0;
                            if (Math.Abs(stroke.Opacity - newOpacity) > 0.02)
                            {
                                stroke.Opacity = newOpacity;
                                changed = true;
                            }
                        }
                    }
                }

                if (changed)
                {
                    Changed?.Invoke();
                }

                return changed;
            }
        }

        public void ApplyTransforms(List<AnnotationTransformItem>? transforms)
        {
            if (transforms == null || transforms.Count == 0) return;
            bool changed = false;
            lock (_lock)
            {
                foreach (var item in transforms)
                {
                    var stroke = _strokes.FirstOrDefault(s => s.OwnerKey == item.OwnerKey && s.StrokeId == item.StrokeId);
                    if (stroke != null)
                    {
                        bool newHidden = item.Hidden != 0;
                        if (stroke.OffsetU != item.DeltaU || stroke.OffsetV != item.DeltaV || stroke.Hidden != newHidden)
                        {
                            stroke.OffsetU = item.DeltaU;
                            stroke.OffsetV = item.DeltaV;
                            stroke.Hidden = newHidden;
                            changed = true;
                        }
                    }
                }
            }
            if (changed)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Çizim için thread-safe kopya döndürür.
        /// </summary>
        public IReadOnlyList<AnnotationStroke> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<AnnotationStroke>(_strokes.Count);
                for (int i = 0; i < _strokes.Count; i++)
                {
                    var s = _strokes[i];
                    list.Add(new AnnotationStroke
                    {
                        OwnerKey = s.OwnerKey,
                        OwnerName = s.OwnerName,
                        StrokeId = s.StrokeId,
                        ColorIndex = s.ColorIndex,
                        Width = s.Width,
                        Points = new List<int>(s.Points),
                        Ended = s.Ended,
                        LastUpdateTick = s.LastUpdateTick,
                        EndedTick = s.EndedTick,
                        Opacity = s.Opacity,
                        OffsetU = s.OffsetU,
                        OffsetV = s.OffsetV,
                        Hidden = s.Hidden,
                        AnchorFrameId = s.AnchorFrameId
                    });
                }
                return list;
            }
        }
    }
}
