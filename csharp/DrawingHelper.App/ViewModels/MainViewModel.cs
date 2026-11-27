using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingHelper.App.Controls;
using DrawingHelper.App.Interop;
using DrawingHelper.Core.ColorScience;
using DrawingHelper.Core.Matching;
using DrawingHelper.Core.Palette;
using DrawingHelper.Core.Progress;

namespace DrawingHelper.App.ViewModels;

public sealed class TileColorRow : ObservableObject
{
    private bool _isDone;
    public int PaletteIndex { get; init; }
    public required PaletteColor Color { get; init; }
    public required SolidColorBrush SwatchBrush { get; init; }
    public int PixelCount { get; set; }

    public string KoreanName => Color.KoreanName;
    public string ShadeDisplay => $"{Color.Shade}";
    public string HexCode => Color.HexCode;
    public string DoneMark => IsDone ? "☑" : "☐";

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (SetProperty(ref _isDone, value))
                RaisePropertyChanged(nameof(DoneMark));
        }
    }
}

public sealed class SelectablePaletteItem : ObservableObject
{
    private bool _isIncluded = true;
    public required string ItemName { get; init; }
    public required string KoreanName { get; init; }
    public required SolidColorBrush SwatchBrush { get; init; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}

public sealed record TileChoice(int Col, int Row, string Label)
{
    public override string ToString() => Label;
}

public sealed record GridCountOption(int Tiles, int Pixels, string Label, bool IsCustomPrompt = false)
{
    public static readonly GridCountOption CustomPrompt = new(0, 0, "직접 입력...", IsCustomPrompt: true);
    public override string ToString() => Label;
}

public sealed record GridFitOption(ResizeMode Mode, string Label)
{
    public override string ToString() => Label;
}

public sealed class MainViewModel : ObservableObject
{
    public const int TileSize = 32;
    public const int DefaultPresetMaxTiles = 16;
    public const int MaxTiles = 128;

    private readonly ItemPalette _fullPalette;
    private Rgb24[]? _sourcePixels;
    private int _sourceW, _sourceH;
    private string? _sourceFilePath;

    private BitmapSource? _sourcePreview;
    private ConversionResult? _result;
    private bool _isBusy;

    // Dual sizing priority: true = 사진 기준 (image drives grid W:H), false = 칸 고정 (grid W:H drives image)
    private bool _isImageFirstMode = true;
    private bool _syncingTiles;
    private GridCountOption _selectedWidthTiles;
    private GridCountOption _selectedHeightTiles;
    private GridFitOption _selectedGridFit;
    private CropRect? _customCrop;
    private ColorMetric _selectedMetric = ColorMetric.Ciede2000;
    private DitherMode _selectedDither = DitherMode.None;
    private bool _showOverviewGrid = true;

    // Paint & tool state
    private (int Col, int Row) _selectedTile = (0, 0);
    private TileChoice? _selectedTileChoice;
    private TileColorRow? _selectedColorRow;
    private int _activeBrushPaletteIdx;
    private int? _highlightedPaletteIdx;
    private string? _highlightedItemName;
    private bool _highlightWholeItem;
    private HighlightOutlineColor _highlightOutlineColor = HighlightOutlineColor.Yellow;
    private bool _showPaintGrid = true;
    private bool _showCrosshair = true;
    private bool _dimCompleted = true;
    private bool _isCurrentTileDone;

    // Undo stack for manual Pencil pixel edits: (gx, gy, prevPaletteIdx)
    private readonly Stack<(int X, int Y, int PrevIdx)> _undoStack = new();

    // Window & status properties
    private bool _isTopmost;
    private double _windowOpacity = 1.0;
    private bool _isGlobalHotkeysEnabled;
    private string _statusText = "";
    private string _overviewTitle = "DrawingHelper";
    private string _navigatorHeader = "네비게이터";
    private string _checklistHeader = "";

    // Live hover card
    private SolidColorBrush _hoverSwatchBrush = FreezeBrush(Color.FromRgb(0x2B, 0x2B, 0x32));
    private string _hoverCoordText = "";
    private string _hoverItemText = "";
    private string _hoverDetailText = "";

    // Progress tracking
    private readonly HashSet<(int Col, int Row)> _completedTiles = [];
    private readonly Dictionary<(int Col, int Row), HashSet<int>> _completedColors = [];

    public event Action? CanvasStateChanged;
    public event Action<bool>? CustomGridInputRequested; // true = Width, false = Height

