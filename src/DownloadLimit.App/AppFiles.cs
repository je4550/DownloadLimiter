using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DownloadLimit.Core;

namespace DownloadLimit.App;

internal static class AppFiles
{
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DownloadLimit");
    public static string InstalledExecutable => Path.Combine(InstallDirectory, "DownloadLimit.exe");
    public static string NativeDirectory => Path.Combine(InstallDirectory, "WinDivert-2.2.2-x64");
    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadLimit");
    private static string SettingsPath => Path.Combine(UserDirectory, "settings.json");

    public static bool Install()
    {
        bool firstInstall = !File.Exists(InstalledExecutable);
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(NativeDirectory);
        string source = Environment.ProcessPath ?? throw new IOException("Could not locate this executable.");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase) &&
            (!File.Exists(InstalledExecutable) || !SameFile(source, InstalledExecutable)))
        {
            string temporary = InstalledExecutable + ".new";
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, InstalledExecutable, overwrite: true);
        }
        // Existing native files remain replaceable for LGPL-compatible relinking.
        Extract("Dependencies.WinDivert.dll", Path.Combine(NativeDirectory, "WinDivert.dll"), false);
        Extract("Dependencies.WinDivert64.sys", Path.Combine(NativeDirectory, "WinDivert64.sys"), false);
        string licenses = Path.Combine(InstallDirectory, "Licenses");
        Directory.CreateDirectory(licenses);
        Extract("Dependencies.WinDivert.LICENSE", Path.Combine(licenses, "WinDivert-LICENSE.txt"), true);
        Extract("Dependencies.WinDivert.Source.zip", Path.Combine(licenses, "WinDivert-2.2.2-Source.zip"), false);
        Extract("Dependencies.NOTICES", Path.Combine(licenses, "THIRD-PARTY-NOTICES.txt"), true);
        Extract("Dependencies.NOTICES", Path.Combine(InstallDirectory, "THIRD-PARTY-NOTICES.txt"), true);
        Extract("Dependencies.APP-LICENSE", Path.Combine(licenses, "DownloadLimit-MIT-LICENSE.txt"), true);
        Extract("Dependencies.APP-LICENSE", Path.Combine(InstallDirectory, "LICENSE"), true);
        Extract("Dependencies.DotNet.LICENSE", Path.Combine(licenses, "DotNet-LICENSE.txt"), true);
        Extract("Dependencies.DotNet.NOTICES", Path.Combine(licenses, "DotNet-NOTICES.txt"), true);
        Extract("Dependencies.WindowsDesktop.LICENSE", Path.Combine(licenses, "WindowsDesktop-LICENSE.txt"), true);
        Extract("Dependencies.README", Path.Combine(InstallDirectory, "README.md"), true);
        string docs = Path.Combine(InstallDirectory, "docs");
        Directory.CreateDirectory(docs);
        Extract("Dependencies.VALIDATION", Path.Combine(docs, "WINDOWS-VALIDATION.md"), true);
        Extract("Dependencies.DESIGN", Path.Combine(docs, "DESIGN.md"), true);
        return firstInstall;
    }

    private static bool SameFile(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var first = File.OpenRead(left);
        using var second = File.OpenRead(right);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    private static void Extract(string resource, string target, bool overwrite)
    {
        if (!overwrite && File.Exists(target)) return;
        using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ??
            throw new IOException($"Missing embedded dependency: {resource}");
        string temporary = target + ".new";
        using (var output = File.Create(temporary)) source.CopyTo(output);
        File.Move(temporary, target, overwrite: true);
    }

    public static AppSettings LoadSettings(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (settings is null || !settings.IsValid) throw new InvalidDataException("Invalid saved speed limits.");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error(ex);
            warning = "Saved settings could not be read. Defaults are restored; shaping remains disabled.";
            return new();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        Directory.CreateDirectory(UserDirectory);
        string temporary = SettingsPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}

internal static class AppLog
{
    private static readonly object Gate = new();
    public static void Error(Exception error)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppFiles.UserDirectory);
                string path = Path.Combine(AppFiles.UserDirectory, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                {
                    for (int index = 2; index >= 1; index--)
                    {
                        string older = path + "." + index;
                        if (File.Exists(older)) File.Move(older, path + "." + (index + 1), true);
                    }
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {error}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
