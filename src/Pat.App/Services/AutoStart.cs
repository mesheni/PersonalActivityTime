using Microsoft.Win32;

namespace Pat.App.Services;

/// <summary>Автозапуск через HKCU\...\Run — админ-права не нужны.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PersonalActivityTime";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}
