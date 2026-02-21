using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        AppPaths.EnsureCreated();

        using var logger = new FileLogger(AppPaths.AppLogPath);

        Application.ThreadException += (_, args) =>
        {
            logger.Error("UI thread exception", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                logger.Error("Unhandled application exception", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppPaths.CleanupLegacyData(logger);
        ProtocolRegistrationManager.CleanupLegacyRegistrations(logger);
        ProtocolRegistrationManager.EnsureRegistered(Application.ExecutablePath, logger);

        var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

        using var singleInstanceMutex = new Mutex(true, AppIdentity.SingleInstanceMutex, out var createdNew);
        if (!createdNew)
        {
            // Secondary invocation (typically protocol open) forwards/captures URL and exits.
            _ = ProtocolInvocationHandler.TryHandle(launchArgs, logger);
            return;
        }

        // Primary instance: handle initial protocol URL (if any), then keep tray alive.
        if (ProtocolInvocationHandler.TryHandle(launchArgs, logger))
        {
            logger.Info("Handled protocol URL on primary instance; continuing with tray mode.");
        }

        using var appContext = new TrayApplicationContext(logger);
        Application.Run(appContext);
    }
}
