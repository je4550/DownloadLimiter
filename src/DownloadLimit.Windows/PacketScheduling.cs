using System.Diagnostics;

namespace DownloadLimit.Windows;

public static class PacketScheduling
{
    public static void ConfigureProcess()
    {
        using Process process = Process.GetCurrentProcess();
        // Older startup tasks launch Below Normal. Correct the current launch
        // before any packet handles open, including when task migration fails.
        if (process.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal)
            process.PriorityClass = ProcessPriorityClass.Normal;
    }

    public static void ConfigureWorker() => Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
}
