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
        if (key?.GetValue(ValueName) is string)
        {
            return true;
        }

        if (key?.GetValue(LegacyValueName) is not string)
        {
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
                ?? throw new InvalidOperationException("Unable to determine executable path.");
            key.SetValue(ValueName, $"\"{executablePath}\"");
            key.DeleteValue(LegacyValueName, false);
        }
        else
        {
            key.DeleteValue(ValueName, false);
            key.DeleteValue(LegacyValueName, false);
        }
    }
}
