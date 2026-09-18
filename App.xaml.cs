using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace OptiPC;

public partial class App : Application
{
    static string CrashLog => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OptiPC", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash("AppDomain", args.ExceptionObject as Exception);

        try
        {
            WriteCrash("START", null);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Fatal("Opti-PC n'a pas pu démarrer.", ex);
            Shutdown(-1);
        }
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Fatal("Une erreur inattendue s'est produite dans Opti-PC.", e.Exception);
        e.Handled = true;
    }

    static void Fatal(string title, Exception ex)
    {
        WriteCrash("FATAL", ex);
        MessageBox.Show(
            title + "\n\n" + ex.Message +
            "\n\nUn rapport a été enregistré ici :\n" + CrashLog,
            "Opti-PC — erreur",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    static void WriteCrash(string stage, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}");
            sb.AppendLine($"Version: {typeof(App).Assembly.GetName().Version}");
            sb.AppendLine($"Windows: {Environment.OSVersion}");
            sb.AppendLine($"64-bit OS/process: {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess}");
            if (ex != null) sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(CrashLog, sb.ToString());
        }
        catch { }
    }
}
