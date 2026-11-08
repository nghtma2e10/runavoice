using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RunaVoice_WinUI.Models;
using Windows.System;

namespace RunaVoice_WinUI.Services
{
    public static class LinkPreviewHelper
    {
        private static readonly Regex UrlRegex = new Regex(
            @"(?:https?://|www\.)[^\s<>""']+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex YouTubeRegex = new Regex(
            @"(?:youtube\.com\/(?:watch\?.*v=|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MetaPropertyRegex = new Regex(
            @"<meta\s+[^>]*property=[""']([^""']*)[""'][^>]*content=[""']([^""']*)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MetaContentPropertyRegex = new Regex(
            @"<meta\s+[^>]*content=[""']([^""']*)[""'][^>]*property=[""']([^""']*)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MetaNameRegex = new Regex(
            @"<meta\s+[^>]*name=[""']([^""']*)[""'][^>]*content=[""']([^""']*)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TitleRegex = new Regex(
            @"<title[^>]*>(.*?)</title>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Lazy<HttpClient> SharedHttpClient = new Lazy<HttpClient>(() =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RunaVoice/2.0");
            client.Timeout = TimeSpan.FromSeconds(3);
            return client;
        });

        /// <summary>
        /// Metin içerisindeki ilk geçerli URL'yi yakalar ve normalize eder (www. -> https://www.).
        /// </summary>
        public static string? ExtractFirstUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = UrlRegex.Match(text);
            if (!match.Success) return null;

            string raw = match.Value;
            while (raw.Length > 0 && ",.!?);:'\"".IndexOf(raw[^1]) >= 0) raw = raw[..^1];
            if (string.IsNullOrWhiteSpace(raw)) return null;

            return raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + raw : raw;
        }

        /// <summary>
        /// Metin içerisindeki tüm URL alan adlarını (host) çıkarır.
        /// </summary>
        public static List<string> ExtractHostsFromText(string text)
        {
            var hosts = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return hosts;

            var matches = UrlRegex.Matches(text);
            foreach (Match m in matches)
            {
                string raw = m.Value;
                while (raw.Length > 0 && ",.!?);:'\"".IndexOf(raw[^1]) >= 0) raw = raw[..^1];
                if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw;

                if (Uri.TryCreate(raw, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                {
                    string h = u.Host.ToLowerInvariant();
                    if (!hosts.Contains(h)) hosts.Add(h);
                }
            }
            return hosts;
        }

        /// <summary>
        /// SSRF Koruması: Verilen host'un DNS çözünürlüğünü yapar;
        /// döngüsel (loopback) veya yerel/özel IP aralıklarındaysa (127/8, 10/8, 172.16/12, 192.168/16, 169.254/16, ::1, fc00::/7)
        /// kesinlikle reddeder.
        /// </summary>
        public static async Task<bool> IsSafePublicHostAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string lower = host.Trim().ToLowerInvariant();
            if (lower == "localhost" || lower.EndsWith(".localhost") || lower.EndsWith(".local") || lower.EndsWith(".internal"))
            {
                return false;
            }

            try
            {
                IPAddress[] ips = await Dns.GetHostAddressesAsync(host);
                if (ips == null || ips.Length == 0) return false;

                foreach (var ip in ips)
                {
                    if (IPAddress.IsLoopback(ip)) return false;

                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] b = ip.GetAddressBytes();
                        if (b[0] == 127) return false;                         // 127.0.0.0/8
                        if (b[0] == 10) return false;                          // 10.0.0.0/8
                        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16.0.0/12
                        if (b[0] == 192 && b[1] == 168) return false;          // 192.168.0.0/16
                        if (b[0] == 169 && b[1] == 254) return false;          // 169.254.0.0/16
                        if (b[0] == 0) return false;                            // 0.0.0.0/8
                        if (b[0] >= 224) return false;                          // Multicast / Reserved
                    }
                    else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6None)) return false;

                        byte[] b = ip.GetAddressBytes();
                        // fc00::/7 (Unique Local)
                        if ((b[0] & 0xFE) == 0xFC) return false;
                        // fe80::/10 (Link Local)
                        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;

                        if (ip.IsIPv4MappedToIPv6)
                        {
                            var v4 = ip.MapToIPv4();
                            byte[] b4 = v4.GetAddressBytes();
                            if (b4[0] == 127 || b4[0] == 10 || (b4[0] == 172 && b4[1] >= 16 && b4[1] <= 31) ||
                                (b4[0] == 192 && b4[1] == 168) || (b4[0] == 169 && b4[1] == 254) || b4[0] == 0 || b4[0] >= 224)
                                return false;
                        }
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gönderen tarafında önizleme üretimi: En fazla 1.5 saniye zaman aşımı uygulanır.
        /// Zaman aşımı veya hata durumunda mesajı geciktirmeden null döner.
        /// </summary>
        public static async Task<LinkPreviewData?> GeneratePreviewAsync(string text)
        {
            string? url = ExtractFirstUrl(text);
            if (string.IsNullOrEmpty(url)) return null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            using var cts = new CancellationTokenSource(1500);
            try
            {
                return await GeneratePreviewInternalAsync(uri, cts.Token);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<LinkPreviewData?> GeneratePreviewInternalAsync(Uri uri, CancellationToken ct)
        {
            if (!await IsSafePublicHostAsync(uri.Host)) return null;

            string url = uri.AbsoluteUri;
            var ytMatch = YouTubeRegex.Match(url);
            if (ytMatch.Success)
            {
                return await GenerateYouTubePreviewAsync(uri, ytMatch.Groups[1].Value, ct);
            }

            return await GenerateWebPreviewAsync(uri, ct);
        }

        private static async Task<LinkPreviewData?> GenerateYouTubePreviewAsync(Uri uri, string videoId, CancellationToken ct)
        {
            var preview = new LinkPreviewData
            {
                Url = uri.AbsoluteUri,
                SiteName = "YouTube",
                IsYouTube = true,
                Title = $"YouTube Videosu ({videoId})"
            };

            var client = SharedHttpClient.Value;

            // 1. oEmbed ile başlık ve yazar çek
            try
            {
                string oembedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(uri.AbsoluteUri)}&format=json";
                using var oembedResp = await client.GetAsync(oembedUrl, ct);
                if (oembedResp.IsSuccessStatusCode)
                {
                    string json = await oembedResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("title", out var tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
                    {
                        preview.Title = tEl.GetString()!.Trim();
                    }
                    if (root.TryGetProperty("author_name", out var aEl) && !string.IsNullOrWhiteSpace(aEl.GetString()))
                    {
                        preview.Description = aEl.GetString()!.Trim();
                    }
                }
            }
            catch { }

            // 2. Kapak resmi indir ve sıkıştır
            try
            {
                string thumbUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                var imgBytes = await DownloadBytesBoundedAsync(client, new Uri(thumbUrl), 2 * 1024 * 1024, ct);
                if (imgBytes == null || imgBytes.Length == 0)
                {
                    // Fallback to mqdefault
                    thumbUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                    imgBytes = await DownloadBytesBoundedAsync(client, new Uri(thumbUrl), 2 * 1024 * 1024, ct);
                }

                if (imgBytes != null && imgBytes.Length > 0)
                {
                    string? base64 = CompressImageToBase64(imgBytes, 320, 40960);
                    if (!string.IsNullOrEmpty(base64))
                    {
                        preview.ThumbJpegBase64 = base64;
                    }
                }
            }
            catch { }

            // Boyut sınırları
            if (preview.Title.Length > 200) preview.Title = preview.Title[..200];
            if (preview.Description.Length > 300) preview.Description = preview.Description[..300];
            return preview;
        }

        private static async Task<LinkPreviewData?> GenerateWebPreviewAsync(Uri uri, CancellationToken ct)
        {
            var client = SharedHttpClient.Value;

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (!mediaType.Contains("text/html") && !mediaType.Contains("application/xhtml+xml"))
            {
                return null;
            }

            // En fazla 256 KB indir
            byte[] htmlBytes;
            using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            {
                byte[] buf = new byte[256 * 1024];
                int totalRead = 0;
                while (totalRead < buf.Length)
                {
                    int r = await stream.ReadAsync(buf.AsMemory(totalRead, buf.Length - totalRead), ct);
                    if (r == 0) break;
                    totalRead += r;
                }
                htmlBytes = new byte[totalRead];
                Buffer.BlockCopy(buf, 0, htmlBytes, 0, totalRead);
            }

            string html = Encoding.UTF8.GetString(htmlBytes);

            string? title = ExtractMeta(html, "og:title");
            if (string.IsNullOrWhiteSpace(title))
            {
                var titleMatch = TitleRegex.Match(html);
                if (titleMatch.Success) title = titleMatch.Groups[1].Value;
            }

            string? desc = ExtractMeta(html, "og:description");
            if (string.IsNullOrWhiteSpace(desc))
            {
                desc = ExtractMetaByName(html, "description");
            }

            string? site = ExtractMeta(html, "og:site_name");
            if (string.IsNullOrWhiteSpace(site)) site = uri.Host;

            string? imgUrl = ExtractMeta(html, "og:image");

            string cleanTitle = WebUtility.HtmlDecode(title ?? "").Trim();
            string cleanDesc = WebUtility.HtmlDecode(desc ?? "").Trim();
            string cleanSite = WebUtility.HtmlDecode(site ?? "").Trim();

            if (string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(cleanDesc))
            {
                return null;
            }

            var preview = new LinkPreviewData
            {
                Url = uri.AbsoluteUri,
                Title = cleanTitle.Length > 200 ? cleanTitle[..200] : cleanTitle,
                Description = cleanDesc.Length > 300 ? cleanDesc[..300] : cleanDesc,
                SiteName = cleanSite.Length > 60 ? cleanSite[..60] : cleanSite,
                IsYouTube = false
            };

            // Kapak resmi
            if (!string.IsNullOrWhiteSpace(imgUrl))
            {
                try
                {
                    if (Uri.TryCreate(uri, imgUrl, out var fullImgUri) &&
                        (fullImgUri.Scheme == Uri.UriSchemeHttp || fullImgUri.Scheme == Uri.UriSchemeHttps))
                    {
                        if (await IsSafePublicHostAsync(fullImgUri.Host))
                        {
                            var imgBytes = await DownloadBytesBoundedAsync(client, fullImgUri, 2 * 1024 * 1024, ct);
                            if (imgBytes != null && imgBytes.Length > 0)
                            {
                                string? base64 = CompressImageToBase64(imgBytes, 320, 40960);
                                if (!string.IsNullOrEmpty(base64))
                                {
                                    preview.ThumbJpegBase64 = base64;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return preview;
        }

        private static string? ExtractMeta(string html, string property)
        {
            var m = MetaPropertyRegex.Matches(html);
            foreach (Match item in m)
            {
                if (string.Equals(item.Groups[1].Value, property, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Groups[2].Value;
                }
            }

            var m2 = MetaContentPropertyRegex.Matches(html);
            foreach (Match item in m2)
            {
                if (string.Equals(item.Groups[2].Value, property, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Groups[1].Value;
                }
            }

            return null;
        }

        private static string? ExtractMetaByName(string html, string name)
        {
            var m = MetaNameRegex.Matches(html);
            foreach (Match item in m)
            {
                if (string.Equals(item.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Groups[2].Value;
                }
            }
            return null;
        }

        private static async Task<byte[]?> DownloadBytesBoundedAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken ct)
        {
            try
            {
                using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return null;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                byte[] buf = new byte[8192];
                while (ms.Length < maxBytes)
                {
                    int r = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, maxBytes - (int)ms.Length)), ct);
                    if (r == 0) break;
                    ms.Write(buf, 0, r);
                }
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// İndirilen kapak resmini en fazla 320px genişliğe ölçeklendirir, JPEG kalitesiyle 40 KB altına sıkıştırıp Base64 döner.
        /// </summary>
        public static string? CompressImageToBase64(byte[] rawBytes, int maxWidth = 320, int maxBytes = 40960)
        {
            if (rawBytes == null || rawBytes.Length == 0) return null;
            try
            {
                using var inMs = new MemoryStream(rawBytes);
                using var origBmp = new System.Drawing.Bitmap(inMs);

                int origW = origBmp.Width;
                int origH = origBmp.Height;
                if (origW <= 0 || origH <= 0) return null;

                int targetW = origW > maxWidth ? maxWidth : origW;
                int targetH = origW > maxWidth ? (int)Math.Round((double)origH * maxWidth / origW) : origH;
                if (targetW <= 0) targetW = 1;
                if (targetH <= 0) targetH = 1;

                using var scaledBmp = new System.Drawing.Bitmap(targetW, targetH);
                using (var g = System.Drawing.Graphics.FromImage(scaledBmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(origBmp, 0, 0, targetW, targetH);
                }

                var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);

                long[] qualitySteps = new long[] { 70L, 55L, 40L, 25L };
                byte[]? resultBytes = null;

                foreach (var q in qualitySteps)
                {
                    using var outMs = new MemoryStream();
                    if (jpegEncoder != null)
                    {
                        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                        scaledBmp.Save(outMs, jpegEncoder, ep);
                    }
                    else
                    {
                        scaledBmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }

                    if (outMs.Length <= maxBytes)
                    {
                        resultBytes = outMs.ToArray();
                        break;
                    }
                    resultBytes = outMs.ToArray();
                }

                if (resultBytes != null && resultBytes.Length <= maxBytes)
                {
                    return Convert.ToBase64String(resultBytes);
                }

                // Aşırı büyükse boyutları yarıya indir
                if (targetW > 160)
                {
                    int halfW = targetW / 2;
                    int halfH = Math.Max(1, targetH / 2);
                    using var halfBmp = new System.Drawing.Bitmap(halfW, halfH);
                    using (var g = System.Drawing.Graphics.FromImage(halfBmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                        g.DrawImage(scaledBmp, 0, 0, halfW, halfH);
                    }
                    using var outMs = new MemoryStream();
                    if (jpegEncoder != null)
                    {
                        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 40L);
                        halfBmp.Save(outMs, jpegEncoder, ep);
                    }
                    else
                    {
                        halfBmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                    if (outMs.Length <= maxBytes)
                    {
                        return Convert.ToBase64String(outMs.ToArray());
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Güvenli bağlantı açma: Yalnızca http ve https protokollerini kabul eder.
        /// Görünen host ile hedef host farklıysa (phishing/punycode şüphesi) onay penceresi gösterir.
        /// </summary>
        public static async Task SafeOpenUrlAsync(string displayText, string destinationUrl, XamlRoot? xamlRoot)
        {
            if (string.IsNullOrWhiteSpace(destinationUrl)) return;

            string target = destinationUrl.Trim();
            if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                target = "https://" + target;
            }

            if (!Uri.TryCreate(target, UriKind.Absolute, out var destUri)) return;

            if (destUri.Scheme != Uri.UriSchemeHttp && destUri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }

            string displayHost = "";
            string disp = displayText.Trim();
            if (disp.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) disp = "https://" + disp;
            if (Uri.TryCreate(disp, UriKind.Absolute, out var dispUri))
            {
                displayHost = dispUri.Host;
            }
            else
            {
                int slashIdx = disp.IndexOf('/');
                displayHost = slashIdx > 0 ? disp[..slashIdx] : disp;
            }

            bool isPunycode = destUri.Host.StartsWith("xn--", StringComparison.OrdinalIgnoreCase) ||
                              destUri.Host.Contains(".xn--", StringComparison.OrdinalIgnoreCase);

            bool hostMismatch = !string.IsNullOrEmpty(displayHost) &&
                                displayHost.Contains('.') &&
                                !string.Equals(displayHost, destUri.Host, StringComparison.OrdinalIgnoreCase) &&
                                !destUri.Host.EndsWith("." + displayHost, StringComparison.OrdinalIgnoreCase);

            if (hostMismatch || isPunycode)
            {
                if (xamlRoot != null)
                {
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Dış Bağlantı Uyarısı",
                            Content = $"Bu bağlantı sizi şu adrese yönlendirecek:\n\n{destUri.Host}\n\n(Görünen metin: {displayText})\n\nAçılsın mı?",
                            PrimaryButtonText = "Bağlantıyı Aç",
                            CloseButtonText = "İptal",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = xamlRoot
                        };

                        var result = await dialog.ShowAsync();
                        if (result != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }
                    catch { }
                }
            }

            try
            {
                await Launcher.LaunchUriAsync(destUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchUri] Hata: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// RichTextBlock için metindeki URL'leri algılayıp tıklanabilir Hyperlink nesnelerine dönüştüren ekli özellik (Attached Property).
    /// </summary>
    public static class ChatRichTextHelper
    {
        public static readonly DependencyProperty FormattedTextProperty =
            DependencyProperty.RegisterAttached(
                "FormattedText",
                typeof(string),
                typeof(ChatRichTextHelper),
                new PropertyMetadata(null, OnFormattedTextChanged));

        public static string? GetFormattedText(DependencyObject obj) =>
            (string?)obj.GetValue(FormattedTextProperty);

        public static void SetFormattedText(DependencyObject obj, string? value) =>
            obj.SetValue(FormattedTextProperty, value);

        private static void OnFormattedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichTextBlock rtb)
            {
                UpdateRichText(rtb, e.NewValue as string);
            }
        }

        private static readonly Regex UrlRegex = new Regex(
            @"(?:https?://|www\.)[^\s<>""']+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly SolidColorBrush LinkBrush =
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 125, 211, 252)); // #7DD3FC

        private static void UpdateRichText(RichTextBlock rtb, string? text)
        {
            rtb.Blocks.Clear();
            if (string.IsNullOrEmpty(text)) return;

            var paragraph = new Paragraph();
            int lastIndex = 0;
            var matches = UrlRegex.Matches(text);

            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    string normalText = text.Substring(lastIndex, match.Index - lastIndex);
                    paragraph.Inlines.Add(new Run { Text = normalText });
                }

                string rawUrl = match.Value;
                string trailingPunct = "";

                while (rawUrl.Length > 0 && ",.!?);:'\"".IndexOf(rawUrl[^1]) >= 0)
                {
                    trailingPunct = rawUrl[^1] + trailingPunct;
                    rawUrl = rawUrl[..^1];
                }

                if (rawUrl.Length > 0)
                {
                    string targetUrl = rawUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                        ? "https://" + rawUrl
                        : rawUrl;

                    var hyperlink = new Hyperlink
                    {
                        UnderlineStyle = UnderlineStyle.None,
                        Foreground = LinkBrush
                    };

                    hyperlink.Inlines.Add(new Run { Text = rawUrl });

                    string clickDisplay = rawUrl;
                    string clickTarget = targetUrl;
                    hyperlink.Click += (s, e) =>
                    {
                        _ = LinkPreviewHelper.SafeOpenUrlAsync(clickDisplay, clickTarget, rtb.XamlRoot);
                    };

                    paragraph.Inlines.Add(hyperlink);
                }

                if (trailingPunct.Length > 0)
                {
                    paragraph.Inlines.Add(new Run { Text = trailingPunct });
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                string remainingText = text.Substring(lastIndex);
                paragraph.Inlines.Add(new Run { Text = remainingText });
            }

            rtb.Blocks.Add(paragraph);
        }
    }
}
