using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KeList.Interop;
using KeList.Models;
using KeList.Services;
using Forms = System.Windows.Forms;

namespace KeList;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int TogglePassThroughHotkeyId = 0x5444;

    private readonly StorageService _storage = new();
    private readonly ObservableCollection<TodoItem> _items;
    private readonly AppData _appData;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayShowItem;
    private readonly Forms.ToolStripMenuItem _trayTopmostItem;
    private readonly Forms.ToolStripMenuItem _trayPassThroughItem;
    private readonly Forms.ToolStripMenuItem _trayStartupItem;

    private HwndSource? _windowSource;
    private nint _windowHandle;
    private bool _isPassThrough;
    private bool _allowExit;
    private bool _isLoaded;
    private System.Windows.Point _dragStart;
    private TodoItem? _undoItem;
    private int _undoIndex;
    private double _itemFontSize = 16;
    private int _completedCount;

    public MainWindow()
    {
        _appData = _storage.Load();
        _items = new ObservableCollection<TodoItem>(
            _appData.Items.OrderBy(item => item.IsCompleted).ThenBy(item => item.Order));

        foreach (var item in _items)
        {
            item.PropertyChanged += TodoItem_PropertyChanged;
        }

        ActiveItems = CollectionViewSource.GetDefaultView(_items);
        ActiveItems.Filter = item => item is TodoItem todo && !todo.IsCompleted;
        ActiveItems.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Order), ListSortDirection.Ascending));

        CompletedItems = new ListCollectionView(_items);
        CompletedItems.Filter = item => item is TodoItem todo && todo.IsCompleted;
        CompletedItems.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Order), ListSortDirection.Ascending));

        _itemFontSize = Math.Clamp(_appData.Settings.FontSize, 12, 28);
        CompletedCount = _items.Count(item => item.IsCompleted);

        InitializeComponent();
        DataContext = this;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync();
        };

        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            UndoToast.Visibility = Visibility.Collapsed;
            _undoItem = null;
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusToast.Visibility = Visibility.Collapsed;
        };

        _trayShowItem = new Forms.ToolStripMenuItem("Show keList");
        _trayShowItem.Click += (_, _) => Dispatcher.Invoke(ShowAndActivate);

        _trayTopmostItem = new Forms.ToolStripMenuItem("Keep above other windows")
        {
            CheckOnClick = true
        };
        _trayTopmostItem.Click += (_, _) => Dispatcher.Invoke(() => SetTopmost(_trayTopmostItem.Checked));

        _trayPassThroughItem = new Forms.ToolStripMenuItem("Mouse pass-through")
        {
            CheckOnClick = true
        };
        _trayPassThroughItem.Click += (_, _) => Dispatcher.Invoke(() => SetPassThrough(_trayPassThroughItem.Checked));

        _trayStartupItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = StartupService.IsEnabled()
        };
        _trayStartupItem.Click += (_, _) => Dispatcher.Invoke(ToggleStartup);

        var openDataItem = new Forms.ToolStripMenuItem("Open data folder");
        openDataItem.Click += (_, _) => OpenDataDirectory();

        var resetLayoutItem = new Forms.ToolStripMenuItem("Reset window layout");
        resetLayoutItem.Click += (_, _) => Dispatcher.Invoke(ResetWindowLayout);

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);

        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.AddRange(
        [
            _trayShowItem,
            new Forms.ToolStripSeparator(),
            _trayTopmostItem,
            _trayPassThroughItem,
            new Forms.ToolStripSeparator(),
            _trayStartupItem,
            openDataItem,
            resetLayoutItem,
            new Forms.ToolStripSeparator(),
            exitItem
        ]);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "keList — for better",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);

        ApplyLoadedSettings();
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            NewTodoTextBox.Focus();
        };
    }

    public ICollectionView ActiveItems { get; }
    public ICollectionView CompletedItems { get; }

    public double ItemFontSize
    {
        get => _itemFontSize;
        private set
        {
            var clamped = Math.Clamp(value, 12, 28);
            if (Math.Abs(_itemFontSize - clamped) < 0.01)
            {
                return;
            }

            _itemFontSize = clamped;
            _appData.Settings.FontSize = clamped;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public int CompletedCount
    {
        get => _completedCount;
        private set
        {
            if (_completedCount == value)
            {
                return;
            }

            _completedCount = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ShowAndActivate()
    {
        if (_isPassThrough)
        {
            SetPassThrough(false);
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = _appData.Settings.IsTopmost;
        NewTodoTextBox.Focus();
        _trayShowItem.Text = "Hide keList";
    }

    private void ApplyLoadedSettings()
    {
        var settings = _appData.Settings;
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        Opacity = Math.Clamp(settings.BackgroundOpacity, 0.4, 0.95);
        OpacitySlider.Value = Opacity;
        SetTopmost(settings.IsTopmost);
        SetLocked(settings.IsLocked);

        if (IsPositionVisible(settings.Left, settings.Top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.Left;
            Top = settings.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private static bool IsPositionVisible(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return false;
        }

        var windowRect = new Rect(left, top, width, height);
        return Forms.Screen.AllScreens.Any(screen =>
        {
            var area = screen.WorkingArea;
            var screenRect = new Rect(area.Left, area.Top, area.Width, area.Height);
            return Rect.Intersect(windowRect, screenRect).Width >= 80
                && Rect.Intersect(windowRect, screenRect).Height >= 80;
        });
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);

        NativeMethods.EnableAcrylic(_windowHandle);
        NativeMethods.RegisterHotKey(
            _windowHandle,
            TogglePassThroughHotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt,
            (uint)KeyInterop.VirtualKeyFromKey(Key.P));
    }

    private nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == TogglePassThroughHotkeyId)
        {
            SetPassThrough(!_isPassThrough);
            handled = true;
        }

        return nint.Zero;
    }

    private void SetTopmost(bool enabled)
    {
        Topmost = enabled;
        _appData.Settings.IsTopmost = enabled;
        _trayTopmostItem.Checked = enabled;

        if (PinButton is not null)
        {
            PinButton.Opacity = enabled ? 1 : 0.48;
            PinButton.Background = enabled
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 255, 255, 255))
                : System.Windows.Media.Brushes.Transparent;
        }

        ScheduleSave();
    }

    private void SetLocked(bool enabled)
    {
        _appData.Settings.IsLocked = enabled;
        LockCheckBox.IsChecked = enabled;
        ResizeMode = enabled ? ResizeMode.NoResize : ResizeMode.CanResize;
        ScheduleSave();
    }

    private void SetPassThrough(bool enabled)
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();

        if (enabled)
        {
            extendedStyle |= NativeMethods.WsExTransparent | NativeMethods.WsExLayered;
        }
        else
        {
            extendedStyle &= ~NativeMethods.WsExTransparent;
        }

        NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GwlExStyle,
            new nint(extendedStyle));

        _isPassThrough = enabled;
        _trayPassThroughItem.Checked = enabled;
        PassThroughButton.Opacity = enabled ? 1 : 0.58;
        PassThroughButton.Background = enabled
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 255, 255, 255))
            : System.Windows.Media.Brushes.Transparent;

        if (enabled)
        {
            ShowStatus("Mouse pass-through is on\nPress Ctrl + Alt + P or use the tray menu to turn it off", 4);
        }
        else
        {
            ShowStatus("Mouse pass-through is off", 2);
            Activate();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !_appData.Settings.IsLocked)
        {
            DragMove();
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
        => SetTopmost(!Topmost);

    private void PassThroughButton_Click(object sender, RoutedEventArgs e)
        => SetPassThrough(true);

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        LockCheckBox.IsChecked = _appData.Settings.IsLocked;
        OpacitySlider.Value = Opacity;
        MorePopup.IsOpen = true;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
        => HideToTray();

    private void LockCheckBox_Click(object sender, RoutedEventArgs e)
        => SetLocked(LockCheckBox.IsChecked == true);

    private void IncreaseFontButton_Click(object sender, RoutedEventArgs e)
        => ItemFontSize += 1;

    private void DecreaseFontButton_Click(object sender, RoutedEventArgs e)
        => ItemFontSize -= 1;

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded && Math.Abs(e.NewValue - _appData.Settings.BackgroundOpacity) > 0.1)
        {
            return;
        }

        Opacity = Math.Clamp(e.NewValue, 0.4, 0.95);
        _appData.Settings.BackgroundOpacity = Opacity;
        ScheduleSave();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        System.Windows.MessageBox.Show(
            this,
            "keList 0.1.0\nfor better\n\nA focused desktop todo list for Windows.\nLocal-first, private, and open source.",
            "About keList",
            MessageBoxButton.OK,
            MessageBoxImage.None);
    }

    private void NewTodoTextBox_TextChanged(object sender, TextChangedEventArgs e)
        => InputPlaceholder.Visibility = string.IsNullOrEmpty(NewTodoTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void NewTodoTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var text = NewTodoTextBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var item = new TodoItem
        {
            Text = text,
            Order = _items.Where(todo => !todo.IsCompleted).Select(todo => todo.Order).DefaultIfEmpty(-1).Max() + 1
        };

        item.PropertyChanged += TodoItem_PropertyChanged;
        _items.Add(item);
        NewTodoTextBox.Clear();
        RefreshViews();
        ScheduleSave();
        e.Handled = true;
    }

    private void CompletionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { DataContext: TodoItem item })
        {
            return;
        }

        if (!item.IsCompleted)
        {
            item.Order = _items.Where(todo => !todo.IsCompleted).Select(todo => todo.Order).DefaultIfEmpty(-1).Max() + 1;
        }
        else
        {
            item.Order = _items.Where(todo => todo.IsCompleted).Select(todo => todo.Order).DefaultIfEmpty(-1).Max() + 1;
        }

        RefreshViews();
        ScheduleSave();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: TodoItem item })
        {
            return;
        }

        _undoIndex = _items.IndexOf(item);
        _undoItem = item;
        item.PropertyChanged -= TodoItem_PropertyChanged;
        _items.Remove(item);
        RefreshViews();
        ScheduleSave();

        UndoToast.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undoItem is null)
        {
            return;
        }

        _undoItem.PropertyChanged += TodoItem_PropertyChanged;
        _items.Insert(Math.Clamp(_undoIndex, 0, _items.Count), _undoItem);
        _undoItem = null;
        _undoTimer.Stop();
        UndoToast.Visibility = Visibility.Collapsed;
        RefreshViews();
        ScheduleSave();
    }

    private void TodoText_LostFocus(object sender, RoutedEventArgs e)
        => ScheduleSave();

    private void TodoText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            ScheduleSave();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TodoItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoItem.IsCompleted))
        {
            RefreshViews();
        }

        ScheduleSave();
    }

    private void ActiveList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(ActiveList);

    private void ActiveList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(ActiveList);
        if (Math.Abs(currentPosition.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var container = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (container?.DataContext is TodoItem item)
        {
            System.Windows.DragDrop.DoDragDrop(container, item, System.Windows.DragDropEffects.Move);
        }
    }

    private void ActiveList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TodoItem)))
        {
            return;
        }

        var draggedItem = (TodoItem)e.Data.GetData(typeof(TodoItem))!;
        var targetContainer = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (targetContainer?.DataContext is not TodoItem targetItem || draggedItem == targetItem)
        {
            return;
        }

        var oldIndex = _items.IndexOf(draggedItem);
        var targetIndex = _items.IndexOf(targetItem);
        _items.Move(oldIndex, targetIndex);
        NormalizeOrders();
        RefreshViews();
        ScheduleSave();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void NormalizeOrders()
    {
        var activeOrder = 0;
        var completedOrder = 0;

        foreach (var item in _items)
        {
            item.Order = item.IsCompleted ? completedOrder++ : activeOrder++;
        }
    }

    private void RefreshViews()
    {
        ActiveItems.Refresh();
        CompletedItems.Refresh();
        CompletedCount = _items.Count(item => item.IsCompleted);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (!_isLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        _appData.Settings.Left = Left;
        _appData.Settings.Top = Top;
        _appData.Settings.Width = ActualWidth;
        _appData.Settings.Height = ActualHeight;
        ScheduleSave();
    }

    private void HideToTray()
    {
        Hide();
        _trayShowItem.Text = "Show keList";

        if (!_appData.Settings.HasShownTrayHint)
        {
            _trayIcon.ShowBalloonTip(
                2500,
                "keList is still running",
                "Double-click the tray icon to bring it back.",
                Forms.ToolTipIcon.Info);
            _appData.Settings.HasShownTrayHint = true;
            ScheduleSave();
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupService.SetEnabled(_trayStartupItem.Checked);
            _appData.Settings.StartWithWindows = _trayStartupItem.Checked;
            ScheduleSave();
        }
        catch (Exception exception)
        {
            _trayStartupItem.Checked = StartupService.IsEnabled();
            ShowStatus($"Unable to update startup setting: {exception.Message}", 4);
        }
    }

    private void OpenDataDirectory()
    {
        Directory.CreateDirectory(_storage.DataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _storage.DataDirectory,
            UseShellExecute = true
        });
    }

    private void ResetWindowLayout()
    {
        SetLocked(false);
        Width = 390;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 32;
        Top = area.Top + 32;
        ShowAndActivate();
        ScheduleSave();
    }

    private void ShowStatus(string message, double seconds)
    {
        StatusToastText.Text = message;
        StatusToast.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(seconds);
        _statusTimer.Start();
    }

    private void ScheduleSave()
    {
        if (!_isLoaded)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveNowAsync()
    {
        _appData.Items = _items.ToList();
        await _storage.SaveAsync(_appData);
    }

    private async void ExitApplication()
    {
        _allowExit = true;
        _saveTimer.Stop();
        await SaveNowAsync();
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        NativeMethods.UnregisterHotKey(_windowHandle, TogglePassThroughHotkeyId);
        _windowSource?.RemoveHook(WindowMessageHook);
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        await SaveNowAsync();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ItemFontSize += e.Delta > 0 ? 1 : -1;
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
