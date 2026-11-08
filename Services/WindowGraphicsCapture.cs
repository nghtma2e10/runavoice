using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Windows.Graphics.Capture (WGC) ile tek bir uygulama penceresini yakalar.
    /// Ekran bölgesini kopyalayan BitBlt'in aksine, pencere arkada kalsa veya üstü başka
    /// pencerelerle (örn. RUNA'nın kendisi) kapansa bile yalnızca o pencerenin kendi içeriğini verir;
    /// Chromium/Electron/UWP/WinUI gibi GPU ile çizen uygulamalarda da doğru görüntü üretir.
    /// Sınıf iş parçacığı güvenli değildir; yalnızca yakalama döngüsünden kullanılır.
    /// </summary>
    internal sealed class WindowGraphicsCapture : IDisposable
    {
        #region Interop
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software, uint flags,
            IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const int D3D_DRIVER_TYPE_WARP = 5;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        private const uint D3D11_SDK_VERSION = 7;

        private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        #endregion

        private readonly IDirect3DDevice _device;
        private readonly GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _session;
        private Windows.Graphics.SizeInt32 _poolSize;
        private volatile bool _closed;

        // Son başarılı karenin BGRA pikselleri (pencere küçültülünce / yeni kare gelmeyince tekrar kullanılır)
        private byte[] _pixels = Array.Empty<byte>();
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool HasFrame => Width > 0 && Height > 0;

        /// <summary>Hedef pencere kapandı (yakalama artık kare üretmeyecek).</summary>
        public bool IsClosed => _closed;

        public static bool IsSupported
        {
            get
            {
                try { return GraphicsCaptureSession.IsSupported(); }
                catch { return false; }
            }
        }

        private WindowGraphicsCapture(IDirect3DDevice device, GraphicsCaptureItem item)
        {
            _device = device;
            _item = item;
            _poolSize = item.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _session = _framePool.CreateCaptureSession(item);
            _item.Closed += (_, _) => _closed = true;

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try { _session.IsCursorCaptureEnabled = true; } catch { }
            }
            // Windows 11: yakalanan pencerenin etrafındaki sarı çerçeveyi kapat (desteklenmiyorsa sessizce geç)
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                try { _session.IsBorderRequired = false; } catch { }
            }

            _session.StartCapture();
        }

        /// <summary>Pencere için WGC oturumu açar; desteklenmiyorsa veya başarısızsa null döner.</summary>
        public static WindowGraphicsCapture? TryCreate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsSupported) return null;
            try
            {
                var device = CreateD3DDevice();
                if (device == null) return null;
                var item = CreateItemForWindow(hwnd);
                if (item == null) return null;
                return new WindowGraphicsCapture(device, item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WGC] Oturum açılamadı: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Varsa en yeni kareyi CPU belleğine alır. Yeni kare yoksa önceki kare korunur.
        /// Dönüş: elde kullanılabilir bir kare var mı.
        /// </summary>
        public bool TryUpdateFrame()
        {
            if (_closed) return HasFrame;

            // Kuyruktaki en yeni kareye kadar ilerle (eskileri at), gecikme birikmesin
            Direct3D11CaptureFrame? frame = null;
            while (true)
            {
                var next = _framePool.TryGetNextFrame();
                if (next == null) break;
                frame?.Dispose();
                frame = next;
            }
            if (frame == null) return HasFrame;

            using (frame)
            {
                var content = frame.ContentSize;
                if (content.Width <= 0 || content.Height <= 0) return HasFrame;

                // Pencere boyutu değiştiyse havuzu yeni boyuta göre yeniden kur (bu kare yine kullanılır)
                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    _poolSize = content;
                    try { _framePool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize); } catch { }
                }

                using var soft = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                    .AsTask().GetAwaiter().GetResult();

                int surfW = soft.PixelWidth;
                int surfH = soft.PixelHeight;
                int needed = surfW * surfH * 4;
                if (_pixels.Length != needed) _pixels = new byte[needed];
                soft.CopyToBuffer(_pixels.AsBuffer());

                // Yüzey, havuz boyutunda olabilir; geçerli içerik sol üstteki ContentSize kadardır
                int w = Math.Min(content.Width, surfW);
                int h = Math.Min(content.Height, surfH);
                if (w != surfW)
                {
                    // Satırları sıkıştır: hedef genişlik w, kaynak satır uzunluğu surfW
                    for (int y = 1; y < h; y++)
                        Buffer.BlockCopy(_pixels, y * surfW * 4, _pixels, y * w * 4, w * 4);
                }
                Width = w;
                Height = h;
                return true;
            }
        }

        /// <summary>Son karenin BGRA pikselleri (satır uzunluğu Width*4).</summary>
        public byte[] Pixels => _pixels;

        public void Dispose()
        {
            try { _session.Dispose(); } catch { }
            try { _framePool.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
        }

        private static IDirect3DDevice? CreateD3DDevice()
        {
            foreach (int driverType in new[] { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP })
            {
                int hr = D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION, out IntPtr d3dDevice, out _, out IntPtr context);
                if (hr < 0 || d3dDevice == IntPtr.Zero) continue;

                try
                {
                    Guid iid = IID_IDXGIDevice;
                    if (Marshal.QueryInterface(d3dDevice, ref iid, out IntPtr dxgiDevice) < 0) continue;
                    try
                    {
                        if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable) < 0 || inspectable == IntPtr.Zero) continue;
                        try
                        {
                            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                        }
                        finally
                        {
                            Marshal.Release(inspectable);
                        }
                    }
                    finally
                    {
                        Marshal.Release(dxgiDevice);
                    }
                }
                finally
                {
                    if (context != IntPtr.Zero) Marshal.Release(context);
                    Marshal.Release(d3dDevice);
                }
            }
            return null;
        }

        private static unsafe GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
        {
            // IGraphicsCaptureItemInterop, GraphicsCaptureItem aktivasyon fabrikası üzerinden alınır
            var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            Guid interopIid = IID_IGraphicsCaptureItemInterop;
            if (Marshal.QueryInterface(factory.ThisPtr, ref interopIid, out IntPtr interop) < 0) return null;
            try
            {
                // vtable: 0-2 IUnknown, 3 CreateForWindow, 4 CreateForMonitor
                var vtbl = *(IntPtr**)interop;
                var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                Guid itemIid = IID_IGraphicsCaptureItem;
                IntPtr itemPtr = IntPtr.Zero;
                int hr = createForWindow(interop, hwnd, &itemIid, &itemPtr);
                if (hr < 0 || itemPtr == IntPtr.Zero) return null;
                try
                {
                    return GraphicsCaptureItem.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
    }
}
