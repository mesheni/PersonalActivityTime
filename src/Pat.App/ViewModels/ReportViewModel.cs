using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core;
using Pat.Core.Categorization;
using Pat.Core.Narrative;
using Pat.Core.Reporting;
using Pat.Core.Sessionization;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Ollama;
using Pat.Infrastructure.Settings;

namespace Pat.App.ViewModels;

public sealed class NarrativeBlockVm
{
    public required string TimeRange { get; init; }
    public required string Duration { get; init; }
    public required string Title { get; init; }
    public required Brush Color { get; init; }
    public required string Details { get; init; }
}

/// <summary>
/// Вкладка «Отчёт»: нарратив дня (локально, всегда) + опциональный вопрос локальной
/// LLM через Ollama (выключено по умолчанию, только 127.0.0.1).
/// </summary>
public sealed partial class ReportViewModel(
    SessionRepository sessions,
    EventRepository events,
    SessionMaterializer materializer,
    SettingsStore settingsStore) : ObservableObject
{
    private DayReport _report = DayReport.Empty(0, 0);
    private DayNarrative _narrative = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, []);

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private ObservableCollection<NarrativeBlockVm> _blocks = [];

    [ObservableProperty] private bool _ollamaEnabled;
    [ObservableProperty] private string _ollamaHost = "";
    [ObservableProperty] private string _ollamaModel = "";
    [ObservableProperty] private string _ollamaStatus = "";
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _llmAnswer = "";
    [ObservableProperty] private bool _isBusy;

    public void Initialize()
    {
        var s = settingsStore.Settings;
        OllamaEnabled = s.OllamaEnabled;
        OllamaHost = s.OllamaHost;
        OllamaModel = s.OllamaModel;
    }

    [RelayCommand]
    private void SaveOllamaSettings()
    {
        var s = settingsStore.Settings;
        s.OllamaEnabled = OllamaEnabled;
        s.OllamaHost = string.IsNullOrWhiteSpace(OllamaHost) ? "http://127.0.0.1:11434" : OllamaHost.Trim();
        s.OllamaModel = OllamaModel.Trim();
        settingsStore.Save(s);
        OllamaStatus = "Настройки сохранены.";
    }

    [RelayCommand]
    private async Task CheckOllamaAsync()
    {
        OllamaStatus = "Проверяю…";
        var client = new OllamaClient(OllamaHost);
        if (!await client.IsAvailableAsync().ConfigureAwait(true))
        {
            OllamaStatus = $"Ollama недоступна на {OllamaHost}. Установите с ollama.com и запустите «ollama serve».";
            return;
        }
        var models = await client.ListModelsAsync().ConfigureAwait(true);
        OllamaStatus = models.Length == 0
            ? "Сервер отвечает, но моделей нет. Скачайте модель: ollama pull llama3.2"
            : $"Сервер отвечает. Модели: {string.Join(", ", models)}";
        if (models.Length > 0 && string.IsNullOrWhiteSpace(OllamaModel))
        {
            OllamaModel = models[0];
            OllamaStatus += $" — подставил «{models[0]}», не забудьте «Сохранить».";
        }
    }

    [RelayCommand]
    private async Task AskAsync()
    {
        if (IsBusy)
            return;
        if (!OllamaEnabled)
        {
            LlmAnswer = "";
            OllamaStatus = "Включите интеграцию галочкой выше и сохраните настройки.";
            return;
        }

        IsBusy = true;
        LlmAnswer = "";
        OllamaStatus = "Спрашиваю у локальной модели…";
        try
        {
            var digest = NarrativeDigest.Build(_report, _narrative);
            var userPrompt = string.IsNullOrWhiteSpace(Question)
                ? $"{digest}\n\nВопрос: расскажи, как прошёл мой день. Отметь главный фокус, самое длительное занятие и что заметил интересного."
                : $"{digest}\n\nВопрос: {Question.Trim()}";

            var client = new OllamaClient(OllamaHost);
            var answer = await client.ChatAsync(
                OllamaModel,
                "Ты — персональный аналитик активности пользователя за компьютером. " +
                "Тебе дают агрегированную статистику дня от локального трекера. " +
                "Отвечай по-русски, кратко, структурировано, только на основе данных — ничего не выдумывай.",
                userPrompt).ConfigureAwait(true);

            LlmAnswer = string.IsNullOrWhiteSpace(answer)
                ? "(пустой ответ модели)"
                : answer.Trim();
            OllamaStatus = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            OllamaStatus = $"Не удалось получить ответ от Ollama ({OllamaHost}). Проверьте, что сервер запущен.";
        }
        catch (Exception ex)
        {
            OllamaStatus = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Пересобрать нарратив из БД (при открытии вкладки).</summary>
    public void Load()
    {
        var (dayStart, dayEnd) = LocalDay.Today();
        var rules = materializer.CurrentRules();
        var rows = sessions.GetRange(dayStart, dayEnd, rules);
        var segments = rows.Select(r => new ActivitySegment(
            r.StartedUtcMs, r.EndedUtcMs, r.AppPath,
            r.AppName ?? "(исключено)", r.WindowTitle, r.IsIdle, r.IsExcluded)).ToList();

        _report = new DayReportBuilder().Build(segments, dayStart, dayEnd, rules);

        var browserFacts = events.GetBrowserVisits(dayStart, dayEnd, 500)
            .Select(v => new BrowserFact(v.TsUtcMs, v.Domain)).ToList();
        var fileFacts = events.GetFileEvents(dayStart, dayEnd, 500)
            .Select(f => new FileFact(f.TsUtcMs, f.Kind, f.Path)).ToList();

        _narrative = new NarrativeBuilder().Build(_report, browserFacts, fileFacts, rules);

        Headline = _narrative.Blocks.Count == 0
            ? "Пока нет активности — отрезки появятся по мере работы"
            : $"{_narrative.Blocks.Count} отрезков · активность {TimeText.Humanize(_narrative.Active)} · " +
              $"самый длинный {TimeText.Humanize(_narrative.LongestStreak)} · смен контекста {_narrative.ContextSwitches}";

        Blocks = new ObservableCollection<NarrativeBlockVm>(_narrative.Blocks.Select(b =>
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(b.StartUtcMs).ToLocalTime();
            var end = DateTimeOffset.FromUnixTimeMilliseconds(b.EndUtcMs).ToLocalTime();
            return new NarrativeBlockVm
            {
                TimeRange = $"{start:HH:mm}–{end:HH:mm}",
                Duration = TimeText.Humanize(b.Duration),
                Title = b.Title,
                Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(b.ColorHex)),
                Details = string.Join(" · ", b.Details),
            };
        }));
    }
}
