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
    private readonly DispatcherTimer _passThroughHitTestTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayShowItem;
    private readonly Forms.ToolStripMenuItem _trayTopmostItem;
    private readonly Forms.ToolStripMenuItem _trayPassThroughItem;
    private readonly Forms.ToolStripMenuItem _trayStartupItem;

    private HwndSource? _windowSource;
    private nint _windowHandle;
    private bool _isPassThrough;
    private bool _nativePassThroughEnabled;
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

        _passThroughHitTestTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _passThroughHitTestTimer.Tick += (_, _) => UpdatePassThroughHitTest();

        _trayShowItem = new Forms.ToolStripMenuItem("隐藏 keList");
        _trayShowItem.Click += (_, _) => Dispatcher.Invoke(ToggleWindowVisibility);

        _trayTopmostItem = new Forms.ToolStripMenuItem("保持窗口置顶")
        {
            CheckOnClick = true
        };
        _trayTopmostItem.Click += (_, _) => Dispatcher.Invoke(() => SetTopmost(_trayTopmostItem.Checked));

        _trayPassThroughItem = new Forms.ToolStripMenuItem("鼠标穿透")
        {
            CheckOnClick = true,
            ShortcutKeyDisplayString = "Ctrl + Alt + P"
        };
        _trayPassThroughItem.Click += (_, _) => Dispatcher.Invoke(() => SetPassThrough(_trayPassThroughItem.Checked));

        var startupEnabled = false;
        try
        {
            startupEnabled = StartupService.Synchronize(_appData.Settings.StartWithWindows);
            _appData.Settings.StartWithWindows = startupEnabled;
        }
        catch (Exception exception)
        {
            CrashLogger.Write($"Unable to synchronize startup registration: {exception}");
        }

        _trayStartupItem = new Forms.ToolStripMenuItem("开机启动")
        {
            CheckOnClick = true,
            Checked = startupEnabled
        };
        _trayStartupItem.Click += (_, _) => Dispatcher.Invoke(ToggleStartup);

        var openDataItem = new Forms.ToolStripMenuItem("打开数据文件夹");
        openDataItem.Click += (_, _) => OpenDataDirectory();

        var resetLayoutItem = new Forms.ToolStripMenuItem("重置窗口位置");
        resetLayoutItem.Click += (_, _) => Dispatcher.Invoke(ResetWindowLayout);

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);

        var trayMenu = new Forms.ContextMenuStrip
        {
            BackColor = System.Drawing.Color.FromArgb(250, 250, 250),
            ForeColor = System.Drawing.Color.FromArgb(28, 28, 28),
            Font = new System.Drawing.Font("Microsoft YaHei UI", 10F),
            Padding = new Forms.Padding(5, 7, 5, 7),
            MinimumSize = new System.Drawing.Size(240, 0),
            ShowCheckMargin = true,
            ShowImageMargin = false,
            Renderer = new TrayMenuRenderer()
        };
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

        foreach (Forms.ToolStripItem item in trayMenu.Items)
        {
            if (item is Forms.ToolStripMenuItem)
            {
                item.Padding = new Forms.Padding(10, 7, 12, 7);
                item.Margin = new Forms.Padding(1, 1, 1, 1);
            }
            else if (item is Forms.ToolStripSeparator)
            {
                item.Margin = new Forms.Padding(0, 3, 0, 3);
            }
        }

        _trayShowItem.Font = new System.Drawing.Font(
            trayMenu.Font,
            System.Drawing.FontStyle.Bold);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "keList · 桌面待办",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);

        ApplyLoadedSettings();
        UpdateEmptyState();
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
        _trayShowItem.Text = "隐藏 keList";
    }

    private void ApplyLoadedSettings()
    {
        var settings = _appData.Settings;
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        var backgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.4, 0.95);
        Opacity = 1;
        ApplyBackgroundOpacity(backgroundOpacity);
        OpacitySlider.Value = backgroundOpacity;
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
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 0, 0, 0))
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

        _isPassThrough = enabled;

        if (enabled)
        {
            _passThroughHitTestTimer.Start();
            UpdatePassThroughHitTest();
        }
        else
        {
            _passThroughHitTestTimer.Stop();
            ApplyNativePassThrough(false);
        }

        _trayPassThroughItem.Checked = enabled;
        PassThroughButton.ToolTip = enabled
            ? "关闭鼠标穿透（Ctrl + Alt + P）"
            : "开启鼠标穿透（Ctrl + Alt + P）";
        PassThroughButton.Opacity = enabled ? 1 : 0.58;
        PassThroughButton.Background = enabled
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 0, 0, 0))
            : System.Windows.Media.Brushes.Transparent;

        if (enabled)
        {
            ShowStatus("鼠标穿透已开启\n顶部按钮仍可直接点击", 3);
        }
        else
        {
            ShowStatus("鼠标穿透已关闭", 2);
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
        => SetPassThrough(!_isPassThrough);

    private void UpdatePassThroughHitTest()
    {
        if (!_isPassThrough || !IsVisible || TitleActionsPanel.ActualWidth <= 0)
        {
            return;
        }

        var cursor = Forms.Cursor.Position;
        var cursorInWindow = PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        var panelOrigin = TitleActionsPanel.TranslatePoint(new System.Windows.Point(0, 0), this);
        var interactiveBounds = new Rect(
            panelOrigin.X - 6,
            panelOrigin.Y,
            TitleActionsPanel.ActualWidth + 12,
            TitleActionsPanel.ActualHeight);

        ApplyNativePassThrough(!interactiveBounds.Contains(cursorInWindow));
    }

    private void ApplyNativePassThrough(bool enabled)
    {
        if (_windowHandle == nint.Zero || _nativePassThroughEnabled == enabled)
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
        _nativePassThroughEnabled = enabled;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        LockCheckBox.IsChecked = _appData.Settings.IsLocked;
        OpacitySlider.Value = _appData.Settings.BackgroundOpacity;
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

        var backgroundOpacity = Math.Clamp(e.NewValue, 0.4, 0.95);
        ApplyBackgroundOpacity(backgroundOpacity);
        _appData.Settings.BackgroundOpacity = backgroundOpacity;
        ScheduleSave();
    }

    private void ApplyBackgroundOpacity(double opacity)
    {
        if (RootPanel is null)
        {
            return;
        }

        var veilOpacity = Math.Clamp(opacity * 0.29, 0.09, 0.32);
        var alpha = (byte)Math.Round(veilOpacity * byte.MaxValue);
        RootPanel.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, 216, 216, 220));
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        System.Windows.MessageBox.Show(
            this,
            "keList 0.2.1\n\n一款专注、高效的 Windows 桌面待办工具。\n数据仅保存在本地，保护隐私，并开放源代码。",
            "关于 keList",
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
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyStateText.Visibility = _items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompletedExpander.Visibility = CompletedCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        _trayShowItem.Text = "显示 keList";

        if (!_appData.Settings.HasShownTrayHint)
        {
            _trayIcon.ShowBalloonTip(
                2500,
                "keList 仍在运行",
                "双击托盘图标即可重新打开。",
                Forms.ToolTipIcon.Info);
            _appData.Settings.HasShownTrayHint = true;
            ScheduleSave();
        }
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible)
        {
            HideToTray();
        }
        else
        {
            ShowAndActivate();
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
            ShowStatus($"无法更新开机启动设置：{exception.Message}", 4);
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
        _passThroughHitTestTimer.Stop();
        _windowSource?.RemoveHook(WindowMessageHook);
        var trayIconImage = _trayIcon.Icon;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        trayIconImage?.Dispose();
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
