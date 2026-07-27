using System.IO;
using Microsoft.Win32;

namespace KeList.Services;

public static class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "keList";
    private const string LegacyValueName = "TodoList";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key?.GetValue(ValueName) is string command)
        {
            return IsRegisteredExecutableAvailable(command);
        }

        if (key?.GetValue(LegacyValueName) is not string)
        {
            return false;
        }

        SetEnabled(true);
        return true;
    }

    public static bool Synchronize(bool requested)
    {
        var registered = IsEnabled();
        if (registered)
        {
            return true;
        }

        if (!requested)
        {
            SetEnabled(false);
            return false;
        }

        SetEnabled(true);
        return true;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);

        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("??????????????);
            var normalizedPath = Path.GetFullPath(executablePath);
            key.SetValue(ValueName, $"\"{normalizedPath}\"");
            key.DeleteValue(LegacyValueName, false);
        }
        else
        {
            key.DeleteValue(ValueName, false);
            key.DeleteValue(LegacyValueName, false);
        }
    }

    private static bool IsRegisteredExecutableAvailable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string executablePath;
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            executablePath = trimmed[1..closingQuote];
        }
        else
        {
            var firstSpace = trimmed.IndexOf(' ');
            executablePath = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
        }

        return File.Exists(executablePath);
    }
}
