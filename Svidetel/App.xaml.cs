using System.Security.Principal;
using System.Windows;
namespace Svidetel;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private EventWaitHandle? showWindow;
    private RegisteredWaitHandle? wakeRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int wait = Array.IndexOf(e.Args, "--wait-for");
        if (wait >= 0 && wait + 1 < e.Args.Length && int.TryParse(e.Args[wait + 1], out var prior) && prior != Environment.ProcessId)
        {
            try { using var previous = System.Diagnostics.Process.GetProcessById(prior); previous.WaitForExit(5000); }
            catch (ArgumentException) { }
        }
        bool sample = e.Args.Contains("--demo") || e.Args.Contains("--capture-demo");
        if (!sample)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var name = @"Local\Svidetel-" + identity.User?.Value;
            showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
            instance = new Mutex(false, name + "-instance", out var created);
            if (!created)
            {
                if (!e.Args.Contains("--tray")) showWindow.Set();
                Shutdown();
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        if (showWindow is not null)
            wakeRegistration = ThreadPool.RegisterWaitForSingleObject(showWindow,
                (_, _) => Dispatcher.BeginInvoke(window.OpenFromTray), null, Timeout.Infinite, false);
        if (e.Args.Contains("--tray") && !sample) window.StartInTray();
        else window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.AllowExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        wakeRegistration?.Unregister(null);
        showWindow?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
