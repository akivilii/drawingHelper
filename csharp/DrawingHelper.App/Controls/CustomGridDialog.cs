using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using DrawingHelper.App.ViewModels;

namespace DrawingHelper.App.Controls;

/// <summary>
/// Compact Photoshop/Affinity-style dark studio dialog for entering any custom tile count (1..128)
/// with live 32px multiple & aspect-synced W×H preview.
/// </summary>
public sealed class CustomGridDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly bool _isWidth;
    private readonly TextBox _inputBox;
    private readonly TextBlock _previewText;
    private readonly Button _okButton;

    public int EnteredTiles { get; private set; }

    public CustomGridDialog(Window owner, MainViewModel vm, bool isWidth)
    {
        _vm = vm;
        _isWidth = isWidth;
        EnteredTiles = isWidth ? vm.SelectedWidthTiles.Tiles : vm.SelectedHeightTiles.Tiles;

        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        Title = isWidth ? "가로 칸 수 직접 입력" : "세로 칸 수 직접 입력";

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 30,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        var chromeBg = (Brush)Application.Current.FindResource("StudioChromeBrush");
        var panelBg = (Brush)Application.Current.FindResource("StudioPanelBrush");
        var borderBrush = (Brush)Application.Current.FindResource("StudioBorderBrush");
        var dividerBrush = (Brush)Application.Current.FindResource("StudioDividerBrush");
        var textBrush = (Brush)Application.Current.FindResource("StudioTextBrush");
        var mutedBrush = (Brush)Application.Current.FindResource("StudioTextMutedBrush");
        var accentHoverBrush = (Brush)Application.Current.FindResource("StudioAccentHoverBrush");
        var accentBtnStyle = (Style)Application.Current.FindResource("StudioAccentButton");

        var root = new DockPanel { Background = panelBg };

        // 1. Dark Title Bar
        var titleBar = new Grid { Height = 30, Background = chromeBg };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(titleText, 0);
        titleBar.Children.Add(titleText);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 30,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = mutedBrush,
            Padding = new Thickness(0)
        };
        WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
        closeBtn.Click += (_, _) => { DialogResult = false; };
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(closeBtn);

        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        var sep = new Border { Height = 1, Background = dividerBrush };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        // 2. Body Content
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        inputRow.Children.Add(new TextBlock
        {
            Text = isWidth ? "가로 (W):" : "세로 (H):",
            Width = 58,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textBrush
        });

        _inputBox = new TextBox
        {
            Text = EnteredTiles.ToString(CultureInfo.InvariantCulture),
            Width = 86,
            Height = 26,
            FontFamily = new FontFamily("Consolas, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            MaxLength = 3,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _inputBox.PreviewTextInput += (_, e) =>
        {
            foreach (char ch in e.Text)
            {
                if (!char.IsDigit(ch)) { e.Handled = true; return; }
            }
        };
        DataObject.AddPastingHandler(_inputBox, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(typeof(string)) ||
                !int.TryParse((string)e.DataObject.GetData(typeof(string))!, out int _))
            {
                e.CancelCommand();
            }
        });
        _inputBox.PreviewKeyDown += InputBox_PreviewKeyDown;
        _inputBox.PreviewMouseWheel += InputBox_PreviewMouseWheel;
        _inputBox.TextChanged += (_, _) => UpdatePreview();
        inputRow.Children.Add(_inputBox);

        inputRow.Children.Add(new TextBlock
        {
            Text = $"칸  (1 ~ {MainViewModel.MaxTiles})",
            Foreground = mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        });
        body.Children.Add(inputRow);

        // Quick large-grid preset buttons (20, 24, 32, 48, 64)
        var presetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(58, 8, 0, 0)
        };
        foreach (int preset in new[] { 20, 24, 32, 48, 64 })
        {
            int p = preset;
            var btn = new Button
            {
                Content = p.ToString(CultureInfo.InvariantCulture),
                Padding = new Thickness(7, 1, 7, 1),
                MinHeight = 20,
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = $"{p}칸 ({p * MainViewModel.TileSize}px)"
            };
            btn.Click += (_, _) =>
            {
                _inputBox.Text = p.ToString(CultureInfo.InvariantCulture);
                _inputBox.Focus();
                _inputBox.SelectAll();
            };
            presetRow.Children.Add(btn);
        }
        body.Children.Add(presetRow);

        // Live preview badge
        _previewText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Malgun Gothic"),
            FontSize = 11.5,
            Foreground = accentHoverBrush,
            Margin = new Thickness(0, 10, 0, 12)
        };
        body.Children.Add(_previewText);

        // Action buttons (Right aligned)
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _okButton = new Button
        {
            Content = "확인",
            Style = accentBtnStyle,
            MinWidth = 68,
            IsDefault = true,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _okButton.Click += OnConfirmClick;

        var cancelBtn = new Button
        {
            Content = "취소",
            MinWidth = 62,
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; };

        btnRow.Children.Add(_okButton);
        btnRow.Children.Add(cancelBtn);
        body.Children.Add(btnRow);

        root.Children.Add(body);

        Content = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Child = root
        };

        Loaded += (_, _) =>
        {
            UpdatePreview();
            _inputBox.Focus();
            _inputBox.SelectAll();
        };
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            int cur = TryReadTiles(out int v) ? v : EnteredTiles;
            int next = Math.Clamp(cur + (e.Key == Key.Up ? 1 : -1), 1, MainViewModel.MaxTiles);
            _inputBox.Text = next.ToString(CultureInfo.InvariantCulture);
            _inputBox.SelectAll();
            e.Handled = true;
        }
    }

    private void InputBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int cur = TryReadTiles(out int v) ? v : EnteredTiles;
        int next = Math.Clamp(cur + (e.Delta > 0 ? 1 : -1), 1, MainViewModel.MaxTiles);
        _inputBox.Text = next.ToString(CultureInfo.InvariantCulture);
        _inputBox.SelectAll();
        e.Handled = true;
    }

    private bool TryReadTiles(out int tiles)
    {
        if (int.TryParse(_inputBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) &&
            v >= 1 && v <= MainViewModel.MaxTiles)
        {
            tiles = v;
            return true;
        }
        tiles = 0;
        return false;
    }

    private void UpdatePreview()
    {
        if (!TryReadTiles(out int val))
        {
            _previewText.Text = $"1 ~ {MainViewModel.MaxTiles} 사이의 칸 수를 입력하세요.";
            if (_okButton is not null) _okButton.IsEnabled = false;
            return;
        }

        if (_okButton is not null) _okButton.IsEnabled = true;
        var (w, h) = _vm.PreviewSyncedTiles(_isWidth, val);
        string modeHint = _vm.IsImageFirstMode && _vm.HasSource ? " · 사진비율 연동" : "";
        _previewText.Text = $"→ {w} × {h}칸  ({w * MainViewModel.TileSize} × {h * MainViewModel.TileSize} px{modeHint})";
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadTiles(out int val)) return;
        EnteredTiles = val;
        DialogResult = true;
    }
}
