using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using System.Xml;
using System.Text;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DownloadLimit.Windows.Tests")]

namespace DownloadLimit.Windows;

public static class StartupTask
{
    private static string UserSid
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        }
    }
    private static string Name => $"DownloadLimit-{UserSid}";
    public static bool Exists() => Run(["/Query", "/TN", Name], throwOnError: false) == 0;

    public static void SetEnabled(bool enabled, string executable)
    {
        if (!enabled)
        {
            if (Exists()) Run(["/Delete", "/TN", Name, "/F"]);
            return;
        }
        string xmlPath = Path.Combine(Path.GetTempPath(), $"DownloadLimit-task-{Guid.NewGuid():N}.xml");
        try
        {
            SaveDefinition(xmlPath, executable, UserSid);
            Run(["/Create", "/TN", Name, "/XML", xmlPath, "/F"]);
        }
        finally { File.Delete(xmlPath); }
    }

    internal static void SaveDefinition(string xmlPath, string executable, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("The startup executable must use an absolute path.", nameof(executable));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "DownloadLimit tray app; starts with shaping disabled.")),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                new XElement(ns + "Enabled", "true"), new XElement(ns + "UserId", userSid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "StartWhenAvailable", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "User"),
                new XElement(ns + "Exec", new XElement(ns + "Command", executable),
                    new XElement(ns + "Arguments", "--startup"),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable))))));
        // Task Scheduler's XML file importer expects little-endian Unicode.
        using var writer = XmlWriter.Create(xmlPath, new XmlWriterSettings { Encoding = Encoding.Unicode, Indent = true });
        document.Save(writer);
    }

    private static int Run(string[] arguments, bool throwOnError = true)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("Could not open Task Scheduler.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            throw new TimeoutException("Task Scheduler did not respond.");
        }
        if (throwOnError && process.ExitCode != 0)
            throw new IOException($"Task Scheduler rejected the change: {error.GetAwaiter().GetResult()} {output.GetAwaiter().GetResult()}");
        return process.ExitCode;
    }
}
