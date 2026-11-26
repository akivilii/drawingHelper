using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DrawingHelper.App.Interop;
using DrawingHelper.App.ViewModels;
using Microsoft.Win32;

namespace DrawingHelper.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly GlobalHotkeyService _hotkeys = new();
    private bool _isCompactSplit;
    private Rect _savedWideBounds;
    private bool _isTileMode;
    private bool _isFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.CanvasStateChanged += SyncCanvases;

        // Center MainCanvas events
        MainCanvas.TileClicked += (c, r) => EnterTileMode((c, r));
        MainCanvas.PixelHovered += (lx, ly, gx, gy) => _vm.OnCanvasPixelHovered(lx, ly, gx, gy);
        MainCanvas.PixelClicked += (lx, ly, gx, gy) => _vm.OnCanvasPixelClicked(lx, ly, gx, gy);
        MainCanvas.ClearHighlightRequested += () => _vm.ClearHighlight();
        MainCanvas.ZoomChanged += UpdateZoomButtonText;

        // Right Navigator minimap: clicking any 32x32 tile jumps straight to it
        NavigatorCanvas.TileClicked += (c, r) => EnterTileMode((c, r));

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hotkeys.Attach(this);
        _hotkeys.TileDirectionRequested += (dx, dy) => Dispatcher.Invoke(() => _vm.MoveTileDirection(dx, dy));
        _hotkeys.ColorStepRequested += delta => Dispatcher.Invoke(() => _vm.StepChecklistColor(delta));
        SyncCanvases();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _hotkeys.Dispose();
    }

    private void SyncCanvases()
    {
        EmptyOriginalPreview.Visibility = _vm.HasResult ? Visibility.Collapsed : Visibility.Visible;

        // Main canvas switches cleanly between Full Overview and 32x32 Sheet
        MainCanvas.IsOverviewMode = !_isTileMode;
        MainCanvas.UpdateState(
            _vm.Result,
            _vm.SelectedTile,
            _isTileMode ? _vm.HighlightedPaletteIdx : null,
            _isTileMode ? _vm.HighlightedItemName : null,
            _vm.HighlightOutlineColor,
            _vm.ShowPaintGrid,
            _isTileMode && _vm.ShowCrosshair,
            _isTileMode && _vm.DimCompleted,
            _vm.CompletedTiles,
            _vm.CurrentTileCompletedColors);

        // Navigator always displays the full overview with 32x32 grid & active tile box
        NavigatorCanvas.UpdateState(
            _vm.Result,
            _vm.SelectedTile,
            null,
            null,
            _vm.HighlightOutlineColor,
            true,
            false,
            false,
            _vm.CompletedTiles,
            _vm.CurrentTileCompletedColors);
    }

    private void EnterTileMode((int Col, int Row) tile)
    {
        if (!_vm.HasResult) return;
        _isTileMode = true;
        RbViewTile.IsChecked = true;
        _vm.SelectTile(tile);
    }

    private void OnCanvasViewModeChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _isTileMode = RbViewTile.IsChecked == true;
        SyncCanvases();
    }

    private void OnModeOverviewClick(object sender, RoutedEventArgs e) => RbViewOverview.IsChecked = true;
    private void OnModeTileClick(object sender, RoutedEventArgs e)
    {
        if (_vm.HasResult) RbViewTile.IsChecked = true;
    }

    // ------------------------------------------------------------------
    // File & Convert Handlers
    // ------------------------------------------------------------------
    private void OnOpenImageClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "이미지 열기",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|모든 파일|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _isTileMode = false;
            RbViewOverview.IsChecked = true;
            _vm.LoadImage(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnConvertClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.ConvertAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveFullPngClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is null) return;
        string stem = _vm.SourceFilePath is not null
            ? Path.GetFileNameWithoutExtension(_vm.SourceFilePath)
            : "converted";
        var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            DefaultExt = ".png",
            FileName = $"{stem}_{_vm.Result.Width}x{_vm.Result.Height}_{_vm.Result.Metric}.png"
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SaveFullPng(dlg.FileName);
    }

    private void OnSaveTilePngClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is null) return;
        string stem = _vm.SourceFilePath is not null
            ? Path.GetFileNameWithoutExtension(_vm.SourceFilePath)
            : "converted";
        var (tc, tr) = _vm.SelectedTile;
        var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            DefaultExt = ".png",
            FileName = $"{stem}_tile_{tc + 1}_{tr + 1}.png"
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SaveCurrentTilePng(dlg.FileName);
    }

    private void OnSaveProgressClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is null) return;
        string stem = _vm.SourceFilePath is not null
            ? Path.GetFileNameWithoutExtension(_vm.SourceFilePath)
            : "drawing";
        var dlg = new SaveFileDialog
        {
            Filter = "JSON|*.json",
            DefaultExt = ".json",
            FileName = $"{stem}_progress.json"
        };
        if (dlg.ShowDialog(this) == true)
            _vm.SaveProgress(dlg.FileName);
    }

    private void OnLoadProgressClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is null) return;
        var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _vm.LoadProgress(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            SetFullscreen(false);
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnToggleFullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(!_isFullscreen);

    private void SetFullscreen(bool enable)
    {
        if (_isFullscreen == enable) return;
        _isFullscreen = enable;
        if (enable)
        {
            _preFullscreenState = WindowState;
            TitleBarGrid.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal; // ensure clean transition to true fullscreen
            WindowState = WindowState.Maximized;
            RootFrame.Margin = new Thickness(0);
            RootFrame.BorderThickness = new Thickness(0);
            _vm.StatusText = "전체화면 (F11 해제)";
        }
        else
        {
            TitleBarGrid.Visibility = Visibility.Visible;
            WindowState = _preFullscreenState;
            UpdateMaximizedMargin();
            _vm.StatusText = "";
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMaximizedMargin();

    private void UpdateMaximizedMargin()
    {
        if (_isFullscreen)
        {
            RootFrame.Margin = new Thickness(0);
            RootFrame.BorderThickness = new Thickness(0);
        }
        else if (WindowState == WindowState.Maximized)
        {
            // Prevent WindowChrome border from clipping outside monitor work area when maximized
            RootFrame.Margin = new Thickness(7);
            RootFrame.BorderThickness = new Thickness(0);
        }
        else
        {
            RootFrame.Margin = new Thickness(0);
            RootFrame.BorderThickness = new Thickness(1);
        }
    }

    // ------------------------------------------------------------------
    // Window / Dock / Zoom Controls
    // ------------------------------------------------------------------
    private void OnToggleSplitSizeClick(object sender, RoutedEventArgs e)
    {
        if (!_isCompactSplit)
        {
            _savedWideBounds = new Rect(Left, Top, Width, Height);
            Rect area = SystemParameters.WorkArea;
            Width = Math.Min(560, area.Width * 0.45);
            Height = area.Height - 16;
            Left = area.Right - Width - 6;
            Top = area.Top + 8;
            _isCompactSplit = true;
            BtnSplitSize.IsChecked = true;
            if (_vm.HasResult) RbViewTile.IsChecked = true;
        }
        else
        {
            Left = _savedWideBounds.Left;
            Top = _savedWideBounds.Top;
            Width = _savedWideBounds.Width;
            Height = _savedWideBounds.Height;
            _isCompactSplit = false;
            BtnSplitSize.IsChecked = false;
        }
    }

    private void OnToggleRightDockClick(object sender, RoutedEventArgs e)
    {
        bool show = sender == MenuRightDock
            ? MenuRightDock.IsChecked
            : (sender == BtnTitleRightDock ? BtnTitleRightDock.IsChecked == true : BtnRightDock.IsChecked == true);

        MenuRightDock.IsChecked = show;
        BtnRightDock.IsChecked = show;
        BtnTitleRightDock.IsChecked = show;
        RightDockPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RightSplitterCol.Width = show ? new GridLength(3) : new GridLength(0);
        RightDockCol.Width = show ? new GridLength(260) : new GridLength(0);
        RightDockCol.MinWidth = show ? 200 : 0;
    }

    private void OnPeekOriginalChanged(object sender, RoutedEventArgs e)
    {
        bool peek = BtnPeekOriginal.IsChecked == true;
        NavigatorOriginalImg.Visibility = peek ? Visibility.Visible : Visibility.Collapsed;
        NavigatorCanvas.Visibility = peek ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnToggleGlobalHotkeysClick(object sender, RoutedEventArgs e)
    {
        bool ok = _hotkeys.SetEnabled(_vm.IsGlobalHotkeysEnabled);
        _vm.StatusText = _vm.IsGlobalHotkeysEnabled
            ? (ok ? "전역키 ON" : "전역키 충돌")
            : "전역키 OFF";
    }

    private void UpdateZoomButtonText(double zoom) =>
        BtnZoomReset.Content = $"{(int)Math.Round(zoom * 100)}%";

    private void OnZoomInClick(object sender, RoutedEventArgs e) => MainCanvas.ZoomBy(1.25);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => MainCanvas.ZoomBy(1.0 / 1.25);
    private void OnZoomResetClick(object sender, RoutedEventArgs e) => MainCanvas.ResetZoom();

    // ------------------------------------------------------------------
    // Tile, Palette & Shortcuts
    // ------------------------------------------------------------------
    private void OnSelectAllItemsClick(object sender, RoutedEventArgs e) => _vm.SelectAllFilterItems(true);
    private void OnDeselectAllItemsClick(object sender, RoutedEventArgs e) => _vm.SelectAllFilterItems(false);
    private void OnPrevTileClick(object sender, RoutedEventArgs e) { _vm.StepTile(-1); }
    private void OnNextTileClick(object sender, RoutedEventArgs e) { _vm.StepTile(1); }
    private void OnClearHighlightClick(object sender, RoutedEventArgs e) => _vm.ClearHighlight();
    private void OnToggleColorDoneClick(object sender, RoutedEventArgs e) => _vm.ToggleSelectedColorDone();
    private void OnNextColorClick(object sender, RoutedEventArgs e) => _vm.StepChecklistColor(1);

    private void OnScopeRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.HighlightWholeItem = RbScopeItem.IsChecked == true;
    }

    private void ColorListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.ToggleSelectedColorDone();

    private void ColorListView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _vm.ToggleSelectedColorDone();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullscreen(!_isFullscreen);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key is Key.OemPlus or Key.Add) { MainCanvas.ZoomBy(1.25); e.Handled = true; return; }
            if (e.Key is Key.OemMinus or Key.Subtract) { MainCanvas.ZoomBy(1.0 / 1.25); e.Handled = true; return; }
            if (e.Key is Key.D0 or Key.NumPad0) { MainCanvas.ResetZoom(); e.Handled = true; return; }
        }

        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Escape)
        {
            _vm.ClearHighlight();
            e.Handled = true;
            return;
        }

        if (!_vm.HasResult) return;

        if (e.Key == Key.Q) { _vm.StepChecklistColor(-1); e.Handled = true; }
        else if (e.Key == Key.E) { _vm.StepChecklistColor(1); e.Handled = true; }
        else if (_isTileMode && Keyboard.FocusedElement is not ListViewItem)
        {
            switch (e.Key)
            {
                case Key.Left: _vm.MoveTileDirection(-1, 0); e.Handled = true; break;
                case Key.Right: _vm.MoveTileDirection(1, 0); e.Handled = true; break;
                case Key.Up: _vm.MoveTileDirection(0, -1); e.Handled = true; break;
                case Key.Down: _vm.MoveTileDirection(0, 1); e.Handled = true; break;
            }
        }
    }
}
