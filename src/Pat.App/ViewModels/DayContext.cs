using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pat.App.ViewModels;

public sealed partial class DayContext : ObservableObject
{
    private DateTime _selectedDate = DateTime.Today;
    public DateTime Today => DateTime.Today;
    public bool IsToday => SelectedDate == Today;
    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            var date = value.Date > Today ? Today : value.Date;
            if (!SetProperty(ref _selectedDate, date)) return;
            OnPropertyChanged(nameof(IsToday));
            NextDayCommand.NotifyCanExecuteChanged();
        }
    }

    public void UpdateToday()
    {
        OnPropertyChanged(nameof(Today));
        OnPropertyChanged(nameof(IsToday));
        NextDayCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void PreviousDay() => SelectedDate = SelectedDate.AddDays(-1);
    [RelayCommand(CanExecute = nameof(CanGoNext))] private void NextDay() => SelectedDate = SelectedDate.AddDays(1);
    private bool CanGoNext() => SelectedDate < Today;
    [RelayCommand] private void GoToday() => SelectedDate = Today;
}
