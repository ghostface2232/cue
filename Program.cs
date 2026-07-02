using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Cue.Services;

namespace Cue;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        ToolkitToastPresenter? toastServices = null;

        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // XAML has established the STA/dispatcher, but App.xaml resources have not been loaded yet.
            // Subscribe here so a toast-launched completion can stay headless while COM activation is valid.
            toastServices = new ToolkitToastPresenter();
            _ = new App(toastServices);
        });

        toastServices?.Dispose();
        return 0;
    }
}
