using System.Windows;

namespace ArkFixYourQueues;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, "Local\\ArkFixYourQueues", out var isFirstInstance);
        _ownsSingleInstance = isFirstInstance;
        if (!isFirstInstance)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("ARK Join Assist is already running.", "ARK Join Assist");
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
