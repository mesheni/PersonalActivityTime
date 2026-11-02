using System.IO;
using System.Text;
using Microsoft.Win32;
using Pat.Core.Export;
using Pat.Infrastructure.Database;

namespace Pat.App.Services;

/// <summary>
/// Экспорт сессий за период в CSV или JSON через диалог сохранения.
/// Формат строит Pat.Core.Export, здесь только выбор файла и запись (UTF-8 с BOM,
/// чтобы Excel корректно открыл кириллицу).
/// </summary>
public sealed class ExportService(SessionMaterializer materializer)
{
    public void ExportRange(string filePrefix, long fromUtcMs, long toUtcMs)
    {
        var rows = materializer.BuildRows(fromUtcMs, Math.Min(toUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var data = rows.Select(r => new ExportSession(
            r.StartedUtcMs, r.EndedUtcMs,
            r.AppName ?? "(исключено)", r.WindowTitle, r.Project,
            r.CategoryName, r.IsIdle, r.IsExcluded)).ToList();

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт активности",
            Filter = "CSV (Excel)|*.csv|JSON|*.json",
            FilterIndex = 1,
            FileName = $"{filePrefix}_{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog() != true)
            return;

        var content = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? ActivityExporter.ToJson(data)
            : ActivityExporter.ToCsv(data);
        File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        System.Windows.MessageBox.Show(
            $"Экспортировано сессий: {data.Count}\n{dialog.FileName}",
            "Экспорт", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
