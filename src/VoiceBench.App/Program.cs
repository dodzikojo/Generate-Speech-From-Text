using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace IssueLab.VoiceBench.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppInstance current = AppInstance.GetCurrent();
        AppActivationArguments activation = current.GetActivatedEventArgs();
        AppInstance instance = AppInstance.FindOrRegisterForKey("IssueLab.VoiceBench.Desktop.v1");
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return;
        }

        instance.Activated += (_, _) => App.MainWindow?.DispatcherQueue.TryEnqueue(() => App.MainWindow?.Activate());
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