    public MainViewModel()
    {
        for (int n = 1; n <= DefaultPresetMaxTiles; n++)
            GridCountOptions.Add(new GridCountOption(n, n * TileSize, $"{n} ({n * TileSize})"));
        GridCountOptions.Add(GridCountOption.CustomPrompt);

        _selectedWidthTiles = GridCountOptions[3];  // default 4 (128px)
        _selectedHeightTiles = GridCountOptions[3]; // default 4 (128px)

        GridFitOptions =
        [
            new GridFitOption(ResizeMode.Cover, "채우기"),
            new GridFitOption(ResizeMode.Fit, "여백"),
            new GridFitOption(ResizeMode.Stretch, "늘림")
        ];
        _selectedGridFit = GridFitOptions[0];

        string mdPath = LocatePaletteMarkdown();
        _fullPalette = PaletteParser.FromMarkdownFile(mdPath, verifyConsistency: true);

        foreach (string itemName in _fullPalette.ItemNames)
        {
            var first = _fullPalette.Colors.First(c => c.ItemName == itemName);
            PaletteFilterItems.Add(new SelectablePaletteItem
            {
                ItemName = itemName,
                KoreanName = first.KoreanName,
                SwatchBrush = FreezeBrush(Color.FromRgb(first.Rgb.R, first.Rgb.G, first.Rgb.B)),
                IsIncluded = true
            });
        }

        _statusText = $"{_fullPalette.ItemNames.Count}종 · {_fullPalette.Count}색";
    }

