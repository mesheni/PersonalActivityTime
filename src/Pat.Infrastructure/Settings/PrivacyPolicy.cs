namespace Pat.Infrastructure.Settings;

public static class PrivacyPolicy
{
    public static bool IsExcluded(PatSettings settings, string? processName) => processName is not null &&
        settings.ExcludedProcesses.Any(p => string.Equals(Normalize(p), Normalize(processName), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string name)
    {
        var file = Path.GetFileName(name.Trim());
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }
}
