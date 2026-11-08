using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace RunaVoice_WinUI;

public partial class App : Application
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private static readonly object TimerResolutionLock = new();
    private static bool _highResTimerActive;

    /// <summary>
    /// 1 ms Windows zamanlayıcı çözünürlüğü yalnızca sesli odadayken açılır (ses çerçeveleme, yayın
    /// yakalama aralıkları için). Önceden uygulama açıldığı an tüm sistem için açılıyor, lobide boşta
    /// beklerken bile işlemci uyanmalarını ve güç tüketimini artırıyordu.
    /// </summary>
    public static void SetHighResolutionTimer(bool enabled)
    {
        lock (TimerResolutionLock)
        {
            if (enabled == _highResTimerActive) return;
            try
            {
                if (enabled) TimeBeginPeriod(1);
                else TimeEndPeriod(1);
                _highResTimerActive = enabled;
            }
            catch { }
        }
    }

    private Window? _window;

    public App()
    {

        UnhandledException += (s, e) =>
        {
            LogException("XAML_UNHANDLED", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogException("APPDOMAIN_UNHANDLED", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogException("UNOBSERVED_TASK", e.Exception);
            e.SetObserved();
        };

#if DEBUG
        try
        {
            RunaVoice_WinUI.Services.AnnotationProtocol.RunSelfCheck();
        }
        catch (Exception ex)
        {
            LogException("SELF_CHECK_FAILED", ex);
        }
#endif

        InitializeComponent();
    }

    private static void LogException(string tag, Exception? ex)
    {
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunaVoice");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {ex}\r\n\r\n");
        }
        catch { }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
