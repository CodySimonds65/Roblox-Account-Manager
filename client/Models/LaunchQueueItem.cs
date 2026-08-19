using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RobloxAltClient.Models;

public enum LaunchQueueState
{
    Waiting,
    Preparing,
    Launching,
    Running,
    Exited,
    Failed,
    Canceled
}

public sealed class LaunchQueueItem(AccountProfile account) : INotifyPropertyChanged
{
    private LaunchQueueState _state = LaunchQueueState.Waiting;
    private string _detail = "Waiting";

    public AccountProfile Account { get; } = account;

    public string Label => Account.Label;

    public LaunchQueueState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
        }
    }

    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value)
            {
                return;
            }

            _detail = value;
            OnPropertyChanged();
        }
    }

    public string Status => State switch
    {
        LaunchQueueState.Waiting => "WAITING",
        LaunchQueueState.Preparing => "PREPARING",
        LaunchQueueState.Launching => "LAUNCHING",
        LaunchQueueState.Running => "RUNNING",
        LaunchQueueState.Exited => "EXITED",
        LaunchQueueState.Failed => "FAILED",
        LaunchQueueState.Canceled => "CANCELED",
        _ => State.ToString().ToUpperInvariant()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
