using System;
using System.Diagnostics;
using System.IO;

namespace Monkasa.Services;

public sealed class AppLogService
{
    public string LogFilePath => GetLogFilePath();

    public static string GetLogFilePath()
    {
        var logDirectory = GetLogDirectoryPath();
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, "monkasa.log");
    }

    public void OpenLogFile()
    {
        var logFilePath = LogFilePath;
        var logDirectory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        if (!File.Exists(logFilePath))
        {
            using (File.Create(logFilePath))
            {
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", logFilePath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Process.Start("xdg-open", logFilePath);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = logFilePath,
            UseShellExecute = true,
        });
    }

    private static string GetLogDirectoryPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Monkasa",
                "Logs");
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(homeDirectory, "Library", "Logs", "Monkasa");
        }

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateHome))
        {
            stateHome = Path.Combine(homeDirectory, ".local", "state");
        }

        return Path.Combine(stateHome, "Monkasa", "logs");
    }
}
