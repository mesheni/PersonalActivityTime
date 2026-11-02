using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.App.Services;
using Pat.App.Tracking;
using Pat.Core;

namespace Pat.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    public DayContext Day { get; }
    public TodayViewModel Overview { get; }
    public JournalViewModel Journal { get; }
    public StatsViewModel Stats { get; }
    public SettingsViewModel Settings { get; }
    public ReportViewModel Assistant { get; }
    private readonly TrackingService _tracking;
    private readonly ExportService _export;
    [ObservableProperty] private int _selectedSection;
    [ObservableProperty] private string _trackingLabel = "";
    [ObservableProperty] private string _pauseLabel = "";
    [ObservableProperty] private string _operationStatus = "";
    public bool ShowsDay => SelectedSection is 0 or 1;
    public string SectionTitle => SelectedSection switch { 0 => "Обзор дня", 1 => "Журнал активности", 2 => "Статистика", _ => "Настройки" };
    public ShellViewModel(DayContext day, TodayViewModel overview, JournalViewModel journal, StatsViewModel stats,
        SettingsViewModel settings, ReportViewModel assistant, TrackingService tracking, ExportService export)
    {
        Day = day; Overview = overview; Journal = journal; Stats = stats; Settings = settings;
        Assistant = assistant; _tracking = tracking; _export = export;
        day.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(DayContext.SelectedDate)) return;
            Overview.ClearDay(); Journal.ClearDay();
            OperationStatus = "";
            await RefreshVisibleAsync();
        };
        tracking.StateChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTracking);
        stats.DayRequested += OpenDay;
        settings.Saved += () => { Assistant.InvalidateDay(); Overview.ClearDay(); Journal.ClearDay(); };
        UpdateTracking();
    }
    private void UpdateTracking()
    {
        TrackingLabel = _tracking.StorageError is { } error ? "Ошибка записи: " + error
            : !_tracking.IsStarted ? "Запись остановлена — проверьте настройки"
            : _tracking.State == TrackingState.Running ? "Запись идёт" : "Запись на паузе";
        PauseLabel = _tracking.State == TrackingState.Running ? "Приостановить запись" : "Продолжить запись";
    }
    partial void OnSelectedSectionChanged(int value)
    {
        Overview.CancelLoad(); Journal.CancelLoad(); Stats.CancelLoad();
        OnPropertyChanged(nameof(ShowsDay)); OnPropertyChanged(nameof(SectionTitle));
        RefreshCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
        OperationStatus = "";
        _ = RefreshVisibleAsync();
    }
    public void OpenDay(DateTime date)
    {
        Day.SelectedDate = date;
        SelectedSection = 0;
    }
    public Task RefreshVisibleAsync() => SelectedSection switch
    {
        0 => Overview.LoadAsync(), 1 => Journal.LoadAsync(), 2 => Stats.LoadAsync(), _ => Task.CompletedTask,
    };
    private bool CanReadData() => SelectedSection != 3;
    [RelayCommand(CanExecute = nameof(CanReadData))] private async Task RefreshAsync()
    {
        try
        {
            if (SelectedSection != 3 && (Day.IsToday || SelectedSection == 2)) await _tracking.RefreshSessionsAsync();
            await RefreshVisibleAsync();
        }
        catch (Exception ex) { OperationStatus = "Не удалось обновить данные: " + ex.Message; }
    }
    [RelayCommand] private async Task TogglePauseAsync()
    {
        try { await _tracking.TogglePauseAsync(); await RefreshVisibleAsync(); }
        catch (Exception ex) { OperationStatus = "Не удалось изменить состояние записи: " + ex.Message; }
        finally { UpdateTracking(); }
    }
    [RelayCommand(CanExecute = nameof(CanReadData))] private async Task ExportAsync()
    {
        try
        {
            if (SelectedSection == 3) { OperationStatus = "Откройте обзор, журнал или статистику для экспорта."; return; }
            if (SelectedSection == 2)
            {
                if (Stats.IsLoading || Stats.LoadedRange is null) { OperationStatus = "Дождитесь загрузки периода."; return; }
                var range = Stats.LoadedRange.Value;
                OperationStatus = (await _export.ExportAsync(range.Start, range.End, Stats.RangeLabel)).Message;
            }
            else
            {
                var (start, end) = LocalDay.Bounds(Day.SelectedDate);
                end = Math.Min(end, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                OperationStatus = (await _export.ExportAsync(start, end, Day.SelectedDate.ToString("dd.MM.yyyy"))).Message;
            }
        }
        catch (Exception ex) { OperationStatus = "Не удалось экспортировать данные: " + ex.Message; }
    }
}
