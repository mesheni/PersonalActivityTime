using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pat.App.Services;
using Pat.App.Tracking;
using Pat.Infrastructure;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Ollama;
using Pat.Infrastructure.Settings;

namespace Pat.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly TrackingService _tracking;
    private readonly FileActivityCollector _files;
    private readonly BrowserIntegrationService _browser;
    private readonly RetentionService _retention;
    private bool _initializing;
    private string _baseline = "";
    [ObservableProperty] private string _idleThresholdMinutes = "5";
    [ObservableProperty] private string _excludedText = "";
    [ObservableProperty] private string _watchedRootsText = "";
    [ObservableProperty] private string _extensionId = "";
    [ObservableProperty] private bool _autoStartEnabled;
    [ObservableProperty] private string _retentionDays = "180";
    [ObservableProperty] private bool _ollamaEnabled;
    [ObservableProperty] private string _ollamaHost = "";
    [ObservableProperty] private string _ollamaModel = "";
    [ObservableProperty] private ObservableCollection<string> _models = [];
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _browserStatus = "";
    [ObservableProperty] private string _ollamaStatus = "";
    [ObservableProperty] private string _rootsError = "";
    [ObservableProperty] private string _extensionError = "";
    [ObservableProperty] private string _hostError = "";
    [ObservableProperty] private string _generalError = "";
    public string DirtyLabel => IsDirty ? "Есть несохранённые изменения" : "Все изменения сохранены";
    public string ProcessingAddress => "Обработка статистики: " + OllamaHost + " · разрешены только локальные IP-адреса";
    public event Action? Saved;

    private static readonly HashSet<string> DraftProperties = [nameof(IdleThresholdMinutes), nameof(ExcludedText),
        nameof(WatchedRootsText), nameof(ExtensionId), nameof(AutoStartEnabled), nameof(RetentionDays),
        nameof(OllamaEnabled), nameof(OllamaHost), nameof(OllamaModel)];

    public SettingsViewModel(SettingsStore store, TrackingService tracking, FileActivityCollector files,
        BrowserIntegrationService browser, RetentionService retention)
    {
        _store = store; _tracking = tracking; _files = files; _browser = browser; _retention = retention;
        PropertyChanged += (_, e) =>
        {
            if (_initializing || !DraftProperties.Contains(e.PropertyName ?? "")) return;
            IsDirty = Fingerprint() != _baseline;
            OnPropertyChanged(nameof(ProcessingAddress));
        };
        Reset();
    }
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DirtyLabel));
    private string Fingerprint() => JsonSerializer.Serialize(new { IdleThresholdMinutes, ExcludedText, WatchedRootsText,
        ExtensionId, AutoStartEnabled, RetentionDays, OllamaEnabled, OllamaHost, OllamaModel });
    [RelayCommand] private void Reset()
    {
        _initializing = true;
        var s = _store.Settings;
        IdleThresholdMinutes = (s.IdleThresholdSeconds / 60).ToString();
        ExcludedText = string.Join(Environment.NewLine, s.ExcludedProcesses);
        WatchedRootsText = string.Join(Environment.NewLine, s.WatchedRoots);
        ExtensionId = s.BrowserExtensionId ?? "";
        AutoStartEnabled = s.StartWithWindows;
        RetentionDays = s.RetentionDays.ToString();
        OllamaEnabled = s.OllamaEnabled; OllamaHost = s.OllamaHost; OllamaModel = s.OllamaModel;
        Models = string.IsNullOrWhiteSpace(s.OllamaModel) ? [] : new([s.OllamaModel]);
        _baseline = Fingerprint(); IsDirty = false;
        RootsError = ExtensionError = HostError = GeneralError = "";
        Status = _store.LastError ?? "Настройки загружены.";
        UpdateBrowserStatus();
        _initializing = false;
        OnPropertyChanged(nameof(ProcessingAddress));
    }

    public static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private bool Validate()
    {
        var invalid = Lines(WatchedRootsText).Where(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p)).ToList();
        RootsError = invalid.Count > 0 ? "Папка не найдена или путь не полный: " + string.Join(", ", invalid) : "";
        ExtensionError = ExtensionId.Trim().Length > 0 && !Regex.IsMatch(ExtensionId.Trim(), "^[a-p]{32}$")
            ? "ID должен содержать 32 буквы a–p." : "";
        try { OllamaClient.ValidateHost(OllamaHost); HostError = ""; }
        catch (ArgumentException ex) { HostError = ex.Message; }
        GeneralError = !int.TryParse(IdleThresholdMinutes, out var minutes) || minutes is < 1 or > 1440
            || !int.TryParse(RetentionDays, out var retention) || retention < 0
            ? "Простой: от 1 до 1440 минут; срок хранения: 0 или больше дней." : "";
        if (OllamaEnabled && string.IsNullOrWhiteSpace(OllamaModel)) GeneralError = "Выберите модель Ollama или отключите интеграцию.";
        return RootsError.Length + ExtensionError.Length + HostError.Length + GeneralError.Length == 0;
    }
    [RelayCommand] private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Папки для наблюдения", Multiselect = true };
        if (dialog.ShowDialog() == true)
            WatchedRootsText = string.Join(Environment.NewLine, Lines(WatchedRootsText).Concat(dialog.FolderNames).Distinct(StringComparer.OrdinalIgnoreCase));
    }
    [RelayCommand] private async Task SaveAsync()
    {
        if (!Validate()) { Status = "Исправьте отмеченные поля."; return; }
        IsBusy = true;
        try
        {
            var s = _store.Settings;
            s.IdleThresholdSeconds = int.Parse(IdleThresholdMinutes) * 60;
            s.ExcludedProcesses = Lines(ExcludedText).Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p).ToList();
            s.WatchedRoots = [.. Lines(WatchedRootsText)];
            s.BrowserExtensionId = string.IsNullOrWhiteSpace(ExtensionId) ? null : ExtensionId.Trim();
            s.StartWithWindows = AutoStartEnabled; s.RetentionDays = int.Parse(RetentionDays);
            s.OllamaEnabled = OllamaEnabled; s.OllamaHost = OllamaHost.Trim(); s.OllamaModel = OllamaModel?.Trim() ?? "";
            var result = await Task.Run(() => _store.TrySave(s));
            Status = result.Message;
            if (!result.IsSuccess) return;
            _baseline = Fingerprint(); IsDirty = false;
            try
            {
                await Task.Run(() => { AutoStart.SetEnabled(s.StartWithWindows); _files.Reconfigure(); });
                if (!_tracking.IsStarted) _tracking.Start();
                await _tracking.RefreshSessionsAsync();
            }
            catch (Exception ex) { Status = "Настройки сохранены, но применены не полностью: " + ex.Message + ". Повторите сохранение."; }
            Saved?.Invoke();
        }
        catch (Exception ex) { Status = "Не удалось сохранить настройки: " + ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task CheckOllamaAsync()
    {
        var host = OllamaHost;
        OllamaStatus = "Получение списка моделей…";
        try
        {
            var models = await new OllamaClient(host).ListModelsAsync();
            if (host != OllamaHost) return;
            Models = new(models);
            if (models.Length > 0 && !models.Contains(OllamaModel)) OllamaModel = models[0];
            OllamaStatus = models.Length == 0 ? "Сервер отвечает, но моделей нет. Установите модель в Ollama."
                : "Подключение проверено. Выберите модель и сохраните настройки.";
        }
        catch (Exception ex) { if (host == OllamaHost) OllamaStatus = "Не удалось подключиться: " + ex.Message; }
    }
    [RelayCommand] private async Task InstallBrowserAsync()
    {
        try
        {
            var id = ExtensionId;
            var result = await Task.Run(() => _browser.Install(id));
            BrowserStatus = result.Message;
        }
        catch (Exception ex) { BrowserStatus = "Не удалось зарегистрировать хост: " + ex.Message; }
    }
    [RelayCommand] private async Task UninstallBrowserAsync()
    {
        var result = await Task.Run(() => _browser.Uninstall());
        BrowserStatus = result.Message;
    }
    private void UpdateBrowserStatus()
    {
        try { BrowserStatus = _browser.IsInstalled ? "Хост зарегистрирован. Подключение расширения не подтверждено; проверьте визиты в журнале."
            : "Хост ещё не зарегистрирован."; }
        catch (Exception ex) { BrowserStatus = "Статус регистрации недоступен: " + ex.Message; }
    }
    [RelayCommand] private async Task PurgeAsync()
    {
        if (IsDirty) { Status = "Сначала сохраните или отмените изменения настроек."; return; }
        var days = _store.Settings.RetentionDays;
        if (days == 0) { Status = "Выбрано бессрочное хранение; очистка отключена."; return; }
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)days * 86_400_000;
        var cutoffDate = DateTimeOffset.FromUnixTimeMilliseconds(cutoff).ToLocalTime();
        if (MessageBox.Show($"Безвозвратно удалить события и завершённые сессии старше {cutoffDate:dd.MM.yyyy HH:mm}?\nСохранённый срок: {days} дней.",
            "Очистка старых данных", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        { Status = "Очистка отменена."; return; }
        IsBusy = true;
        try { Status = (await Task.Run(() => _retention.PurgeBefore(cutoff))).Message; Saved?.Invoke(); }
        finally { IsBusy = false; }
    }
}
