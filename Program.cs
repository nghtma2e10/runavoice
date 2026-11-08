using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinRT;

namespace RunaVoice_WinUI;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Kurulum/guncelleme sirasinda Velopack'in kendi islerini yapmasi icin
        // bu satir uygulamanin EN BASINDA olmali.
        VelopackApp.Build().Run();

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
