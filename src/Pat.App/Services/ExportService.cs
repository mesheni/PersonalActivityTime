using System.IO;
using System.Text;
using Microsoft.Win32;
using Pat.Core.Export;
using Pat.Infrastructure;
using Pat.Infrastructure.Database;

namespace Pat.App.Services;

public sealed class ExportService(SessionMaterializer materializer)
{
    public async Task<OperationResult> ExportAsync(long fromUtcMs, long toUtcMs, string rangeLabel)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromUtcMs).ToLocalTime();
        var to = DateTimeOffset.FromUnixTimeMilliseconds(toUtcMs - 1).ToLocalTime();
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт активности · " + rangeLabel,
            Filter = "CSV (Excel)|*.csv|JSON|*.json", FilterIndex = 1,
            FileName = $"activity_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog() != true) return new(OperationStatus.Cancelled, "Экспорт отменён.");
        return await Task.Run(() => WriteRange(dialog.FileName, fromUtcMs, toUtcMs));
    }

    public OperationResult WriteRange(string path, long fromUtcMs, long toUtcMs)
    {
        try
        {
            var rows = materializer.BuildRows(fromUtcMs, Math.Min(toUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var data = rows.Select(r => new ExportSession(r.StartedUtcMs, r.EndedUtcMs,
                r.AppName ?? "(исключено)", r.WindowTitle, r.Project, r.CategoryName, r.IsIdle, r.IsExcluded)).ToList();
            if (data.Count == 0) return new(OperationStatus.NoChanges, "За выбранный период нет сессий для экспорта.");
            var content = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ActivityExporter.ToJson(data) : ActivityExporter.ToCsv(data);
            File.WriteAllText(path, content, new UTF8Encoding(true));
            return new(OperationStatus.Success, $"Экспортировано сессий: {data.Count} · {path}", path);
        }
        catch (Exception ex) { return OperationResult.Failure(ex); }
    }
}
