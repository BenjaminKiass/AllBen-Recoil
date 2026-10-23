using System;
using System.Threading;
using System.Windows;

namespace AllBenRecoil
{
    public partial class App : Application
    {
        static Mutex mutex;
        static EventWaitHandle showSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            mutex = new Mutex(true, "AllBenRecoil_single_instance", out bool created);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "AllBenRecoil_show");
            if (!created)
            {
                showSignal.Set();
                Shutdown();
                return;
            }

            Log.Info($"AllBen-Recoil starting v1.0.0 args=[{string.Join(" ", e.Args)}]");
            DispatcherUnhandledException += (s, a) => { Log.Error("UI", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Error("crash", a.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => { Log.Error("task", a.Exception); a.SetObserved(); };

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var w = new MainWindow();
            MainWindow = w;

            ThreadPool.RegisterWaitForSingleObject(showSignal,
                (s, t) => w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)), null, -1, false);

            if (Array.IndexOf(e.Args, "--tray") >= 0) w.StartInTray();
            else w.Show();
        }
    }
}
