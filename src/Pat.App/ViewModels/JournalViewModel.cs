using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.Core;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Settings;

namespace Pat.App.ViewModels;

public sealed record EventFilter(string Name, int Kind);

public sealed partial class JournalViewModel(JournalReader reader, EventRepository events,
    DayContext context, SettingsStore settings, Services.BrowserIntegrationService browser) : ObservableObject
{
    private readonly LatestLoad _load = new();
    private int _limit = 100;
    public IReadOnlyList<EventFilter> EventFilters { get; } = [new("Все события", 0),
        new("Запуск процесса",10), new("Завершение процесса",11), new("Создание файла",13),
        new("Изменение файла",14), new("Удаление файла",15), new("Переименование файла",16), new("Визит",17)];
    [ObservableProperty] private int _group;
    [ObservableProperty] private int _eventKind;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private ObservableCollection<JournalEntry> _entries = [];
    [ObservableProperty] private JournalEntry? _selectedEntry;
    [ObservableProperty] private string _integrationStatus = "";
    public IReadOnlyList<EventRepository.MetricSampleEntry> MetricSamples { get; private set; } = [];
    public event Action? MetricsLoaded;
    public bool IsMetrics => Group == 4;
    public string MetricsStatus => MetricSamples.Count < 2 ? "Недостаточно измерений для графика. Измерения появляются во время записи." : $"Измерений: {MetricSamples.Count}";
    partial void OnGroupChanged(int value) { OnPropertyChanged(nameof(IsMetrics)); Reset(); }
    partial void OnEventKindChanged(int value) => Reset();
    partial void OnSearchChanged(string value) => Reset();
    private async void Reset()
    {
        _limit = 100;
        await LoadAsync(debounce: true);
    }
    public void ClearDay()
    {
        _load.Cancel(); IsLoading = false; Entries = []; SelectedEntry = null;
        MetricSamples = []; HasMore = false; _limit = 100;
        OnPropertyChanged(nameof(MetricsStatus)); MetricsLoaded?.Invoke();
    }
    public void CancelLoad() { _load.Cancel(); IsLoading = false; }
    [RelayCommand] private async Task LoadMoreAsync() { _limit += 100; await LoadAsync(); }
    public async Task LoadAsync(bool debounce = false)
    {
        var request = _load.Begin();
        var date = context.SelectedDate;
        var group = Group;
        var kind = EventKind;
        var search = Search;
        var limit = _limit;
        IsLoading = true;
        Status = "Загрузка журнала…";
        try
        {
            if (debounce) await Task.Delay(250, request.Token);
            var (start, end) = LocalDay.Bounds(date);
            end = Math.Min(end, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var result = await Task.Run(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                var page = group == 4 ? new JournalPage([], 0) : reader.Read(start, end, group, kind, search, limit);
                request.Token.ThrowIfCancellationRequested();
                var samples = group == 4 ? events.GetMetricSamples(start, end) : [];
                var configured = browser.IsInstalled;
                var visits = group == 3 ? events.GetBrowserVisits(start, end, 1).Count > 0 : false;
                return (page, samples, configured, visits);
            }, request.Token);
            if (!_load.IsCurrent(request) || date != context.SelectedDate) return;
            var selectedId = SelectedEntry?.Id;
            CollectionUpdates.Reconcile(Entries, result.page.Entries, e => e.Id);
            SelectedEntry = Entries.FirstOrDefault(e => e.Id == selectedId);
            HasMore = Entries.Count < result.page.Total;
            Status = group == 4 ? "CPU и RAM за выбранный день" : $"Показано {Entries.Count} из {result.page.Total}" +
                (Entries.Count == 0 ? " · Событий по выбранным фильтрам нет" : "");
            MetricSamples = result.samples;
            OnPropertyChanged(nameof(MetricsStatus));
            MetricsLoaded?.Invoke();
            IntegrationStatus = group == 3 ? result.visits
                ? "В выбранный день есть визиты. Это не подтверждает текущее подключение расширения."
                : result.configured ? "Хост зарегистрирован; подключение расширения не подтверждено. За этот день визитов нет."
                : "Хост браузера не зарегистрирован. Подключите расширение в настройках." :
                group == 2 && settings.Settings.WatchedRoots.Count == 0 ? "Папки не выбраны. Добавьте их в настройках учёта активности." : "";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (_load.IsCurrent(request)) Status = "Не удалось прочитать журнал: " + ex.Message; }
        finally { if (_load.IsCurrent(request)) IsLoading = false; _load.End(request); }
    }
}
