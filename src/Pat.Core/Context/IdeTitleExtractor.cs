using System.Text.RegularExpressions;

namespace Pat.Core.Context;

/// <summary>
/// Достаёт «проект» и «файл» из заголовков IDE-стиля:
///   "Parser.cs — WaferAnalyzer — JetBrains Rider"
///   "MainWindow.xaml - WaferAnalyzer - Visual Studio Code"
/// </summary>
public static partial class IdeTitleExtractor
{
    [GeneratedRegex(@"^(?<file>.+?)\s+[—–-]{1,2}\s+(?<project>.+?)\s+[—–-]{1,2}\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex TwoSeparators();

    public sealed record ExtractedContext(string Project, string File);

    public static ExtractedContext? Extract(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
            return null;

        var match = TwoSeparators().Match(windowTitle);
        if (!match.Success)
            return null;

        var file = match.Groups["file"].Value.Trim();
        var project = match.Groups["project"].Value.Trim();
        if (file.Length == 0 || project.Length == 0 || file.Length > 260 || project.Length > 260)
            return null;

        return new ExtractedContext(project, file);
    }
}
