using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingHelper.Core.Matching;
using DrawingHelper.Core.Palette;

namespace DrawingHelper.App.Controls;

public enum HighlightOutlineColor
{
    Yellow,
    Cyan
}

/// <summary>
/// High-performance NearestNeighbor WPF pixel canvas with smooth Zoom (MouseWheel / Ctrl+Plus/Minus/0)
/// and Pan (Middle-drag / Shift+Left-drag / Right-drag when zoomed), modern dark styling,
/// pinned 1..32 coordinate headers, and adaptive-contrast color isolation.
/// </summary>
public sealed class PixelTileCanvas : FrameworkElement
{
    public const int TileSize = 32;
    private const double MinZoom = 1.0;
    private const double MaxZoom = 12.0;

    // Photoshop / Affinity neutral dark studio brushes (zero color cast)
    private static readonly SolidColorBrush BgWellBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly SolidColorBrush HeaderFaceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)));
    private static readonly SolidColorBrush HeaderHotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x80, 0xEB)));
    private static readonly SolidColorBrush HeaderTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0x96)));
    private static readonly SolidColorBrush HeaderHotTextBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen HeaderBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x19)), 1.0));
    private static readonly Pen TileBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 1.5));
    private static readonly Pen ActiveTilePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x80, 0xEB)), 2.5));
    private static readonly Pen CrosshairLinePen = Freeze(CreateDashPen(Color.FromArgb(190, 0x3B, 0x90, 0xF5)));
    private static readonly Pen HoverCellPen = Freeze(new Pen(Brushes.White, 2.0));
    private static readonly Pen HoverCellOuterPen = Freeze(new Pen(Brushes.Black, 1.0));
    private static readonly SolidColorBrush BadgeBgBrush = Freeze(new SolidColorBrush(Color.FromArgb(220, 0x18, 0x18, 0x1B)));
    private static readonly SolidColorBrush BadgeDoneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
    private static readonly SolidColorBrush BadgeNormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
    private static readonly Typeface HeaderTypeface = new(new FontFamily("Segoe UI, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface HeaderBoldTypeface = new(new FontFamily("Segoe UI, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Freeze<T>(T obj) where T : Freezable { obj.Freeze(); return obj; }

    private static Pen CreateDashPen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1.0)
        {
            DashStyle = new DashStyle([3, 3], 0)
        };
        return pen;
    }

    // Current rendering inputs
    private ConversionResult? _result;
    private bool _isOverviewMode;
    private (int Col, int Row) _selectedTile = (0, 0);
    private int? _highlightedPaletteIdx;
    private string? _highlightedItemName;
    private HighlightOutlineColor _outlineColor = HighlightOutlineColor.Yellow;
    private bool _showGrid = true;
    private bool _showCrosshair = true;
    private bool _dimCompleted = true;
    private IReadOnlySet<(int Col, int Row)> _completedTiles = new HashSet<(int, int)>();
    private IReadOnlySet<int> _completedColorsInTile = new HashSet<int>();

    // Zoom & pan state (1.0 = fit viewport)
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private bool _panMoved;
    private bool _panStartedByRightButton;
    private Point _panLastPt;
    private Point _panStartPt;

    // Cached pixel bitmap
    private WriteableBitmap? _cachedBitmap;
    private bool _bitmapDirty = true;
    private bool[]? _cachedHlMask;
    private int _srcX0, _srcY0, _srcW, _srcH;

    // Last rendered geometry
    private Rect _drawRect = Rect.Empty;
    private Rect _viewportRect = Rect.Empty;
    private Rect _cornerRect = Rect.Empty;
    private (int Lx, int Ly)? _hoverPixel;

    public event Action<int, int, int, int>? PixelHovered;
    public event Action? HoverLeft;
    public event Action<int, int, int, int>? PixelClicked;
    public event Action<int, int>? TileClicked;
    public event Action? ClearHighlightRequested;
    public event Action<double>? ZoomChanged;

    public PixelTileCanvas()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public double ZoomFactor => _zoom;

    public bool IsOverviewMode
    {
        get => _isOverviewMode;
        set
        {
            if (_isOverviewMode == value) return;
            _isOverviewMode = value;
            _bitmapDirty = true;
            ResetZoom();
        }
    }

    public void ZoomBy(double factor, Point? anchor = null)
    {
        double oldZoom = _zoom;
        double newZoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 1e-4) return;

        if (newZoom <= 1.001)
        {
            _zoom = 1.0;
            _panX = 0;
            _panY = 0;
        }
        else
        {
            Point pt = anchor ?? new Point(
                _viewportRect.IsEmpty ? ActualWidth * 0.5 : _viewportRect.X + _viewportRect.Width * 0.5,
                _viewportRect.IsEmpty ? ActualHeight * 0.5 : _viewportRect.Y + _viewportRect.Height * 0.5);

            double ratio = newZoom / oldZoom;
            double cx = _viewportRect.IsEmpty ? ActualWidth * 0.5 : _viewportRect.X + _viewportRect.Width * 0.5;
            double cy = _viewportRect.IsEmpty ? ActualHeight * 0.5 : _viewportRect.Y + _viewportRect.Height * 0.5;

            _panX = (pt.X - cx) * (1.0 - ratio) + _panX * ratio;
            _panY = (pt.Y - cy) * (1.0 - ratio) + _panY * ratio;
            _zoom = newZoom;
        }

        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    public void ResetZoom()
    {
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    public void UpdateState(
        ConversionResult? result,
        (int Col, int Row) selectedTile,
        int? highlightedPaletteIdx,
        string? highlightedItemName,
        HighlightOutlineColor outlineColor,
        bool showGrid,
        bool showCrosshair,
        bool dimCompleted,
        IReadOnlySet<(int Col, int Row)> completedTiles,
        IReadOnlySet<int> completedColorsInTile)
    {
        bool tileOrResultChanged = !ReferenceEquals(_result, result) || _selectedTile != selectedTile;
        _result = result;
        _selectedTile = selectedTile;
        _highlightedPaletteIdx = highlightedPaletteIdx;
        _highlightedItemName = highlightedItemName;
        _outlineColor = outlineColor;
        _showGrid = showGrid;
        _showCrosshair = showCrosshair;
        _dimCompleted = dimCompleted;
        _completedTiles = completedTiles;
        _completedColorsInTile = completedColorsInTile;
        _bitmapDirty = true;

        if (tileOrResultChanged)
        {
            _zoom = 1.0;
            _panX = 0;
            _panY = 0;
            ZoomChanged?.Invoke(_zoom);
        }

        InvalidateVisual();
    }

    private void RebuildBitmapIfNeeded()
    {
        if (!_bitmapDirty) return;
        _bitmapDirty = false;
        _cachedBitmap = null;
        _cachedHlMask = null;

        if (_result is null) return;

        if (_isOverviewMode)
        {
            _srcX0 = 0; _srcY0 = 0;
            _srcW = _result.Width;
            _srcH = _result.Height;
            _cachedBitmap = BuildBitmap(_result.PixelRgb, _srcW, _srcH);
            return;
        }

        var (cols, rows) = _result.TileGridShape(TileSize);
        int tc = Math.Clamp(_selectedTile.Col, 0, Math.Max(0, cols - 1));
        int tr = Math.Clamp(_selectedTile.Row, 0, Math.Max(0, rows - 1));
        var (sub, bounds) = _result.SliceTile(tc, tr, TileSize);

        _srcX0 = bounds.X0;
        _srcY0 = bounds.Y0;
        _srcW = sub.Width;
        _srcH = sub.Height;

        int n = sub.PixelRgb.Length;
        var disp = new Rgb24[n];

        bool[]? hlMask = null;
        if (_highlightedPaletteIdx is int targetPidx)
        {
            hlMask = new bool[n];
            for (int i = 0; i < n; i++)
                hlMask[i] = sub.PaletteIndices[i] == targetPidx;
        }
        else if (_highlightedItemName is string targetItem)
        {
            hlMask = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int idx = sub.PaletteIndices[i];
                hlMask[i] = idx >= 0 && sub.Palette[idx].ItemName == targetItem;
            }
        }

        _cachedHlMask = hlMask;

        // Keep original background colors intact; only optionally dim completed colors
        if (_dimCompleted && _completedColorsInTile.Count > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var p = sub.PixelRgb[i];
                if (_completedColorsInTile.Contains(sub.PaletteIndices[i]) && (hlMask is null || !hlMask[i]))
                {
                    double gray = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    byte v = (byte)Math.Clamp((int)Math.Round(gray * 0.45 + 64.0), 0, 255);
                    disp[i] = new Rgb24(v, v, v);
                }
                else
                {
                    disp[i] = p;
                }
            }
        }
        else
        {
            Array.Copy(sub.PixelRgb, disp, n);
        }

        _cachedBitmap = BuildBitmap(disp, _srcW, _srcH);
    }

    private static WriteableBitmap BuildBitmap(Rgb24[] pixels, int w, int h)
    {
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        var buf = new byte[w * h * 3];
        for (int i = 0; i < pixels.Length; i++)
        {
            int o = i * 3;
            buf[o] = pixels[i].B;
            buf[o + 1] = pixels[i].G;
            buf[o + 2] = pixels[i].R;
        }
        wb.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 3, 0);
        wb.Freeze();
        return wb;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double aw = ActualWidth;
        double ah = ActualHeight;
        dc.DrawRectangle(BgWellBrush, null, new Rect(0, 0, aw, ah));

        RebuildBitmapIfNeeded();
        if (_cachedBitmap is null || _result is null || aw < 20 || ah < 20)
        {
            _drawRect = Rect.Empty;
            _viewportRect = Rect.Empty;
            _cornerRect = Rect.Empty;
            return;
        }

        if (_isOverviewMode)
            RenderOverview(dc, aw, ah);
        else
            RenderPaintSheet(dc, aw, ah);
    }

    private void RenderOverview(DrawingContext dc, double aw, double ah)
    {
        const double pad = 8;
        _viewportRect = new Rect(0, 0, aw, ah);
        _cornerRect = Rect.Empty;

        double baseScale = Math.Min((aw - pad * 2) / _srcW, (ah - pad * 2) / _srcH);
        double scale = baseScale * _zoom;
        double dw = Math.Max(1, _srcW * scale);
        double dh = Math.Max(1, _srcH * scale);

        ClampPan(dw, dh, aw, ah);

        double x0 = Math.Floor((aw - dw) * 0.5 + _panX);
        double y0 = Math.Floor((ah - dh) * 0.5 + _panY);
        _drawRect = new Rect(x0, y0, dw, dh);

        dc.DrawImage(_cachedBitmap!, _drawRect);
        dc.DrawRectangle(null, HeaderBorderPen, _drawRect);

        if (!_showGrid || _result is null) return;

        var (cols, rows) = _result.TileGridShape(TileSize);
        for (int gx = TileSize; gx < _srcW; gx += TileSize)
        {
            double lx = x0 + gx * dw / _srcW;
            dc.DrawLine(TileBorderPen, new Point(lx, y0), new Point(lx, y0 + dh));
        }
        for (int gy = TileSize; gy < _srcH; gy += TileSize)
        {
            double ly = y0 + gy * dh / _srcH;
            dc.DrawLine(TileBorderPen, new Point(x0, ly), new Point(x0 + dw, ly));
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int tr = 0; tr < rows; tr++)
        {
            for (int tc = 0; tc < cols; tc++)
            {
                bool done = _completedTiles.Contains((tc, tr));
                double bx = x0 + (tc * TileSize) * dw / _srcW + 3;
                double by = y0 + (tr * TileSize) * dh / _srcH + 3;
                string label = done ? $"{tc + 1},{tr + 1}✓" : $"{tc + 1},{tr + 1}";
                var ft = new FormattedText(
                    label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    HeaderBoldTypeface, 10, done ? BadgeDoneBrush : BadgeNormalBrush, dpi);
                var rect = new Rect(bx, by, ft.Width + 6, ft.Height + 2);
                dc.DrawRectangle(BadgeBgBrush, done ? new Pen(BadgeDoneBrush, 1) : new Pen(BadgeNormalBrush, 1), rect);
                dc.DrawText(ft, new Point(bx + 3, by + 1));
            }
        }

        if (_selectedTile.Col < cols && _selectedTile.Row < rows)
        {
            var (_, b) = _result.SliceTile(_selectedTile.Col, _selectedTile.Row, TileSize);
            dc.DrawRectangle(null, ActiveTilePen, new Rect(
                x0 + b.X0 * dw / _srcW,
                y0 + b.Y0 * dh / _srcH,
                b.Width * dw / _srcW,
                b.Height * dh / _srcH));
        }
    }

    private void RenderPaintSheet(DrawingContext dc, double aw, double ah)
    {
        const double headerW = 24;
        const double headerH = 20;
        const double pad = 4;

        _viewportRect = new Rect(headerW, headerH, Math.Max(1, aw - headerW), Math.Max(1, ah - headerH));
        _cornerRect = new Rect(0, 0, headerW, headerH);

        double baseCell = Math.Max(2.0, Math.Min((_viewportRect.Width - pad * 2) / _srcW, (_viewportRect.Height - pad * 2) / _srcH));
        double cell = baseCell * _zoom;
        if (_zoom <= 1.001) cell = Math.Max(2.0, Math.Floor(baseCell));

        double dw = cell * _srcW;
        double dh = cell * _srcH;
        ClampPan(dw, dh, _viewportRect.Width, _viewportRect.Height);

        double x0 = Math.Floor(_viewportRect.X + (_viewportRect.Width - dw) * 0.5 + _panX);
        double y0 = Math.Floor(_viewportRect.Y + (_viewportRect.Height - dh) * 0.5 + _panY);
        _drawRect = new Rect(x0, y0, dw, dh);

        // 1. Clipped Pixel Viewport
        dc.PushClip(new RectangleGeometry(_viewportRect));

        dc.DrawImage(_cachedBitmap!, _drawRect);

        if (_showGrid && cell >= 4)
        {
            var grid1Pen = new Pen(new SolidColorBrush(Color.FromArgb(115, 0x3A, 0x3C, 0x46)), 1.0);
            var grid8Pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0xF5, 0x9E, 0x0B)), 1.0);
            grid1Pen.Freeze();
            grid8Pen.Freeze();

            for (int x = 1; x < _srcW; x++)
            {
                double px = x0 + x * cell;
                dc.DrawLine((x % 8 == 0) ? grid8Pen : grid1Pen, new Point(px, y0), new Point(px, y0 + dh));
            }
            for (int y = 1; y < _srcH; y++)
            {
                double py = y0 + y * cell;
                dc.DrawLine((y % 8 == 0) ? grid8Pen : grid1Pen, new Point(x0, py), new Point(x0 + dw, py));
            }
        }

        // Trace ONLY the outer perimeter contour of selected color regions (background unchanged!)
        if (_cachedHlMask is not null && cell >= 4)
        {
            double strokeW = cell >= 14 ? 2.2 : 1.5;
            var backingPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 0x0A, 0x0A, 0x0D)), strokeW + 1.6);
            var contourColor = _outlineColor == HighlightOutlineColor.Yellow
                ? Color.FromRgb(0xFF, 0xE0, 0x33)
                : Color.FromRgb(0x38, 0xCE, 0xFF);
            var contourPen = new Pen(new SolidColorBrush(contourColor), strokeW);
            backingPen.StartLineCap = backingPen.EndLineCap = PenLineCap.Square;
            contourPen.StartLineCap = contourPen.EndLineCap = PenLineCap.Square;
            backingPen.Freeze();
            contourPen.Freeze();

            // Collect outer boundary segments once, then draw dark backing + bright contour
            var segments = new List<(Point A, Point B)>(256);
            for (int ly = 0; ly < _srcH; ly++)
            {
                int row = ly * _srcW;
                double py0 = y0 + ly * cell;
                double py1 = py0 + cell;
                for (int lx = 0; lx < _srcW; lx++)
                {
                    if (!_cachedHlMask[row + lx]) continue;
                    double px0 = x0 + lx * cell;
                    double px1 = px0 + cell;

                    if (ly == 0 || !_cachedHlMask[(ly - 1) * _srcW + lx])
                        segments.Add((new Point(px0, py0), new Point(px1, py0)));
                    if (ly == _srcH - 1 || !_cachedHlMask[(ly + 1) * _srcW + lx])
                        segments.Add((new Point(px0, py1), new Point(px1, py1)));
                    if (lx == 0 || !_cachedHlMask[row + lx - 1])
                        segments.Add((new Point(px0, py0), new Point(px0, py1)));
                    if (lx == _srcW - 1 || !_cachedHlMask[row + lx + 1])
                        segments.Add((new Point(px1, py0), new Point(px1, py1)));
                }
            }

            foreach (var (a, b) in segments)
                dc.DrawLine(backingPen, a, b);
            foreach (var (a, b) in segments)
                dc.DrawLine(contourPen, a, b);
        }

        dc.DrawRectangle(null, HeaderBorderPen, _drawRect);

        if (_showCrosshair && _hoverPixel is { Lx: var cx, Ly: var cy })
        {
            double px0 = x0 + cx * cell;
            double py0 = y0 + cy * cell;
            dc.DrawRectangle(null, CrosshairLinePen, new Rect(x0, py0, dw, cell));
            dc.DrawRectangle(null, CrosshairLinePen, new Rect(px0, y0, cell, dh));
            dc.DrawRectangle(null, HoverCellOuterPen, new Rect(px0, py0, cell, cell));
            dc.DrawRectangle(null, HoverCellPen, new Rect(px0 + 1, py0 + 1, Math.Max(1, cell - 2), Math.Max(1, cell - 2)));
        }

        dc.Pop();

        // 2. Pinned Modern Dark Headers (top & left, synced to zoom/pan)
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int labelStep = cell >= 16 ? 1 : (cell >= 8 ? 4 : 8);

        // Top header bar background
        dc.DrawRectangle(HeaderFaceBrush, null, new Rect(headerW, 0, Math.Max(0, aw - headerW), headerH));
        dc.PushClip(new RectangleGeometry(new Rect(headerW, 0, Math.Max(0, aw - headerW), headerH)));
        for (int lx = 0; lx < _srcW; lx++)
        {
            double cx0 = x0 + lx * cell;
            if (cx0 + cell < headerW || cx0 > aw) continue;
            bool isHot = _hoverPixel is { Lx: var hx } && hx == lx;
            var r = new Rect(cx0, 0, cell, headerH);
            dc.DrawRectangle(isHot ? HeaderHotBrush : HeaderFaceBrush, HeaderBorderPen, r);
            if (isHot || lx % labelStep == 0 || lx == _srcW - 1)
            {
                if (cell >= 12 || isHot || lx % labelStep == 0)
                {
                    var ft = new FormattedText(
                        (lx + 1).ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        isHot ? HeaderBoldTypeface : HeaderTypeface,
                        cell >= 14 ? 10 : 9, isHot ? HeaderHotTextBrush : HeaderTextBrush, dpi);
                    dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) * 0.5, r.Y + (r.Height - ft.Height) * 0.5));
                }
            }
        }
        dc.Pop();

        // Left header bar background
        dc.DrawRectangle(HeaderFaceBrush, null, new Rect(0, headerH, headerW, Math.Max(0, ah - headerH)));
        dc.PushClip(new RectangleGeometry(new Rect(0, headerH, headerW, Math.Max(0, ah - headerH))));
        for (int ly = 0; ly < _srcH; ly++)
        {
            double cy0 = y0 + ly * cell;
            if (cy0 + cell < headerH || cy0 > ah) continue;
            bool isHot = _hoverPixel is { Ly: var hy } && hy == ly;
            var r = new Rect(0, cy0, headerW, cell);
            dc.DrawRectangle(isHot ? HeaderHotBrush : HeaderFaceBrush, HeaderBorderPen, r);
            if (isHot || ly % labelStep == 0 || ly == _srcH - 1)
            {
                if (cell >= 11 || isHot || ly % labelStep == 0)
                {
                    var ft = new FormattedText(
                        (ly + 1).ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        isHot ? HeaderBoldTypeface : HeaderTypeface,
                        cell >= 13 ? 10 : 9, isHot ? HeaderHotTextBrush : HeaderTextBrush, dpi);
                    dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) * 0.5, r.Y + (r.Height - ft.Height) * 0.5));
                }
            }
        }
        dc.Pop();

        // Top-left corner reset/clear button
        dc.DrawRectangle(HeaderFaceBrush, HeaderBorderPen, _cornerRect);
    }

    private void ClampPan(double contentW, double contentH, double viewW, double viewH)
    {
        double maxPanX = Math.Max(0, (contentW - viewW) * 0.5 + 32);
        double maxPanY = Math.Max(0, (contentH - viewH) * 0.5 + 32);
        if (_zoom <= 1.001) { _panX = 0; _panY = 0; }
        else
        {
            _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
            _panY = Math.Clamp(_panY, -maxPanY, maxPanY);
        }
    }

    // ------------------------------------------------------------------
    // Mouse Zoom / Pan / Hit-Testing
    // ------------------------------------------------------------------
    private (int Lx, int Ly)? HitTestPixel(Point pt)
    {
        if (_result is null || _drawRect.IsEmpty || !_viewportRect.Contains(pt) || !_drawRect.Contains(pt) || _srcW <= 0 || _srcH <= 0)
            return null;
        int lx = Math.Clamp((int)((pt.X - _drawRect.X) * _srcW / _drawRect.Width), 0, _srcW - 1);
        int ly = Math.Clamp((int)((pt.Y - _drawRect.Y) * _srcH / _drawRect.Height), 0, _srcH - 1);
        return (lx, ly);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_result is null) return;
        double factor = e.Delta > 0 ? 1.22 : (1.0 / 1.22);
        ZoomBy(factor, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Point pt = e.GetPosition(this);

        // Middle-drag or Shift+Left-drag or Right-drag (when zoomed) pans
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ||
            (e.ChangedButton == MouseButton.Right && _zoom > 1.001))
        {
            _isPanning = true;
            _panMoved = false;
            _panStartedByRightButton = e.ChangedButton == MouseButton.Right;
            _panStartPt = pt;
            _panLastPt = pt;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if (!_isOverviewMode && !_cornerRect.IsEmpty && _cornerRect.Contains(pt))
            {
                ClearHighlightRequested?.Invoke();
                return;
            }
            var hit = HitTestPixel(pt);
            if (hit is not { Lx: var lx, Ly: var ly }) return;

            if (_isOverviewMode)
                TileClicked?.Invoke(lx / TileSize, ly / TileSize);
            else
                PixelClicked?.Invoke(lx, ly, _srcX0 + lx, _srcY0 + ly);
        }
        else if (e.ChangedButton == MouseButton.Right && !_isOverviewMode)
        {
            ClearHighlightRequested?.Invoke();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pt = e.GetPosition(this);

        if (_isPanning)
        {
            Vector delta = pt - _panLastPt;
            if ((pt - _panStartPt).Length > 4.0) _panMoved = true;
            _panX += delta.X;
            _panY += delta.Y;
            _panLastPt = pt;
            InvalidateVisual();
            return;
        }

        if (_isOverviewMode) return;
        var hit = HitTestPixel(pt);
        if (hit != _hoverPixel)
        {
            _hoverPixel = hit;
            InvalidateVisual();
            if (hit is { Lx: var lx, Ly: var ly })
                PixelHovered?.Invoke(lx, ly, _srcX0 + lx, _srcY0 + ly);
            else
                HoverLeft?.Invoke();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isPanning)
        {
            bool wasRightClickWithoutDrag = _panStartedByRightButton && !_panMoved;
            _isPanning = false;
            _panStartedByRightButton = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;

            if (wasRightClickWithoutDrag && !_isOverviewMode)
                ClearHighlightRequested?.Invoke();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isPanning && _hoverPixel is not null)
        {
            _hoverPixel = null;
            InvalidateVisual();
            HoverLeft?.Invoke();
        }
    }
}
