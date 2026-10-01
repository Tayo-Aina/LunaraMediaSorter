using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MediaSorter.ViewModels;

namespace MediaSorter;

public partial class MainWindow : Window
{
    private const double BottomTolerance = 60;

    private readonly ChatViewModel _vm;
    private readonly ChatSession _session;
    private ScrollViewer? _scroller;
    private bool _pinnedToBottom = true;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new ChatViewModel();
        _session = new ChatSession(_vm);
        _vm.Session = _session;
        DataContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        ChatList.SizeChanged += OnTranscriptSizeChanged;
        Loaded += OnWindowLoaded;
    }

    // ------------------------------------------------------------------ flow

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        ApplyDarkTitleBar();
        ScrollToBottom();
        InputBox.Focus();
        _session.StartFlow();
    }

    /// <summary>Match the OS title bar to the app's dark palette.</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var dark = 1;

            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 11, 19 on Windows 10 1809+.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows: keep the stock title bar.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // -------------------------------------------------------------- scrolling

    private ScrollViewer? GetScroller()
    {
        if (_scroller is null)
        {
            // FindName("PART_ScrollViewer") does not resolve against the theme
            // namescope, so walk the visual tree instead.
            var scroller = FindScroller(ChatList);

            if (scroller is not null)
            {
                _scroller = scroller;
                scroller.ScrollChanged += OnScrollChanged;
            }
        }

        return _scroller;
    }

    private static ScrollViewer? FindScroller(DependencyObject node)
    {
        if (node is ScrollViewer scroller)
            return scroller;

        var count = VisualTreeHelper.GetChildrenCount(node);

        for (var i = 0; i < count; i++)
        {
            var found = FindScroller(VisualTreeHelper.GetChild(node, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private void ScrollToBottom()
    {
        if (GetScroller() is not { } scroller)
            return;

        ChatList.UpdateLayout();
        scroller.ScrollToEnd();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only user-initiated scrolling (or our own ScrollToEnd) changes the offset;
        // content growing underneath an idle viewport must not un-pin the view.
        if (e.VerticalChange == 0 || GetScroller() is not { } scroller)
            return;

        var distance = scroller.ExtentHeight - scroller.VerticalOffset - scroller.ViewportHeight;
        _pinnedToBottom = distance <= BottomTolerance;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
            return;

        if (_pinnedToBottom)
            Dispatcher.InvokeAsync(ScrollToBottom);
    }

    private void OnTranscriptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pinnedToBottom)
            ScrollToBottom();
    }

    // ------------------------------------------------------------------ input

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        _vm.SendCommand.Execute(null);
    }

    // --------------------------------------------------------------- support

    /// <summary>Copy button inside the support section (copies the bank number).</summary>
    private void OnCopyAccountNumber(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string number || number.Length == 0)
            return;

        try
        {
            Clipboard.SetText(number);
        }
        catch
        {
            // Clipboard can be busy for a moment; nothing useful to tell the user.
            return;
        }

        // Brief "Copied" confirmation on the button itself.
        button.Content = "Copied";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            button.Content = "Copy number";
            timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Open button inside the support section (opens the sealed source link).</summary>
    private void OnOpenRepository(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string url || url.Length == 0)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // No browser registered, or the machine blocks it: nothing useful to tell the user.
            return;
        }

        // Brief "Opened" confirmation on the button itself.
        button.Content = "Opened";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            button.Content = "Open";
            timer.Stop();
        };
        timer.Start();
    }

    // ------------------------------------------------------------- drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        _vm.SubmitPath(paths[0]);
        InputBox.Focus();
    }
}
