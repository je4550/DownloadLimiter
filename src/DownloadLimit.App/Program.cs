using DownloadLimit.Windows;
using System.Runtime.InteropServices;

namespace DownloadLimit.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (RuntimeInformation.OSArchitecture != Architecture.X64 ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045))
        {
            MessageBox.Show("DownloadLimit requires Windows 10 22H2 or Windows 11, x64.", "DownloadLimit");
            return;
        }
        using var singleInstance = new Mutex(false, "Global\\DownloadLimit-1D6062C4-34DF-4ACD-9774-95A4F5B7E395");
        bool owns;
        try { owns = singleInstance.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns)
        {
            MessageBox.Show("DownloadLimit is already running. Right-click its tray icon.", "DownloadLimit");
            return;
        }
        try
        {
            PacketScheduling.ConfigureProcess();
            bool firstInstall = AppFiles.Install();
            WinDivertTransport.ConfigureLibrary(AppFiles.NativeDirectory);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            using var context = new TrayContext(firstInstall);
            Application.ThreadException += (_, e) => context.HandleUnexpectedError(e.Exception);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
            MessageBox.Show(ex.Message, "DownloadLimit could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { singleInstance.ReleaseMutex(); }
    }
}
