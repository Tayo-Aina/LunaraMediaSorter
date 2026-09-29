using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MediaSorter.Engine;
using Microsoft.Win32;

namespace MediaSorter.ViewModels;

/// <summary>Everything the view needs: the transcript, the input box, and the progress strip.</summary>
public sealed class ChatViewModel : INotifyPropertyChanged
{
    private string _inputText = "";
    private string _stepLabel = "Starting…";
    private string _statusText = "";
    private string _progressText = "";
    private double _progressValue;
    private bool _showProgress;
    private bool _moveFiles = true;
    private bool _includeSubfolders = true;
    private bool _cancelEnabled;
    private bool _showSupport;

    /// <summary>
    /// Verified bank rows, or null when the payload failed its integrity
    /// check (tampered build). Null hides the section everywhere.
    /// </summary>
    private readonly IReadOnlyList<SupportBank>? _support = SupportInfo.Load();

    public ChatViewModel()
    {
        SendCommand = new RelayCommand(Send);
        BrowseCommand = new RelayCommand(Browse);
        CancelCommand = new RelayCommand(() => Session?.OnCancelClicked());
    }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>Set once by the window; typed input is routed here.</summary>
    public ChatSession? Session { get; set; }

    public ICommand SendCommand { get; }
    public ICommand BrowseCommand { get; }

    /// <summary>Persistent "Cancel run" button in the input row (same as /cancel).</summary>
    public ICommand CancelCommand { get; }

    /// <summary>True when the support details passed their integrity check.</summary>
    public bool HasSupport => _support is not null;

    /// <summary>Bank rows shown in the support section (empty when unavailable).</summary>
    public IReadOnlyList<SupportBank> SupportBanks => _support ?? Array.Empty<SupportBank>();

    /// <summary>Open state of the support section; forced closed on a tampered build.</summary>
    public bool ShowSupport
    {
        get => _showSupport;
        set
        {
            if (!HasSupport)
                value = false;

            Set(ref _showSupport, value);
        }
    }

    /// <summary>True while a run is in progress — lights up the persistent cancel button.</summary>
    public bool CancelEnabled
    {
        get => _cancelEnabled;
        set => Set(ref _cancelEnabled, value);
    }

    public string InputText
    {
        get => _inputText;
        set => Set(ref _inputText, value ?? "");
    }

    public string StepLabel
    {
        get => _stepLabel;
        set => Set(ref _stepLabel, value);
    }

    public bool ShowProgress
    {
        get => _showProgress;
        set => Set(ref _showProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    /// <summary>0 – 100.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (!Set(ref _progressValue, value))
                return;

            OnPropertyChanged(nameof(ProgressGridWidth));
            ProgressText = $"{value:0}%";
        }
    }

    /// <summary>Column 1 of the progress track, sized as a percentage of the bar.</summary>
    public GridLength ProgressGridWidth => new(Math.Clamp(_progressValue, 0, 100), GridUnitType.Star);

    public bool MoveFiles
    {
        get => _moveFiles;
        set
        {
            if (Set(ref _moveFiles, value))
                OnPropertyChanged(nameof(ModeLabel));
        }
    }

    public string ModeLabel => _moveFiles ? "Move" : "Copy";

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set => Set(ref _includeSubfolders, value);
    }

    // ---------------------------------------------------------------- messages

    public ChatMessage Add(string text, ChatRole role)
    {
        var message = new ChatMessage(role, text);
        Messages.Add(message);
        return message;
    }

    public ChatMessage Bot(string text) => Add(text, ChatRole.Bot);

    public void User(string text) => Add(text, ChatRole.User);

    /// <summary>
    /// Shows the progress strip. <paramref name="percent"/> is 0-100 for the bar;
    /// <paramref name="detailText"/> overrides the automatic "NN%" label when provided
    /// (pass an empty string when no percentage is meaningful yet).
    /// </summary>
    public void SetProgress(double percent, string status, string? detailText = null)
    {
        ShowProgress = true;
        StatusText = status;
        ProgressValue = percent;

        if (detailText is not null)
            ProgressText = detailText;
    }

    public void ClearProgress()
    {
        ShowProgress = false;
        StatusText = "";
        ProgressValue = 0;
        ProgressText = "";
    }

    // ---------------------------------------------------------------- commands

    private void Send()
    {
        var text = InputText;
        InputText = "";

        if (string.IsNullOrWhiteSpace(text))
            return;

        Session?.OnTextInput(text.Trim());
    }

    /// <summary>Called by the window when a folder was dropped or browsed to.</summary>
    public void SubmitPath(string path)
    {
        if (Session is { AcceptsTypedAnswer: true })
        {
            InputText = "";
            Session.OnTextInput(path);
            return;
        }

        InputText = path;
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(InputText))
        {
            var guess = File.Exists(InputText) ? Path.GetDirectoryName(InputText) : InputText;
            if (Directory.Exists(guess))
                dialog.InitialDirectory = guess;
        }

        if (dialog.ShowDialog() != true)
            return;

        SubmitPath(dialog.FolderName);
    }

    // ---------------------------------------------------------------- plumbing

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
