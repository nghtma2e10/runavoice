using System;
using System.Collections.Generic;
using System.Linq;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// İçerik Kaydırma Takipçisi (Görev 1):
    /// - Yayıncının ham ekran takip karelerini (480px gri) dinler.
    /// - ANN_END alan bitmiş çizgilerin etrafından gri şablon çıkarır.
    /// - Kaba-ince SAD (Sum of Absolute Differences) ile içeriğin kaymasını takip eder.
    /// - Statik bölge titreme engelleme, düşük varyans sütun bandı yedeği ve görüş dışı (Hidden) yönetimi uygular.
    /// - En fazla 24 çizgi, en fazla 10 Hz ANN_XFORM yayını (du/dv >= 20 veya h değiştiğinde).
    /// </summary>
    public sealed class AnnotationContentTracker
    {
        private sealed class TrackedStroke
        {
            public string OwnerKey = "";
            public int StrokeId;
            public uint AnchorFrameId;

            public int TemplateW;
            public int TemplateH;
            public byte[] Template = Array.Empty<byte>();

            public int AnchorX;
            public int AnchorY;
            public int CurrentX;
            public int CurrentY;

            public int NormMinU;
            public int NormMinV;
            public int NormMaxU;
            public int NormMaxV;

            public int CurrentOffsetU;
            public int CurrentOffsetV;
            public int LastSentOffsetU;
            public int LastSentOffsetV;

            public bool IsHidden;
            public bool LastSentHidden;

            public long LastGoodMatchTick;
            public long LastHiddenSearchTick;
            public bool IsLowVariance;
            public long RegisteredTick;
        }

        private readonly object _lock = new();
        private readonly List<TrackedStroke> _trackedStrokes = new();
        private readonly WebRtcService _webrtc;
        private readonly AnnotationStore _broadcasterStore;
        private readonly Action? _onLocalTransformApplied;

        private byte[]? _lastFrameGray;
        private int _lastFrameW;
        private int _lastFrameH;
        private uint _lastFrameId;

        private long _lastBroadcastTick = 0;
        private const int MaxTrackedStrokes = 24;

        public AnnotationContentTracker(
            WebRtcService webrtc,
            AnnotationStore broadcasterStore,
            Action? onLocalTransformApplied = null)
        {
            _webrtc = webrtc ?? throw new ArgumentNullException(nameof(webrtc));
            _broadcasterStore = broadcasterStore ?? throw new ArgumentNullException(nameof(broadcasterStore));
            _onLocalTransformApplied = onLocalTransformApplied;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _trackedStrokes.Clear();
                _lastFrameGray = null;
                _lastFrameW = 0;
                _lastFrameH = 0;
                _lastFrameId = 0;
            }
        }

        public void RemoveStroke(string ownerKey, int strokeId)
        {
            lock (_lock)
            {
                _trackedStrokes.RemoveAll(s => s.OwnerKey == ownerKey && s.StrokeId == strokeId);
            }
        }

        public void OnStrokeEnded(AnnotationStroke stroke)
        {
            if (stroke == null || stroke.Points == null || stroke.Points.Count < 2) return;

            lock (_lock)
            {
                // Mevcut kare hazır değilse veya takip karesi yoksa kaydedilemez
                if (_lastFrameGray == null || _lastFrameW <= 0 || _lastFrameH <= 0) return;

                // Zaten takip ediliyorsa tekrar ekleme
                if (_trackedStrokes.Any(s => s.OwnerKey == stroke.OwnerKey && s.StrokeId == stroke.StrokeId))
                    return;

                // Sınır kutusu hesapla (0..10000 normalize)
                int minU = 10000, maxU = 0, minV = 10000, maxV = 0;
                int count = stroke.Points.Count;
                for (int i = 0; i < count; i += 2)
                {
                    int u = stroke.Points[i];
                    int v = stroke.Points[i + 1];
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }

                int trackW = _lastFrameW;
                int trackH = _lastFrameH;

                int px0 = (int)Math.Round((double)minU * trackW / 10000.0);
                int px1 = (int)Math.Round((double)maxU * trackW / 10000.0);
                int py0 = (int)Math.Round((double)minV * trackH / 10000.0);
                int py1 = (int)Math.Round((double)maxV * trackH / 10000.0);

                int bw = Math.Max(1, px1 - px0);
                int bh = Math.Max(1, py1 - py0);

                // Kenarlara %20 pay ekle
                int padX = (int)Math.Max(4, bw * 0.20);
                int padY = (int)Math.Max(4, bh * 0.20);

                int tx0 = Math.Max(0, px0 - padX);
                int ty0 = Math.Max(0, py0 - padY);
                int tw = Math.Clamp(bw + (2 * padX), 48, 160);
                int th = Math.Clamp(bh + (2 * padY), 48, 160);

                if (tx0 + tw > trackW) tx0 = Math.Max(0, trackW - tw);
                if (ty0 + th > trackH) ty0 = Math.Max(0, trackH - th);

                // Şablon piksellerini kopyala
                byte[] template = new byte[tw * th];
                long sum = 0;
                long sumSq = 0;
                for (int y = 0; y < th; y++)
                {
                    int srcOff = ((ty0 + y) * trackW) + tx0;
                    int dstOff = y * tw;
                    Buffer.BlockCopy(_lastFrameGray, srcOff, template, dstOff, tw);
                    for (int x = 0; x < tw; x++)
                    {
                        byte val = template[dstOff + x];
                        sum += val;
                        sumSq += (val * val);
                    }
                }

                // Varyans analizi
                int totalPix = tw * th;
                double mean = (double)sum / totalPix;
                double variance = ((double)sumSq / totalPix) - (mean * mean);
                bool isLowVariance = variance < 15.0; // Düz renk veya tekdüze zemin

                stroke.AnchorFrameId = _lastFrameId;

                var tracked = new TrackedStroke
                {
                    OwnerKey = stroke.OwnerKey,
                    StrokeId = stroke.StrokeId,
                    AnchorFrameId = _lastFrameId,
                    TemplateW = tw,
                    TemplateH = th,
                    Template = template,
                    AnchorX = tx0,
                    AnchorY = ty0,
                    CurrentX = tx0,
                    CurrentY = ty0,
                    NormMinU = minU,
                    NormMinV = minV,
                    NormMaxU = maxU,
                    NormMaxV = maxV,
                    CurrentOffsetU = 0,
                    CurrentOffsetV = 0,
                    LastSentOffsetU = 0,
                    LastSentOffsetV = 0,
                    IsHidden = false,
                    LastSentHidden = false,
                    LastGoodMatchTick = Environment.TickCount64,
                    LastHiddenSearchTick = 0,
                    IsLowVariance = isLowVariance,
                    RegisteredTick = Environment.TickCount64
                };

                // En fazla 24 çizgi: fazlaysa en eskileri çıkar
                while (_trackedStrokes.Count >= MaxTrackedStrokes)
                {
                    _trackedStrokes.RemoveAt(0);
                }

                _trackedStrokes.Add(tracked);
            }
        }

        public void ProcessTrackingFrame(byte[] gray, int width, int height, uint frameId)
        {
            if (gray == null || width <= 0 || height <= 0) return;

            long now = Environment.TickCount64;

            lock (_lock)
            {
                // Çerçeve tamponunu sakla (yeni çizgiler şablon alabilsin)
                int required = width * height;
                if (_lastFrameGray == null || _lastFrameGray.Length != required)
                {
                    _lastFrameGray = new byte[required];
                }
                Buffer.BlockCopy(gray, 0, _lastFrameGray, 0, required);
                _lastFrameW = width;
                _lastFrameH = height;
                _lastFrameId = frameId;

                if (_trackedStrokes.Count == 0) return;

                // Takip edilen her çizgiyi ara
                for (int i = 0; i < _trackedStrokes.Count; i++)
                {
                    var ts = _trackedStrokes[i];
                    TrackSingleStroke(ts, gray, width, height, now);
                }

                // 10 Hz hız sınırıyla ANN_XFORM yayınla
                if (now - _lastBroadcastTick >= 100)
                {
                    _lastBroadcastTick = now;
                    BroadcastTransforms(frameId);
                }
            }
        }

        private void TrackSingleStroke(TrackedStroke ts, byte[] gray, int w, int h, long now)
        {
            int tw = ts.TemplateW;
            int th = ts.TemplateH;

            // Gizli çizgiler için 500 ms aralıkla geniş arama
            if (ts.IsHidden)
            {
                if (now - ts.LastHiddenSearchTick < 500)
                    return;
                ts.LastHiddenSearchTick = now;
            }

            int curX = ts.CurrentX;
            int curY = ts.CurrentY;

            // Arama aralığı: dikeyde ±%45 yükseklik, yatayda ±%15 genişlik (gizlide tüm yükseklik)
            int maxDy = ts.IsHidden ? (h - th) : Math.Max(10, (int)Math.Round(h * 0.45));
            int maxDx = ts.IsHidden ? Math.Max(20, (int)Math.Round(w * 0.25)) : Math.Max(8, (int)Math.Round(w * 0.15));

            int minSearchX = Math.Max(0, curX - maxDx);
            int maxSearchX = Math.Min(w - tw, curX + maxDx);
            int minSearchY = Math.Max(0, curY - maxDy);
            int maxSearchY = Math.Min(h - th, curY + maxDy);

            if (minSearchX > maxSearchX || minSearchY > maxSearchY) return;

            long bestSad = long.MaxValue;
            int bestX = curX;
            int bestY = curY;
            long sumSad = 0;
            int candidateCount = 0;

            // Kaba arama: Adım = 2
            for (int sy = minSearchY; sy <= maxSearchY; sy += 2)
            {
                for (int sx = minSearchX; sx <= maxSearchX; sx += 2)
                {
                    long sad = ComputeSad(gray, w, sx, sy, ts.Template, tw, th);
                    sumSad += sad;
                    candidateCount++;

                    if (sad < bestSad)
                    {
                        bestSad = sad;
                        bestX = sx;
                        bestY = sy;
                    }
                }
            }

            // İnce arama: bulunan en iyinin ±3 piksel çevresinde Adım = 1
            int fineMinX = Math.Max(minSearchX, bestX - 3);
            int fineMaxX = Math.Min(maxSearchX, bestX + 3);
            int fineMinY = Math.Max(minSearchY, bestY - 3);
            int fineMaxY = Math.Min(maxSearchY, bestY + 3);

            for (int sy = fineMinY; sy <= fineMaxY; sy++)
            {
                for (int sx = fineMinX; sx <= fineMaxX; sx++)
                {
                    long sad = ComputeSad(gray, w, sx, sy, ts.Template, tw, th);
                    if (sad < bestSad)
                    {
                        bestSad = sad;
                        bestX = sx;
                        bestY = sy;
                    }
                }
            }

            // Güven kontrolü: En iyi SAD ortalama adaya göre belirgin düşük olmalı (< 0.6)
            double avgSad = candidateCount > 0 ? (double)sumSad / candidateCount : bestSad + 1;
            double confidenceRatio = avgSad > 0 ? (double)bestSad / avgSad : 1.0;
            long perPixelSad = bestSad / (tw * th);

            bool isConfident = !ts.IsLowVariance && (confidenceRatio < 0.60) && (perPixelSad < 45);

            // Statik Bölge Kontrolü: Çizgi sabit bir arayüzde ise (ofset 0 ile en iyi SAD çok yakınsa) kaydırma uygulama
            long anchorSad = ComputeSad(gray, w, ts.AnchorX, ts.AnchorY, ts.Template, tw, th);
            if (anchorSad <= bestSad * 1.05 || Math.Abs(bestSad - anchorSad) < (tw * th * 2))
            {
                bestX = ts.AnchorX;
                bestY = ts.AnchorY;
                isConfident = true;
            }

            // Küresel kaydırma yedeği: Şablon düşük varyanslı veya güvenilmezse sütun bandında satır imzası ile dikey kayma
            if (!isConfident && ts.IsLowVariance)
            {
                int bandX = ts.AnchorX;
                int bandW = ts.TemplateW;
                int estimatedDy = EstimateVerticalShiftFromColumnBand(gray, w, h, bandX, bandW, ts.AnchorY, ts.TemplateH, ts.Template);
                if (estimatedDy != int.MinValue)
                {
                    bestX = ts.AnchorX;
                    bestY = Math.Clamp(ts.AnchorY + estimatedDy, 0, h - th);
                    isConfident = true;
                }
            }

            if (isConfident)
            {
                ts.CurrentX = bestX;
                ts.CurrentY = bestY;
                ts.LastGoodMatchTick = now;

                // Normalize kayma ofseti hesapla (-10000..10000)
                int deltaPx = ts.CurrentX - ts.AnchorX;
                int deltaPy = ts.CurrentY - ts.AnchorY;

                ts.CurrentOffsetU = (int)Math.Round((double)deltaPx * 10000.0 / w);
                ts.CurrentOffsetV = (int)Math.Round((double)deltaPy * 10000.0 / h);

                // Sınır kutusu ekran dışına %60'tan fazla taştıysa Hidden yap
                int boxMinU = ts.NormMinU + ts.CurrentOffsetU;
                int boxMaxU = ts.NormMaxU + ts.CurrentOffsetU;
                int boxMinV = ts.NormMinV + ts.CurrentOffsetV;
                int boxMaxV = ts.NormMaxV + ts.CurrentOffsetV;

                int ovW = Math.Max(0, Math.Min(10000, boxMaxU) - Math.Max(0, boxMinU));
                int ovH = Math.Max(0, Math.Min(10000, boxMaxV) - Math.Max(0, boxMinV));
                int origW = Math.Max(1, ts.NormMaxU - ts.NormMinU);
                int origH = Math.Max(1, ts.NormMaxV - ts.NormMinV);

                double overlapRatio = (double)(ovW * ovH) / (origW * origH);
                if (overlapRatio < 0.40)
                {
                    ts.IsHidden = true;
                }
                else
                {
                    ts.IsHidden = false;
                }
            }
            else
            {
                // Art arda 1 sn güvenli eşleşme yoksa gizle
                if (now - ts.LastGoodMatchTick >= 1000)
                {
                    ts.IsHidden = true;
                }
            }
        }

        private static long ComputeSad(byte[] frame, int frameW, int x, int y, byte[] tmpl, int tw, int th)
        {
            long sad = 0;
            for (int r = 0; r < th; r++)
            {
                int frameRowOff = ((y + r) * frameW) + x;
                int tmplRowOff = r * tw;
                for (int c = 0; c < tw; c++)
                {
                    int diff = frame[frameRowOff + c] - tmpl[tmplRowOff + c];
                    sad += (diff >= 0 ? diff : -diff);
                }
            }
            return sad;
        }

        private static int EstimateVerticalShiftFromColumnBand(
            byte[] frame, int frameW, int frameH,
            int bandX, int bandW, int anchorY, int templateH, byte[] template)
        {
            // Şablondan satır toplamı profili
            long[] tmplProfile = new long[templateH];
            for (int r = 0; r < templateH; r++)
            {
                long rowSum = 0;
                int off = r * bandW;
                for (int c = 0; c < bandW; c++) rowSum += template[off + c];
                tmplProfile[r] = rowSum;
            }

            // Çerçevedeki sütun bandından aday arama (±0.4 kare yüksekliği)
            int maxDy = Math.Max(10, (int)Math.Round(frameH * 0.40));
            int minY = Math.Max(0, anchorY - maxDy);
            int maxY = Math.Min(frameH - templateH, anchorY + maxDy);

            long bestDiff = long.MaxValue;
            int bestDy = 0;

            for (int sy = minY; sy <= maxY; sy++)
            {
                long diff = 0;
                for (int r = 0; r < templateH; r++)
                {
                    long frameRowSum = 0;
                    int fOff = ((sy + r) * frameW) + bandX;
                    for (int c = 0; c < bandW; c++) frameRowSum += frame[fOff + c];
                    diff += Math.Abs(frameRowSum - tmplProfile[r]);
                }

                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestDy = sy - anchorY;
                }
            }

            return bestDiff < (templateH * bandW * 30) ? bestDy : int.MinValue;
        }

        private void BroadcastTransforms(uint frameId)
        {
            var changed = new List<AnnotationTransformItem>();

            for (int i = 0; i < _trackedStrokes.Count; i++)
            {
                var ts = _trackedStrokes[i];
                int diffU = Math.Abs(ts.CurrentOffsetU - ts.LastSentOffsetU);
                int diffV = Math.Abs(ts.CurrentOffsetV - ts.LastSentOffsetV);
                bool hiddenChanged = ts.IsHidden != ts.LastSentHidden;

                if (diffU >= 20 || diffV >= 20 || hiddenChanged)
                {
                    ts.LastSentOffsetU = ts.CurrentOffsetU;
                    ts.LastSentOffsetV = ts.CurrentOffsetV;
                    ts.LastSentHidden = ts.IsHidden;

                    changed.Add(new AnnotationTransformItem
                    {
                        OwnerKey = ts.OwnerKey,
                        StrokeId = ts.StrokeId,
                        DeltaU = ts.CurrentOffsetU,
                        DeltaV = ts.CurrentOffsetV,
                        Hidden = ts.IsHidden ? 1 : 0
                    });
                }
            }

            if (changed.Count == 0) return;

            // En fazla 32 öğeli parçalar halinde gönder
            const int batchSize = 32;
            for (int i = 0; i < changed.Count; i += batchSize)
            {
                var batch = changed.Skip(i).Take(batchSize).ToList();

                // 1. Ağ üzerinden tüm eşlere yayınla (çizimi çizen izleyici dahil!)
                var msg = new AnnotationMessage
                {
                    Kind = AnnotationKind.Xform,
                    FrameId = frameId,
                    Transforms = batch
                };
                object wire = AnnotationProtocol.ToWire(msg, includeOrigin: false);
                _webrtc.BroadcastAnnotation(wire);

                // 2. Yayıncının yerel deposuna da uygula
                _broadcasterStore.ApplyTransforms(batch);
            }

            _onLocalTransformApplied?.Invoke();
        }
    }
}
