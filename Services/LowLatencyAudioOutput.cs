using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Windows 10+ IAudioClient3 ile düşük gecikmeli, PAYLAŞIMLI modda hoparlör çıkışı.
    ///
    /// Normal paylaşımlı WASAPI ses motoru ~10 ms'lik periyotla çalışır ve NAudio en az iki periyot (~20 ms)
    /// tampon tutar. IAudioClient3, sürücü destekliyorsa motor periyodunu 2,7–10 ms'ye indirmeye izin verir.
    /// Bu sınıf ~5 ms periyot seçer ve cihaz kuyruğunda en fazla iki periyot (~10 ms) ses tutar.
    /// Exclusive mod kullanılmaz: oyun ve diğer uygulamaların sesi çalmaya devam eder.
    ///
    /// Sürücü düşük periyodu desteklemiyorsa (en küçük periyot = varsayılan periyot) veya arayüz yoksa
    /// <see cref="TryStart"/> false döner ve çağıran eski (NAudio) çıkışa döner. Çalışırken ses kartının
    /// kuyruğu boşalırsa (takılma) periyot kendiliğinden iki katına çıkarılır.
    /// Tüm COM nesneleri kendi (MTA) oynatma iş parçacığında oluşturulur ve kullanılır.
    /// </summary>
    public sealed class LowLatencyAudioOutput : IDisposable
    {
        private readonly string _deviceId;
        private readonly IWaveProvider _source;
        private readonly int _sampleRate;
        private readonly int _bytesPerFrame;

        private Thread? _thread;
        private volatile bool _stopRequested;
        private IntPtr _eventHandle = IntPtr.Zero;

        private volatile int _periodFrames;
        private volatile int _queueFrames;
        private int _glitches;

        /// <summary>Motor periyodu (ms).</summary>
        public double PeriodMs => _periodFrames > 0 ? _periodFrames * 1000.0 / _sampleRate : -1;
        /// <summary>Cihaz kuyruğunda tutulan en fazla ses (ms) = hoparlör tamponu.</summary>
        public double BufferMs => _queueFrames > 0 ? _queueFrames * 1000.0 / _sampleRate : -1;
        public int Glitches => Volatile.Read(ref _glitches);

        /// <summary>Başladıktan sonra cihaz hatası (ör. kulaklık çıkarıldı) olursa tetiklenir.</summary>
        public event Action<Exception>? Failed;

        private const double TargetPeriodMs = 5.0;

        public LowLatencyAudioOutput(string deviceId, IWaveProvider source)
        {
            _deviceId = deviceId;
            _source = source;
            _sampleRate = Math.Max(8000, source.WaveFormat.SampleRate);
            _bytesPerFrame = source.WaveFormat.BlockAlign > 0
                ? source.WaveFormat.BlockAlign
                : source.WaveFormat.Channels * (source.WaveFormat.BitsPerSample / 8);
        }

        /// <summary>
        /// Oynatmayı başlatır. Düşük gecikme desteklenmiyorsa veya başlatılamazsa false ve neden döner.
        /// </summary>
        public bool TryStart(out string reason)
        {
            reason = "";
            var ready = new ManualResetEventSlim(false);
            string initError = "";
            bool initOk = false;

            _thread = new Thread(() =>
            {
                RenderThread(ok =>
                {
                    initOk = ok.success;
                    initError = ok.error;
                    ready.Set();
                });
            })
            {
                IsBackground = true,
                Name = "RUNA Low-Latency Render",
                Priority = ThreadPriority.Highest
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();

            if (!ready.Wait(3000))
            {
                reason = "Başlatma zaman aşımı";
                Dispose();
                return false;
            }
            if (!initOk)
            {
                reason = initError;
                try { _thread.Join(1000); } catch { }
                _thread = null;
                return false;
            }
            return true;
        }

        private void RenderThread(Action<(bool success, string error)> reportInit)
        {
            IntPtr mmcss = IntPtr.Zero;
            bool reported = false;
            int periodFrames = 0;

            try
            {
                uint taskIndex = 0;
                try { mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex); } catch { }

                _eventHandle = CreateEvent(IntPtr.Zero, false, false, null);
                if (_eventHandle == IntPtr.Zero) throw new InvalidOperationException("CreateEvent başarısız");

                while (!_stopRequested)
                {
                    // Bir akış oturumu: başlat, çal; takılma çoksa periyodu büyütüp yeniden başlat
                    var session = OpenStream(ref periodFrames);
                    if (!reported)
                    {
                        reported = true;
                        reportInit((true, ""));
                    }

                    bool grow = RunSession(session, periodFrames);
                    session.Dispose();
                    if (!grow) break;

                    int bigger = periodFrames * 2;
                    if (bigger > session.DefaultPeriodFrames) bigger = (int)session.DefaultPeriodFrames;
                    if (bigger <= periodFrames) break; // RunSession yalnızca büyütülebilirken true döner; güvenlik
                    periodFrames = bigger;
                }
            }
            catch (Exception ex)
            {
                if (!reported)
                {
                    reported = true;
                    reportInit((false, ex.Message));
                }
                else if (!_stopRequested)
                {
                    try { Failed?.Invoke(ex); } catch { }
                }
            }
            finally
            {
                if (mmcss != IntPtr.Zero) { try { AvRevertMmThreadCharacteristics(mmcss); } catch { } }
                if (_eventHandle != IntPtr.Zero) { CloseHandle(_eventHandle); _eventHandle = IntPtr.Zero; }
            }
        }

        private sealed class StreamSession : IDisposable
        {
            public IAudioClient3? Client;
            public IAudioRenderClient? Render;
            public uint BufferFrames;
            public uint DefaultPeriodFrames;

            public void Dispose()
            {
                try { Client?.Stop(); } catch { }
                if (Render != null) { try { Marshal.ReleaseComObject(Render); } catch { } Render = null; }
                if (Client != null) { try { Marshal.ReleaseComObject(Client); } catch { } Client = null; }
            }
        }

        /// <summary>
        /// IAudioClient3'ü açar ve düşük periyotlu paylaşımlı akışı başlatır.
        /// <paramref name="periodFrames"/> 0 ise ~5 ms'lik periyot seçilir.
        /// </summary>
        private StreamSession OpenStream(ref int periodFrames)
        {
            var session = new StreamSession();
            IntPtr mixFormat = IntPtr.Zero;
            object? enumObj = null;
            IMMDevice? device = null;
            try
            {
                // NAudio aynı CLSID için kendi RCW sınıfını kaydettiğinden "new" ile oluşturulan nesne o türe
                // bağlanıyor; CLSID'den oluşturup doğrudan arayüze (QueryInterface) dönüştürmek güvenli yol.
                enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, true)!)!;
                var enumerator = (IMMDeviceEnumerator)enumObj;
                Check(enumerator.GetDevice(_deviceId, out device), "GetDevice");

                Guid iid = typeof(IAudioClient3).GUID;
                Check(device!.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object clientObj), "IAudioClient3 etkinleştirilemedi");
                session.Client = (IAudioClient3)clientObj;

                Check(session.Client.GetMixFormat(out mixFormat), "GetMixFormat");
                int mixRate = Marshal.ReadInt32(mixFormat, 4); // WAVEFORMATEX.nSamplesPerSec
                if (mixRate != _sampleRate)
                    throw new InvalidOperationException($"Biçim uyuşmazlığı ({mixRate} Hz ≠ {_sampleRate} Hz)");

                Check(session.Client.GetSharedModeEnginePeriod(mixFormat, out uint defPeriod, out uint fundPeriod, out uint minPeriod, out uint maxPeriod),
                      "GetSharedModeEnginePeriod");
                session.DefaultPeriodFrames = defPeriod;

                if (minPeriod >= defPeriod || fundPeriod == 0)
                    throw new NotSupportedException($"Sürücü düşük periyodu desteklemiyor (en küçük {minPeriod} = varsayılan {defPeriod} kare)");

                if (periodFrames <= 0)
                {
                    int target = (int)Math.Ceiling(_sampleRate * TargetPeriodMs / 1000.0);
                    int multiple = (int)Math.Ceiling(target / (double)fundPeriod) * (int)fundPeriod;
                    periodFrames = Math.Clamp(multiple, (int)minPeriod, (int)defPeriod);
                }

                Check(session.Client.InitializeSharedAudioStream(AudclntStreamFlagsEventCallback, (uint)periodFrames, mixFormat, IntPtr.Zero),
                      "InitializeSharedAudioStream");
                Check(session.Client.SetEventHandle(_eventHandle), "SetEventHandle");
                Check(session.Client.GetBufferSize(out session.BufferFrames), "GetBufferSize");

                Guid renderIid = typeof(IAudioRenderClient).GUID;
                Check(session.Client.GetService(ref renderIid, out object renderObj), "GetService(IAudioRenderClient)");
                session.Render = (IAudioRenderClient)renderObj;

                _periodFrames = periodFrames;
                // Kuyrukta en fazla iki periyot: bir periyot motor tarafından okunurken bir sonraki hazır bekler
                _queueFrames = (int)Math.Min(session.BufferFrames, (uint)(periodFrames * 2));
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
            finally
            {
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumObj != null) { try { Marshal.ReleaseComObject(enumObj); } catch { } }
            }
        }

        /// <summary>Akışı çalar. Takılma sıklığı yüksekse (periyot büyütülmeli) true döner.</summary>
        private bool RunSession(StreamSession s, int periodFrames)
        {
            var client = s.Client!;
            var render = s.Render!;
            int queue = _queueFrames;
            byte[] scratch = new byte[(int)s.BufferFrames * _bytesPerFrame];

            // Başlamadan önce kuyruğu doldur
            FillFrames(render, queue, scratch);
            Check(client.Start(), "Start");

            long windowStart = Environment.TickCount64;
            int windowGlitches = 0;
            int events = 0;

            while (!_stopRequested)
            {
                uint wait = WaitForSingleObject(_eventHandle, 200);
                if (_stopRequested) break;
                if (wait != 0) continue; // zaman aşımı: cihaz geçici olarak durmuş olabilir

                Check(client.GetCurrentPadding(out uint padding), "GetCurrentPadding");
                events++;

                // Kuyruk tamamen boşaldıysa motor bir periyodu sessiz çaldı (takılma)
                if (padding == 0 && events > 4)
                {
                    Interlocked.Increment(ref _glitches);
                    windowGlitches++;
                }

                int toWrite = queue - (int)padding;
                if (toWrite > 0) FillFrames(render, toWrite, scratch);

                long now = Environment.TickCount64;
                if (now - windowStart >= 10_000)
                {
                    // 10 sn'de 3+ takılma: bu bilgisayar/sürücü bu periyodu kaldıramıyor, büyüt
                    if (windowGlitches >= 3 && periodFrames < s.DefaultPeriodFrames) return true;
                    windowStart = now;
                    windowGlitches = 0;
                }
            }
            return false;
        }

        private void FillFrames(IAudioRenderClient render, int frames, byte[] scratch)
        {
            if (frames <= 0) return;
            int bytes = frames * _bytesPerFrame;
            if (bytes > scratch.Length) { frames = scratch.Length / _bytesPerFrame; bytes = frames * _bytesPerFrame; }

            Check(render.GetBuffer((uint)frames, out IntPtr data), "GetBuffer");
            int read = 0;
            try { read = _source.Read(scratch, 0, bytes); } catch { read = 0; }
            if (read < bytes) Array.Clear(scratch, read, bytes - read);
            Marshal.Copy(scratch, 0, data, bytes);
            Check(render.ReleaseBuffer((uint)frames, 0), "ReleaseBuffer");
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException($"{what} (0x{hr:X8})", hr);
        }

        public void Dispose()
        {
            _stopRequested = true;
            var h = _eventHandle;
            if (h != IntPtr.Zero) { try { SetEvent(h); } catch { } }
            var t = _thread;
            if (t != null && t != Thread.CurrentThread) { try { t.Join(1500); } catch { } }
            _thread = null;
        }

        // ------------------------------------------------------------------ Win32 / COM
        private const int ClsCtxAll = 0x17;
        private const uint AudclntStreamFlagsEventCallback = 0x00040000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
        [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr h);

        private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        // IAudioClient3 : IAudioClient2 : IAudioClient — sanal tablo sırası korunarak tüm metotlar tanımlı
        [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient3
        {
            // IAudioClient
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
            // IAudioClient2
            [PreserveSig] int IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool offloadCapable);
            [PreserveSig] int SetClientProperties(IntPtr properties);
            [PreserveSig] int GetBufferSizeLimits(IntPtr format, [MarshalAs(UnmanagedType.Bool)] bool eventDriven, out long minDuration, out long maxDuration);
            // IAudioClient3
            [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames, out uint fundamentalPeriodFrames, out uint minPeriodFrames, out uint maxPeriodFrames);
            [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
            [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr sessionGuid);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
        }
    }
}
