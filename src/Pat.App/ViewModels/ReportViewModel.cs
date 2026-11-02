using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core.Narrative;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Ollama;
using Pat.Infrastructure.Settings;

namespace Pat.App.ViewModels;

/// <summary>Optional assistant for the selected day. Uses only saved integration settings.</summary>
public sealed partial class ReportViewModel(SettingsStore settings, DayContext context) : ObservableObject
{
    private DayActivity? _day;
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _llmAnswer = "";
    [ObservableProperty] private string _status = "Настройка Ollama находится в разделе «Настройки».";
    [ObservableProperty] private bool _isBusy;

    public void SetDay(DayActivity day)
    {
        if (_day?.Date != day.Date) InvalidateDay();
        _day = day;
    }
    public void InvalidateDay()
    {
        AskCommand.Cancel();
        _day = null;
        LlmAnswer = "";
        Status = "Модель получает агрегированную статистику выбранного дня. Настройка — в разделе «Настройки».";
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AskAsync(CancellationToken cancellationToken)
    {
        var day = _day;
        var s = settings.Settings;
        if (!s.OllamaEnabled) { Status = "Включите Ollama в настройках и сохраните изменения."; return; }
        if (day is null || day.Date != context.SelectedDate) { Status = "Сначала дождитесь загрузки выбранного дня."; return; }
        if (string.IsNullOrWhiteSpace(s.OllamaModel)) { Status = "Выберите модель в настройках Ollama."; return; }
        IsBusy = true;
        Status = $"Запрос за {day.Date:dd.MM.yyyy} → {s.OllamaHost}. Можно отменить ожидание.";
        try
        {
            var digest = NarrativeDigest.Build(day.Report, day.Narrative);
            var answer = await new OllamaClient(s.OllamaHost).ChatAsync(s.OllamaModel,
                "Ты — персональный аналитик активности. Отвечай по-русски, кратко, только на основании данных. Не выдумывай факты.",
                $"Дата: {day.Date:yyyy-MM-dd}\n{digest}\nВопрос: " +
                (string.IsNullOrWhiteSpace(Question) ? "Расскажи, на что ушло время в этот день." : Question.Trim()), cancellationToken);
            if (cancellationToken.IsCancellationRequested || day.Date != context.SelectedDate) return;
            LlmAnswer = string.IsNullOrWhiteSpace(answer) ? "Модель вернула пустой ответ. Повторите запрос." : answer.Trim();
            Status = $"Ответ за {day.Date:dd.MM.yyyy} · обработано на {s.OllamaHost}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { if (day.Date == context.SelectedDate) Status = "Запрос отменён. Можно повторить."; }
        catch (OperationCanceledException)
        { if (day.Date == context.SelectedDate) Status = "Время ожидания истекло. Проверьте модель и повторите запрос."; }
        catch (Exception ex)
        { if (day.Date == context.SelectedDate) Status = "Ollama не ответила: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
