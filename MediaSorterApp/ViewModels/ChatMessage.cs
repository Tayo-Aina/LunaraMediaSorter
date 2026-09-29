using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MediaSorter.ViewModels;

public enum ChatRole
{
    Bot,
    User,
    System,
    Error,
    Success,
    Warning
}

/// <summary>A button rendered inline under a bot message.</summary>
public sealed class QuickReply
{
    public QuickReply(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
    public ICommand Command { get; set; } = null!;
}

/// <summary>A single chat bubble.</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private bool _hasReplies;

    public ChatMessage(ChatRole role, string text)
    {
        Role = role;
        Text = text;
    }

    public ChatRole Role { get; }
    public string Text { get; }
    public ObservableCollection<QuickReply> Replies { get; } = new();

    public bool HasReplies
    {
        get => _hasReplies;
        private set
        {
            if (_hasReplies == value)
                return;

            _hasReplies = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AddReply(QuickReply reply)
    {
        Replies.Add(reply);
        HasReplies = true;
    }

    public void ClearReplies()
    {
        Replies.Clear();
        HasReplies = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Minimal ICommand — no frameworks, no reflection.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
}
}