    private static SolidColorBrush FreezeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static string LocatePaletteMarkdown()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "minecraft_palette_rgb.md"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "minecraft_palette_rgb.md")),
            Path.Combine(Environment.CurrentDirectory, "minecraft_palette_rgb.md")
        ];
        foreach (string p in candidates)
            if (File.Exists(p)) return p;
        throw new FileNotFoundException("minecraft_palette_rgb.md 파일을 찾을 수 없습니다.");
    }

    // Collections
    public ObservableCollection<GridCountOption> GridCountOptions { get; } = [];
    public GridFitOption[] GridFitOptions { get; }
    public ColorMetric[] Metrics { get; } = Enum.GetValues<ColorMetric>();
    public DitherMode[] DitherModes { get; } = Enum.GetValues<DitherMode>();
    public HighlightOutlineColor[] OutlineColors { get; } = Enum.GetValues<HighlightOutlineColor>();

    public ObservableCollection<SelectablePaletteItem> PaletteFilterItems { get; } = [];
    public ObservableCollection<TileChoice> TileChoices { get; } = [];
    public ObservableCollection<TileColorRow> TileColors { get; } = [];

    // Bindable properties
    public BitmapSource? SourcePreview { get => _sourcePreview; private set => SetProperty(ref _sourcePreview, value); }
    public int SourceWidth => _sourceW;
    public int SourceHeight => _sourceH;
    public ConversionResult? Result { get => _result; private set { if (SetProperty(ref _result, value)) RaisePropertyChanged(nameof(HasResult)); } }
    public bool HasSource => _sourcePixels is not null;
    public bool HasResult => _result is not null;
    public bool CanUndo => _undoStack.Count > 0;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public bool IsImageFirstMode
    {
        get => _isImageFirstMode;
        set
        {
            if (SetProperty(ref _isImageFirstMode, value))
            {
                RaisePropertyChanged(nameof(IsGridFirstMode));
                if (value && HasSource)
                    SyncHeightFromWidth();
            }
        }
    }

    public bool IsGridFirstMode => !_isImageFirstMode;

    public GridCountOption SelectedWidthTiles
    {
        get => _selectedWidthTiles;
        set
        {
            if (value is null) return;
            if (value.IsCustomPrompt)
            {
                CustomGridInputRequested?.Invoke(true);
                RaisePropertyChanged(nameof(SelectedWidthTiles));
                return;
            }
            if (SetProperty(ref _selectedWidthTiles, value) && _isImageFirstMode && !_syncingTiles)
                SyncHeightFromWidth();
        }
    }

    public GridCountOption SelectedHeightTiles
    {
        get => _selectedHeightTiles;
        set
        {
            if (value is null) return;
            if (value.IsCustomPrompt)
            {
                CustomGridInputRequested?.Invoke(false);
                RaisePropertyChanged(nameof(SelectedHeightTiles));
                return;
            }
            if (SetProperty(ref _selectedHeightTiles, value) && _isImageFirstMode && !_syncingTiles)
                SyncWidthFromHeight();
        }
    }

    public GridCountOption GetOrAddGridOption(int tiles)
    {
        int clamped = Math.Clamp(tiles, 1, MaxTiles);
        foreach (var opt in GridCountOptions)
        {
            if (!opt.IsCustomPrompt && opt.Tiles == clamped)
                return opt;
        }

        var created = new GridCountOption(clamped, clamped * TileSize, $"{clamped} ({clamped * TileSize})");
        int insertIdx = GridCountOptions.Count - 1; // right before "직접 입력..."
        for (int i = 0; i < GridCountOptions.Count; i++)
        {
            if (GridCountOptions[i].IsCustomPrompt || GridCountOptions[i].Tiles > clamped)
            {
                insertIdx = i;
                break;
            }
        }
        GridCountOptions.Insert(insertIdx, created);
        return created;
    }

    public void SetCustomTiles(bool isWidth, int tiles)
    {
        var opt = GetOrAddGridOption(tiles);
        if (isWidth)
            SelectedWidthTiles = opt;
        else
            SelectedHeightTiles = opt;
    }

    public (int W, int H) PreviewSyncedTiles(bool isWidthChanged, int newTiles)
    {
        int t = Math.Clamp(newTiles, 1, MaxTiles);
        if (!_isImageFirstMode)
            return isWidthChanged ? (t, _selectedHeightTiles.Tiles) : (_selectedWidthTiles.Tiles, t);

        var (aw, ah) = ActiveAspectSize();
        if (aw <= 0 || ah <= 0)
            return isWidthChanged ? (t, _selectedHeightTiles.Tiles) : (_selectedWidthTiles.Tiles, t);

        if (isWidthChanged)
        {
            double ratio = ah / aw;
            int w = t;
            int h = (int)Math.Round(w * ratio);
            if (h > MaxTiles)
            {
                h = MaxTiles;
                w = Math.Clamp((int)Math.Round(h / ratio), 1, MaxTiles);
            }
            return (w, Math.Clamp(h, 1, MaxTiles));
        }
        else
        {
            double invRatio = aw / ah;
            int h = t;
            int w = (int)Math.Round(h * invRatio);
            if (w > MaxTiles)
            {
                w = MaxTiles;
                h = Math.Clamp((int)Math.Round(w / invRatio), 1, MaxTiles);
            }
            return (Math.Clamp(w, 1, MaxTiles), h);
        }
    }

    public GridFitOption SelectedGridFit { get => _selectedGridFit; set => SetProperty(ref _selectedGridFit, value); }

    public CropRect? CustomCrop
    {
        get => _customCrop;
        set
        {
            if (SetProperty(ref _customCrop, value))
                RaisePropertyChanged(nameof(HasCustomCrop));
        }
    }

    public bool HasCustomCrop => _customCrop is not null;
    public ColorMetric SelectedMetric { get => _selectedMetric; set => SetProperty(ref _selectedMetric, value); }
    public DitherMode SelectedDither { get => _selectedDither; set => SetProperty(ref _selectedDither, value); }

    public bool ShowOverviewGrid
    {
        get => _showOverviewGrid;
        set { if (SetProperty(ref _showOverviewGrid, value)) NotifyCanvasChanged(); }
    }

    public TileChoice? SelectedTileChoice
    {
        get => _selectedTileChoice;
        set
        {
            if (SetProperty(ref _selectedTileChoice, value) && value is not null)
                SelectTile((value.Col, value.Row));
        }
    }

    public TileColorRow? SelectedColorRow
    {
        get => _selectedColorRow;
        set
        {
            if (SetProperty(ref _selectedColorRow, value) && value is not null)
            {
                _activeBrushPaletteIdx = value.PaletteIndex;
                HighlightPaletteIndex(value.PaletteIndex);
            }
        }
    }

    public bool HighlightWholeItem
    {
        get => _highlightWholeItem;
        set
        {
            if (SetProperty(ref _highlightWholeItem, value) && SelectedColorRow is not null)
                HighlightPaletteIndex(SelectedColorRow.PaletteIndex);
        }
    }

    public HighlightOutlineColor HighlightOutlineColor
    {
        get => _highlightOutlineColor;
        set { if (SetProperty(ref _highlightOutlineColor, value)) NotifyCanvasChanged(); }
    }

    public bool ShowPaintGrid
    {
        get => _showPaintGrid;
        set { if (SetProperty(ref _showPaintGrid, value)) NotifyCanvasChanged(); }
    }

    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set { if (SetProperty(ref _showCrosshair, value)) NotifyCanvasChanged(); }
    }

    public bool DimCompleted
    {
        get => _dimCompleted;
        set { if (SetProperty(ref _dimCompleted, value)) NotifyCanvasChanged(); }
    }

    public bool IsCurrentTileDone
    {
        get => _isCurrentTileDone;
        set
        {
            if (!SetProperty(ref _isCurrentTileDone, value) || _result is null) return;
            if (value) _completedTiles.Add(_selectedTile);
            else _completedTiles.Remove(_selectedTile);
            RefreshTileChoices();
            NotifyCanvasChanged();
            StatusText = $"칸 {_completedTiles.Count}/{TileChoices.Count}";
        }
    }

    public bool IsTopmost { get => _isTopmost; set => SetProperty(ref _isTopmost, value); }

    public double WindowOpacity
    {
        get => _windowOpacity;
        set
        {
            if (SetProperty(ref _windowOpacity, Math.Clamp(value, 0.35, 1.0)))
                RaisePropertyChanged(nameof(OpacityPercentText));
        }
    }

    public string OpacityPercentText => $"{(int)Math.Round(_windowOpacity * 100)}%";
    public bool IsGlobalHotkeysEnabled { get => _isGlobalHotkeysEnabled; set => SetProperty(ref _isGlobalHotkeysEnabled, value); }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string OverviewTitle { get => _overviewTitle; set => SetProperty(ref _overviewTitle, value); }
    public string NavigatorHeader { get => _navigatorHeader; set => SetProperty(ref _navigatorHeader, value); }
    public string ChecklistHeader { get => _checklistHeader; set => SetProperty(ref _checklistHeader, value); }

    public SolidColorBrush HoverSwatchBrush { get => _hoverSwatchBrush; private set => SetProperty(ref _hoverSwatchBrush, value); }
    public string HoverCoordText { get => _hoverCoordText; private set => SetProperty(ref _hoverCoordText, value); }
    public string HoverItemText { get => _hoverItemText; private set => SetProperty(ref _hoverItemText, value); }
    public string HoverDetailText { get => _hoverDetailText; private set => SetProperty(ref _hoverDetailText, value); }

    public (int Col, int Row) SelectedTile => _selectedTile;
    public int? HighlightedPaletteIdx => _highlightedPaletteIdx;
    public string? HighlightedItemName => _highlightedItemName;
    public IReadOnlySet<(int Col, int Row)> CompletedTiles => _completedTiles;
    public IReadOnlySet<int> CurrentTileCompletedColors =>
        _completedColors.TryGetValue(_selectedTile, out var s) ? s : new HashSet<int>();
    public string? SourceFilePath => _sourceFilePath;

    // ------------------------------------------------------------------
    // Aspect Synchronization (ImageFirst Mode)
    // ------------------------------------------------------------------
    private (double W, double H) ActiveAspectSize()
    {
        if (_customCrop is { Width: > 0, Height: > 0 } c)
            return (c.Width, c.Height);
        return (_sourceW, _sourceH);
    }

    private void SyncHeightFromWidth()
    {
        var (aw, ah) = ActiveAspectSize();
        if (aw <= 0 || ah <= 0) return;

        _syncingTiles = true;
        try
        {
            var (w, h) = PreviewSyncedTiles(isWidthChanged: true, _selectedWidthTiles.Tiles);
            if (w != _selectedWidthTiles.Tiles)
                SelectedWidthTiles = GetOrAddGridOption(w);
            SelectedHeightTiles = GetOrAddGridOption(h);
        }
        finally
        {
            _syncingTiles = false;
        }
    }

    private void SyncWidthFromHeight()
    {
        var (aw, ah) = ActiveAspectSize();
        if (aw <= 0 || ah <= 0) return;

        _syncingTiles = true;
        try
        {
            var (w, h) = PreviewSyncedTiles(isWidthChanged: false, _selectedHeightTiles.Tiles);
            if (h != _selectedHeightTiles.Tiles)
                SelectedHeightTiles = GetOrAddGridOption(h);
            SelectedWidthTiles = GetOrAddGridOption(w);
        }
        finally
        {
            _syncingTiles = false;
        }
    }

    // ------------------------------------------------------------------
    // Image Load, Rotate/Flip, Crop & Convert
    // ------------------------------------------------------------------
    public void LoadImage(string path)
    {
        var (pixels, w, h, preview) = WpfImageBridge.LoadImageFile(path);
        _sourceFilePath = path;
        _sourcePixels = pixels;
        _sourceW = w;
        _sourceH = h;
        SourcePreview = preview;
        RaisePropertyChanged(nameof(HasSource));

        Result = null;
        CustomCrop = null;
        _undoStack.Clear();
        RaisePropertyChanged(nameof(CanUndo));
        _completedTiles.Clear();
        _completedColors.Clear();
        TileChoices.Clear();
        TileColors.Clear();
        ClearHighlight();

        if (_isImageFirstMode)
            SyncHeightFromWidth();

        string fileName = Path.GetFileName(path);
        OverviewTitle = $"{fileName} ({w}×{h})";
        NavigatorHeader = "네비게이터";
        StatusText = $"{fileName} ({w}×{h}) · {_selectedWidthTiles.Tiles}×{_selectedHeightTiles.Tiles}칸";
        NotifyCanvasChanged();
    }

    public async Task RotateClockwise90Async()
    {
        if (_sourcePixels is null || _isBusy) return;
        int newW = _sourceH;
        int newH = _sourceW;
        var rotated = new Rgb24[newW * newH];

        for (int y = 0; y < _sourceH; y++)
        {
            for (int x = 0; x < _sourceW; x++)
            {
                int nx = _sourceH - 1 - y;
                int ny = x;
                rotated[ny * newW + nx] = _sourcePixels[y * _sourceW + x];
            }
        }

        _sourcePixels = rotated;
        _sourceW = newW;
        _sourceH = newH;
        CustomCrop = null;
        SourcePreview = WpfImageBridge.CreateBitmapFromRgb(_sourcePixels, _sourceW, _sourceH);

        if (_isImageFirstMode)
        {
            // Swap W and H tiles cleanly on 90-degree rotation
            _syncingTiles = true;
            (_selectedWidthTiles, _selectedHeightTiles) = (_selectedHeightTiles, _selectedWidthTiles);
            RaisePropertyChanged(nameof(SelectedWidthTiles));
            RaisePropertyChanged(nameof(SelectedHeightTiles));
            _syncingTiles = false;
        }

        if (HasResult) await ConvertAsync();
        else NotifyCanvasChanged();
    }

    public async Task FlipHorizontalAsync()
    {
        if (_sourcePixels is null || _isBusy) return;
        var flipped = new Rgb24[_sourceW * _sourceH];
        for (int y = 0; y < _sourceH; y++)
        {
            int row = y * _sourceW;
            for (int x = 0; x < _sourceW; x++)
                flipped[row + (_sourceW - 1 - x)] = _sourcePixels[row + x];
        }

        _sourcePixels = flipped;
        if (_customCrop is { } c)
            CustomCrop = new CropRect(Math.Max(0, _sourceW - c.X - c.Width), c.Y, c.Width, c.Height);
        SourcePreview = WpfImageBridge.CreateBitmapFromRgb(_sourcePixels, _sourceW, _sourceH);

        if (HasResult) await ConvertAsync();
        else NotifyCanvasChanged();
    }

    public void StepWidthTiles(int delta)
    {
        int next = Math.Clamp(_selectedWidthTiles.Tiles + delta, 1, MaxTiles);
        SelectedWidthTiles = GetOrAddGridOption(next);
    }

    public void StepHeightTiles(int delta)
    {
        int next = Math.Clamp(_selectedHeightTiles.Tiles + delta, 1, MaxTiles);
        SelectedHeightTiles = GetOrAddGridOption(next);
    }

    public void ApplyCustomCrop(CropRect? crop)
    {
        CustomCrop = crop;
        if (_isImageFirstMode)
            SyncHeightFromWidth();

        StatusText = crop is { } c
            ? $"자르기 {c.Width}×{c.Height} → {_selectedWidthTiles.Tiles}×{_selectedHeightTiles.Tiles}칸"
            : "자르기 해제됨";
    }

    public async Task ConvertAsync()
    {
        if (_sourcePixels is null || _isBusy) return;

        int reqW = _selectedWidthTiles.Pixels;
        int reqH = _selectedHeightTiles.Pixels;

        var excluded = PaletteFilterItems.Where(x => !x.IsIncluded).Select(x => x.ItemName).ToList();
        ItemPalette activePalette = excluded.Count > 0
            ? _fullPalette.Filter(excludeItems: excluded)
            : _fullPalette;

        ResizeMode effectiveMode;
        CropRect? effectiveCrop = _customCrop;

        if (effectiveCrop is not null)
        {
            effectiveMode = ResizeMode.Stretch;
        }
        else if (_isImageFirstMode)
        {
            // In ImageFirst mode, WxH is already matched to image aspect; Cover trims sub-tile rounding cleanly
            effectiveMode = ResizeMode.Cover;
        }
        else
        {
            effectiveMode = _selectedGridFit.Mode;
        }

        var opts = new ConversionOptions(
            Width: reqW,
            Height: reqH,
            ResizeMode: effectiveMode,
            ResampleMode: ResampleMode.Box,
            Metric: SelectedMetric,
            Dithering: SelectedDither,
            Crop: effectiveCrop);

        var pixelsCopy = _sourcePixels;
        int sw = _sourceW, sh = _sourceH;

        IsBusy = true;
        StatusText = "변환 중...";
        try
        {
            var timer = Stopwatch.StartNew();
            ConversionResult res = await Task.Run(() =>
                ImageConverterEngine.Convert(pixelsCopy, sw, sh, activePalette, opts));
            timer.Stop();

            Result = res;
            _undoStack.Clear();
            RaisePropertyChanged(nameof(CanUndo));
            _selectedTile = (0, 0);
            _completedTiles.Clear();
            _completedColors.Clear();
            _highlightedPaletteIdx = null;
            _highlightedItemName = null;

            RefreshTileChoices();
            RebuildTileChecklist();
            if (TileColors.Count > 0)
                _activeBrushPaletteIdx = TileColors[0].PaletteIndex;
            NotifyCanvasChanged();

            var (cols, rows) = res.TileGridShape(TileSize);
            string stem = _sourceFilePath is not null ? Path.GetFileName(_sourceFilePath) : "DrawingHelper";
            OverviewTitle = $"{stem} · {res.Width}×{res.Height}";
            NavigatorHeader = $"네비게이터 · (1, 1)";
            StatusText = $"{res.Width}×{res.Height} · {cols}×{rows}칸 · ΔE {res.MeanDeltaE2000:F1} · {timer.Elapsed.TotalMilliseconds:F0}ms";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SelectAllFilterItems(bool include)
    {
        foreach (var item in PaletteFilterItems)
            item.IsIncluded = include;
    }

    // ------------------------------------------------------------------
    // Tile Navigation & Pixel Tools (Select, Eyedropper, Pencil, Done, Undo)
    // ------------------------------------------------------------------
    public void SelectTile((int Col, int Row) tile)
    {
        if (_result is null) return;
        var (cols, rows) = _result.TileGridShape(TileSize);
        if (tile.Col < 0 || tile.Col >= cols || tile.Row < 0 || tile.Row >= rows) return;

        _selectedTile = tile;
        _highlightedPaletteIdx = null;
        _highlightedItemName = null;
        _selectedColorRow = null;
        RaisePropertyChanged(nameof(SelectedColorRow));

        _isCurrentTileDone = _completedTiles.Contains(tile);
        RaisePropertyChanged(nameof(IsCurrentTileDone));
        NavigatorHeader = $"네비게이터 · ({tile.Col + 1}, {tile.Row + 1})";

        RefreshTileChoices();
        RebuildTileChecklist();
        NotifyCanvasChanged();
    }

    public void OnNavigatorTileHovered(int col, int row)
    {
        if (_result is null) return;
        NavigatorHeader = (col, row) == _selectedTile
            ? $"네비게이터 · ({col + 1}, {row + 1})"
            : $"네비게이터 · ({_selectedTile.Col + 1},{_selectedTile.Row + 1}) → ({col + 1},{row + 1})";
    }

    public void OnNavigatorHoverLeft()
    {
        if (_result is null) return;
        NavigatorHeader = $"네비게이터 · ({_selectedTile.Col + 1}, {_selectedTile.Row + 1})";
    }

    public void StepTile(int delta)
    {
        if (_result is null || TileChoices.Count == 0) return;
        int cur = -1;
        for (int i = 0; i < TileChoices.Count; i++)
        {
            if (TileChoices[i].Col == _selectedTile.Col && TileChoices[i].Row == _selectedTile.Row)
            {
                cur = i;
                break;
            }
        }
        int next = (cur + delta + TileChoices.Count) % TileChoices.Count;
        SelectTile((TileChoices[next].Col, TileChoices[next].Row));
    }

    public void MoveTileDirection(int dx, int dy)
    {
        if (_result is null) return;
        var (cols, rows) = _result.TileGridShape(TileSize);
        int nc = Math.Clamp(_selectedTile.Col + dx, 0, cols - 1);
        int nr = Math.Clamp(_selectedTile.Row + dy, 0, rows - 1);
        if ((nc, nr) != _selectedTile)
            SelectTile((nc, nr));
    }

    private void RefreshTileChoices()
    {
        if (_result is null) return;
        var (cols, rows) = _result.TileGridShape(TileSize);
        TileChoices.Clear();
        TileChoice? active = null;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                string mark = _completedTiles.Contains((c, r)) ? "✓ " : "";
                var choice = new TileChoice(c, r, $"{mark}({c + 1}, {r + 1})");
                TileChoices.Add(choice);
                if (c == _selectedTile.Col && r == _selectedTile.Row)
                    active = choice;
            }
        }

        _selectedTileChoice = active;
        RaisePropertyChanged(nameof(SelectedTileChoice));
    }

    private void RebuildTileChecklist()
    {
        TileColors.Clear();
        if (_result is null) return;

        var (sub, _) = _result.SliceTile(_selectedTile.Col, _selectedTile.Row, TileSize);
        var doneSet = _completedColors.TryGetValue(_selectedTile, out var set) ? set : [];
        var usages = sub.GetColorCounts();

        int doneCount = 0;
        foreach (var u in usages)
        {
            bool isDone = doneSet.Contains(u.Color.Index);
            if (isDone) doneCount++;
            TileColors.Add(new TileColorRow
            {
                PaletteIndex = u.Color.Index,
                Color = u.Color,
                SwatchBrush = FreezeBrush(Color.FromRgb(u.Color.Rgb.R, u.Color.Rgb.G, u.Color.Rgb.B)),
                PixelCount = u.Count,
                IsDone = isDone
            });
        }

        ChecklistHeader = $"({_selectedTile.Col + 1},{_selectedTile.Row + 1})  {doneCount}/{TileColors.Count}";
    }

    public void HighlightPaletteIndex(int pidx)
    {
        if (_result is null || pidx < 0 || pidx >= _result.Palette.Count) return;
        _activeBrushPaletteIdx = pidx;
        var color = _result.Palette[pidx];
        if (HighlightWholeItem)
        {
            _highlightedPaletteIdx = null;
            _highlightedItemName = color.ItemName;
            StatusText = $"{color.KoreanName} (전체)";
        }
        else
        {
            _highlightedPaletteIdx = pidx;
            _highlightedItemName = null;
            StatusText = $"{color.KoreanName} {color.Shade}단계";
        }

        var match = TileColors.FirstOrDefault(r => r.PaletteIndex == pidx);
        if (match is not null && !ReferenceEquals(_selectedColorRow, match))
        {
            _selectedColorRow = match;
            RaisePropertyChanged(nameof(SelectedColorRow));
        }

        NotifyCanvasChanged();
    }

    public void ClearHighlight()
    {
        if (_highlightedPaletteIdx is null && _highlightedItemName is null && _selectedColorRow is null)
            return;
        _highlightedPaletteIdx = null;
        _highlightedItemName = null;
        _selectedColorRow = null;
        RaisePropertyChanged(nameof(SelectedColorRow));
        NotifyCanvasChanged();
        StatusText = "";
    }

    public void OnCanvasPixelClicked(int lx, int ly, int gx, int gy)
    {
        if (_result is null) return;
        int pidx = _result.PaletteIndices[gy * _result.Width + gx];
        if (pidx < 0) return;

        var color = _result.Palette[pidx];
        if (_highlightedPaletteIdx == pidx || _highlightedItemName == color.ItemName)
            ClearHighlight();
        else
            HighlightPaletteIndex(pidx);
    }

    public void OnCanvasPixelPicked(int lx, int ly, int gx, int gy)
    {
        if (_result is null) return;
        int pidx = _result.PaletteIndices[gy * _result.Width + gx];
        if (pidx < 0) return;

        _activeBrushPaletteIdx = pidx;
        var color = _result.Palette[pidx];
        var match = TileColors.FirstOrDefault(r => r.PaletteIndex == pidx);
        if (match is not null)
        {
            _selectedColorRow = match;
            RaisePropertyChanged(nameof(SelectedColorRow));
        }
        StatusText = $"브러시 선택: {color.KoreanName} {color.Shade}단계 ({color.HexCode})";
    }

    public void OnCanvasPixelPainted(int lx, int ly, int gx, int gy)
    {
        if (_result is null) return;
        if (_activeBrushPaletteIdx < 0 || _activeBrushPaletteIdx >= _result.Palette.Count) return;

        int prev = _result.SetPixel(gx, gy, _activeBrushPaletteIdx);
        if (prev < 0 || prev == _activeBrushPaletteIdx) return;

        _undoStack.Push((gx, gy, prev));
        RaisePropertyChanged(nameof(CanUndo));

        int keepIdx = _activeBrushPaletteIdx;
        RebuildTileChecklist();
        var match = TileColors.FirstOrDefault(r => r.PaletteIndex == keepIdx);
        if (match is not null)
        {
            _selectedColorRow = match;
            RaisePropertyChanged(nameof(SelectedColorRow));
        }
        OnCanvasPixelHovered(lx, ly, gx, gy);
        NotifyCanvasChanged();
    }

    public void UndoPixelEdit()
    {
        if (_result is null || _undoStack.Count == 0) return;
        var (gx, gy, prevIdx) = _undoStack.Pop();
        _result.SetPixel(gx, gy, prevIdx);
        RaisePropertyChanged(nameof(CanUndo));
        RebuildTileChecklist();
        NotifyCanvasChanged();
        StatusText = $"되돌리기 ({_undoStack.Count})";
    }

    public void OnCanvasPixelToggleDone(int lx, int ly, int gx, int gy)
    {
        if (_result is null) return;
        int pidx = _result.PaletteIndices[gy * _result.Width + gx];
        if (pidx < 0) return;
        var match = TileColors.FirstOrDefault(r => r.PaletteIndex == pidx);
        if (match is null) return;

        _selectedColorRow = match;
        RaisePropertyChanged(nameof(SelectedColorRow));
        ToggleSelectedColorDone();
    }

    public void OnCanvasPixelHovered(int lx, int ly, int gx, int gy)
    {
        if (_result is null) return;
        int offset = gy * _result.Width + gx;
        int pidx = _result.PaletteIndices[offset];
        if (pidx < 0) return;

        var color = _result.Palette[pidx];
        HoverSwatchBrush = FreezeBrush(Color.FromRgb(color.Rgb.R, color.Rgb.G, color.Rgb.B));
        HoverCoordText = $"({lx + 1},{ly + 1})";
        HoverItemText = $"{color.KoreanName} {color.Shade}단계";
        HoverDetailText = color.HexCode;
    }

    public void ToggleSelectedColorDone()
    {
        if (_result is null || SelectedColorRow is null) return;
        var row = SelectedColorRow;
        if (!_completedColors.TryGetValue(_selectedTile, out var set))
        {
            set = [];
            _completedColors[_selectedTile] = set;
        }

        if (row.IsDone)
        {
            set.Remove(row.PaletteIndex);
            row.IsDone = false;
        }
        else
        {
            set.Add(row.PaletteIndex);
            row.IsDone = true;
        }

        int doneCnt = TileColors.Count(r => r.IsDone);
        ChecklistHeader = $"({_selectedTile.Col + 1},{_selectedTile.Row + 1})  {doneCnt}/{TileColors.Count}";

        if (TileColors.Count > 0 && doneCnt == TileColors.Count)
        {
            _completedTiles.Add(_selectedTile);
            _isCurrentTileDone = true;
            RaisePropertyChanged(nameof(IsCurrentTileDone));
            RefreshTileChoices();
        }
        else if (!row.IsDone && _completedTiles.Remove(_selectedTile))
        {
            _isCurrentTileDone = false;
            RaisePropertyChanged(nameof(IsCurrentTileDone));
            RefreshTileChoices();
        }

        NotifyCanvasChanged();
    }

    public void StepChecklistColor(int delta)
    {
        if (TileColors.Count == 0) return;
        int start = SelectedColorRow is not null ? TileColors.IndexOf(SelectedColorRow) : -1;
        int n = TileColors.Count;

        for (int s = 1; s <= n; s++)
        {
            int idx = ((start + delta * s) % n + n) % n;
            if (!TileColors[idx].IsDone)
            {
                SelectedColorRow = TileColors[idx];
                return;
            }
        }

        int fallback = ((start + delta) % n + n) % n;
        SelectedColorRow = TileColors[fallback];
    }

    // ------------------------------------------------------------------
    // Save / Progress
    // ------------------------------------------------------------------
    public void SaveFullPng(string path)
    {
        if (_result is null) return;
        WpfImageBridge.SaveRgbAsPng(path, _result.PixelRgb, _result.Width, _result.Height);
        StatusText = $"저장: {Path.GetFileName(path)}";
    }

    public void SaveCurrentTilePng(string path)
    {
        if (_result is null) return;
        var (sub, _) = _result.SliceTile(_selectedTile.Col, _selectedTile.Row, TileSize);
        WpfImageBridge.SaveRgbAsPng(path, sub.PixelRgb, sub.Width, sub.Height);
        StatusText = $"저장: {Path.GetFileName(path)}";
    }

    public void SaveProgress(string path)
    {
        if (_result is null) return;
        var prog = new ProjectProgress
        {
            Width = _result.Width,
            Height = _result.Height,
            SelectedTile = [_selectedTile.Col, _selectedTile.Row],
            CompletedTiles = _completedTiles.OrderBy(t => t.Row).ThenBy(t => t.Col)
                .Select(t => new[] { t.Col, t.Row }).ToList(),
            CompletedColors = _completedColors
                .Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => $"{kv.Key.Col},{kv.Key.Row}", kv => kv.Value.Order().ToList())
        };
        ProgressStore.Save(path, prog);
        StatusText = $"저장: {Path.GetFileName(path)}";
    }

    public void LoadProgress(string path)
    {
        if (_result is null) return;
        var prog = ProgressStore.Load(path);
        _completedTiles.Clear();
        foreach (var t in prog.CompletedTiles)
            if (t.Length == 2) _completedTiles.Add((t[0], t[1]));

        _completedColors.Clear();
        foreach (var kv in prog.CompletedColors)
        {
            string[] parts = kv.Key.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int c) && int.TryParse(parts[1], out int r))
                _completedColors[(c, r)] = [.. kv.Value];
        }

        if (prog.SelectedTile.Length == 2)
            SelectTile((prog.SelectedTile[0], prog.SelectedTile[1]));
        else
            SelectTile((0, 0));

        StatusText = $"불러옴 ({_completedTiles.Count}/{TileChoices.Count})";
    }

    private void NotifyCanvasChanged() => CanvasStateChanged?.Invoke();
}
