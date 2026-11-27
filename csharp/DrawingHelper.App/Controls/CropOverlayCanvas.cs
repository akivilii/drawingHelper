using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingHelper.Core.Matching;

namespace DrawingHelper.App.Controls;

/// <summary>
/// Studio interactive crop overlay over the source image:
/// - 8 resize handles (4 corners + 4 edges)
/// - Drag inside to move, drag outside to draw a brand-new crop box, double-click inside to apply
/// - Mouse wheel scales crop box around its center
/// - Live Minecraft 32x32 tile grid preview drawn right inside the crop frame!
/// </summary>
public sealed class CropOverlayCanvas : FrameworkElement
{
    private static readonly SolidColorBrush WellBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly SolidColorBrush MaskBrush = Freeze(new SolidColorBrush(Color.FromArgb(175, 0x0A, 0x0A, 0x0D)));
    private static readonly Pen CropBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), 2.0));
    private static readonly Pen TileGridPen = Freeze(CreateDashPen(Color.FromArgb(175, 0xFB, 0xBF, 0x24)));
    private static readonly SolidColorBrush HandleFillBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen HandleStrokePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x80, 0xEB)), 1.5));
    private static readonly SolidColorBrush InfoBadgeBg = Freeze(new SolidColorBrush(Color.FromArgb(215, 0x18, 0x18, 0x1B)));
    private static readonly Typeface BadgeFont = new(new FontFamily("Consolas, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Freeze<T>(T obj) where T : Freezable { obj.Freeze(); return obj; }

    private static Pen CreateDashPen(Color c)
    {
        var p = new Pen(new SolidColorBrush(c), 1.0) { DashStyle = new DashStyle([4, 3], 0) };
        return p;
    }

    private enum DragMode
    {
        None, Move, NewBox,
        TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left
    }

    private BitmapSource? _image;
    private int _imgW, _imgH;
    private double _targetAspect = 1.0; // W / H when locked
    private bool _lockAspect;
    private int _previewCols = 2, _previewRows = 2;

    // Crop rect in image pixel coordinates
    private double _cx, _cy, _cw, _ch;
    private Rect _imgScreenRect = Rect.Empty;

    private DragMode _dragMode = DragMode.None;
    private Point _dragStartPt;
    private double _origX, _origY, _origW, _origH;
    private double _newStartImgX, _newStartImgY;

    public event Action<CropRect>? CropChanged;
    public event Action? ApplyRequested;

    public CropOverlayCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public bool LockAspect
    {
        get => _lockAspect;
        set
        {
            if (_lockAspect == value) return;
            _lockAspect = value;
            if (_lockAspect && _image is not null)
                FitCropToAspectCentered();
            NotifyCrop();
            InvalidateVisual();
        }
    }

    public CropRect CurrentCrop => new(
        Math.Clamp((int)Math.Round(_cx), 0, Math.Max(0, _imgW - 1)),
        Math.Clamp((int)Math.Round(_cy), 0, Math.Max(0, _imgH - 1)),
        Math.Clamp((int)Math.Round(_cw), 1, Math.Max(1, _imgW)),
        Math.Clamp((int)Math.Round(_ch), 1, Math.Max(1, _imgH)));

    public void Load(
        BitmapSource? image,
        int imgW,
        int imgH,
        bool lockAspect,
        int tileCols,
        int tileRows,
        CropRect? existingCrop)
    {
        _image = image;
        _imgW = imgW;
        _imgH = imgH;
        _lockAspect = lockAspect;
        _previewCols = Math.Max(1, tileCols);
        _previewRows = Math.Max(1, tileRows);
        _targetAspect = _previewCols / (double)_previewRows;

        if (_image is null || _imgW <= 0 || _imgH <= 0)
        {
            InvalidateVisual();
            return;
        }

        if (existingCrop is { } c)
        {
            _cx = Math.Clamp(c.X, 0, Math.Max(0, _imgW - 1));
            _cy = Math.Clamp(c.Y, 0, Math.Max(0, _imgH - 1));
            _cw = Math.Clamp(c.Width, 16, _imgW - _cx);
            _ch = Math.Clamp(c.Height, 16, _imgH - _cy);
            if (_lockAspect)
                EnforceAspectInPlace();
        }
        else
        {
            FitCropToAspectCentered();
        }

        NotifyCrop();
        InvalidateVisual();
    }

    public void UpdateGridPreview(bool lockAspect, int tileCols, int tileRows)
    {
        _lockAspect = lockAspect;
        _previewCols = Math.Max(1, tileCols);
        _previewRows = Math.Max(1, tileRows);
        _targetAspect = _previewCols / (double)_previewRows;

        if (_lockAspect && _image is not null)
        {
            EnforceAspectInPlace();
            NotifyCrop();
        }
        InvalidateVisual();
    }

    public void ResetFullOrCentered()
    {
        if (_image is null || _imgW <= 0 || _imgH <= 0) return;
        FitCropToAspectCentered();
        NotifyCrop();
        InvalidateVisual();
    }

    private void EnforceAspectInPlace()
    {
        double centerX = _cx + _cw * 0.5;
        double centerY = _cy + _ch * 0.5;
        double nw = _cw;
        double nh = nw / _targetAspect;
        if (nh > _imgH) { nh = _imgH; nw = nh * _targetAspect; }
        if (nw > _imgW) { nw = _imgW; nh = nw / _targetAspect; }
        _cw = Math.Max(16, nw);
        _ch = Math.Max(16, nh);
        _cx = Math.Clamp(centerX - _cw * 0.5, 0, Math.Max(0, _imgW - _cw));
        _cy = Math.Clamp(centerY - _ch * 0.5, 0, Math.Max(0, _imgH - _ch));
    }

    private void FitCropToAspectCentered()
    {
        if (_imgW <= 0 || _imgH <= 0) return;
        if (_lockAspect)
        {
            double imgRatio = _imgW / (double)_imgH;
            if (imgRatio > _targetAspect)
            {
                _ch = _imgH;
                _cw = Math.Min(_imgW, _ch * _targetAspect);
            }
            else
            {
                _cw = _imgW;
                _ch = Math.Min(_imgH, _cw / _targetAspect);
            }
        }
        else
        {
            _cw = _imgW;
            _ch = _imgH;
        }
        _cx = (_imgW - _cw) * 0.5;
        _cy = (_imgH - _ch) * 0.5;
    }

    private void NotifyCrop() => CropChanged?.Invoke(CurrentCrop);

    protected override void OnRender(DrawingContext dc)
    {
        double aw = ActualWidth;
        double ah = ActualHeight;
        dc.DrawRectangle(WellBrush, null, new Rect(0, 0, aw, ah));

        if (_image is null || _imgW <= 0 || _imgH <= 0 || aw < 20 || ah < 20)
        {
            _imgScreenRect = Rect.Empty;
            return;
        }

        const double pad = 20;
        double scale = Math.Min((aw - pad * 2) / _imgW, (ah - pad * 2) / _imgH);
        double dw = _imgW * scale;
        double dh = _imgH * scale;
        double ix = (aw - dw) * 0.5;
        double iy = (ah - dh) * 0.5;
        _imgScreenRect = new Rect(ix, iy, dw, dh);

        dc.DrawImage(_image, _imgScreenRect);

        Rect cs = ImageToScreen(_cx, _cy, _cw, _ch);

        // Dark outer mask
        dc.DrawRectangle(MaskBrush, null, new Rect(ix, iy, dw, Math.Max(0, cs.Top - iy)));
        dc.DrawRectangle(MaskBrush, null, new Rect(ix, cs.Bottom, dw, Math.Max(0, _imgScreenRect.Bottom - cs.Bottom)));
        dc.DrawRectangle(MaskBrush, null, new Rect(ix, cs.Top, Math.Max(0, cs.Left - ix), cs.Height));
        dc.DrawRectangle(MaskBrush, null, new Rect(cs.Right, cs.Top, Math.Max(0, _imgScreenRect.Right - cs.Right), cs.Height));

        // Live Minecraft WxH tile grid lines inside crop frame!
        int showCols = _previewCols;
        int showRows = _previewRows;
        if (!_lockAspect && _cw > 0 && _ch > 0)
        {
            // In free-crop (ImageFirst) mode, preview how the cropped ratio maps to tiles
            showRows = Math.Clamp((int)Math.Round(showCols * (_ch / _cw)), 1, 128);
        }

        if (cs.Width / showCols >= 4.0)
        {
            for (int c = 1; c < showCols; c++)
            {
                double gx = cs.Left + cs.Width * c / showCols;
                dc.DrawLine(TileGridPen, new Point(gx, cs.Top), new Point(gx, cs.Bottom));
            }
        }
        if (cs.Height / showRows >= 4.0)
        {
            for (int r = 1; r < showRows; r++)
            {
                double gy = cs.Top + cs.Height * r / showRows;
                dc.DrawLine(TileGridPen, new Point(cs.Left, gy), new Point(cs.Right, gy));
            }
        }

        // Bright crop border
        dc.DrawRectangle(null, CropBorderPen, cs);

        // 8 resize handles (4 corners + 4 mid-edges)
        const double hs = 8;
        double mx = cs.Left + cs.Width * 0.5;
        double my = cs.Top + cs.Height * 0.5;
        DrawHandle(dc, cs.TopLeft, hs);
        DrawHandle(dc, new Point(mx, cs.Top), hs);
        DrawHandle(dc, cs.TopRight, hs);
        DrawHandle(dc, new Point(cs.Right, my), hs);
        DrawHandle(dc, cs.BottomRight, hs);
        DrawHandle(dc, new Point(mx, cs.Bottom), hs);
        DrawHandle(dc, cs.BottomLeft, hs);
        DrawHandle(dc, new Point(cs.Left, my), hs);

        // Compact floating badge showing live tile grid & pixel crop size
        var crop = CurrentCrop;
        string badgeText = $"{showCols}×{showRows}칸 ({showCols * 32}×{showRows * 32}px)  |  원본영역 {crop.Width}×{crop.Height}";
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(badgeText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            BadgeFont, 11, Brushes.White, dpi);
        double bx = Math.Clamp(cs.Left, ix, Math.Max(ix, _imgScreenRect.Right - ft.Width - 12));
        double by = cs.Top >= iy + 24 ? cs.Top - 22 : cs.Top + 4;
        dc.DrawRoundedRectangle(InfoBadgeBg, null, new Rect(bx, by, ft.Width + 10, ft.Height + 4), 3, 3);
        dc.DrawText(ft, new Point(bx + 5, by + 2));
    }

    private static void DrawHandle(DrawingContext dc, Point center, double size) =>
        dc.DrawRectangle(HandleFillBrush, HandleStrokePen,
            new Rect(center.X - size * 0.5, center.Y - size * 0.5, size, size));

    private Rect ImageToScreen(double x, double y, double w, double h)
    {
        double sx = _imgScreenRect.Width / _imgW;
        double sy = _imgScreenRect.Height / _imgH;
        return new Rect(_imgScreenRect.X + x * sx, _imgScreenRect.Y + y * sy, w * sx, h * sy);
    }

    private (double X, double Y) ScreenToImageClamped(Point pt)
    {
        double ix = Math.Clamp((pt.X - _imgScreenRect.X) * _imgW / _imgScreenRect.Width, 0, _imgW);
        double iy = Math.Clamp((pt.Y - _imgScreenRect.Y) * _imgH / _imgScreenRect.Height, 0, _imgH);
        return (ix, iy);
    }

    private DragMode HitTestHandle(Point pt)
    {
        if (_imgScreenRect.IsEmpty) return DragMode.None;
        Rect cs = ImageToScreen(_cx, _cy, _cw, _ch);
        double mx = cs.Left + cs.Width * 0.5;
        double my = cs.Top + cs.Height * 0.5;
        const double hitR = 10;

        if ((pt - cs.TopLeft).Length <= hitR) return DragMode.TopLeft;
        if ((pt - cs.TopRight).Length <= hitR) return DragMode.TopRight;
        if ((pt - cs.BottomRight).Length <= hitR) return DragMode.BottomRight;
        if ((pt - cs.BottomLeft).Length <= hitR) return DragMode.BottomLeft;
        if ((pt - new Point(mx, cs.Top)).Length <= hitR) return DragMode.Top;
        if ((pt - new Point(cs.Right, my)).Length <= hitR) return DragMode.Right;
        if ((pt - new Point(mx, cs.Bottom)).Length <= hitR) return DragMode.Bottom;
        if ((pt - new Point(cs.Left, my)).Length <= hitR) return DragMode.Left;
        if (cs.Contains(pt)) return DragMode.Move;
        if (_imgScreenRect.Contains(pt)) return DragMode.NewBox;
        return DragMode.None;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image is null || _imgScreenRect.IsEmpty) return;

        Point pt = e.GetPosition(this);
        if (e.ClickCount == 2 && ImageToScreen(_cx, _cy, _cw, _ch).Contains(pt))
        {
            ApplyRequested?.Invoke();
            e.Handled = true;
            return;
        }

        _dragMode = HitTestHandle(pt);
        if (_dragMode != DragMode.None)
        {
            _dragStartPt = pt;
            _origX = _cx; _origY = _cy; _origW = _cw; _origH = _ch;
            if (_dragMode == DragMode.NewBox)
                (_newStartImgX, _newStartImgY) = ScreenToImageClamped(pt);
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_image is null || _imgScreenRect.IsEmpty) return;
        Point pt = e.GetPosition(this);

        if (_dragMode == DragMode.None)
        {
            Cursor = HitTestHandle(pt) switch
            {
                DragMode.TopLeft or DragMode.BottomRight => Cursors.SizeNWSE,
                DragMode.TopRight or DragMode.BottomLeft => Cursors.SizeNESW,
                DragMode.Top or DragMode.Bottom => Cursors.SizeNS,
                DragMode.Left or DragMode.Right => Cursors.SizeWE,
                DragMode.Move => Cursors.SizeAll,
                DragMode.NewBox => Cursors.Cross,
                _ => Cursors.Arrow
            };
            return;
        }

        const double minSize = 16;
        double invScale = _imgW / _imgScreenRect.Width;
        double dx = (pt.X - _dragStartPt.X) * invScale;
        double dy = (pt.Y - _dragStartPt.Y) * invScale;

        if (_dragMode == DragMode.Move)
        {
            _cx = Math.Clamp(_origX + dx, 0, Math.Max(0, _imgW - _cw));
            _cy = Math.Clamp(_origY + dy, 0, Math.Max(0, _imgH - _ch));
        }
        else if (_dragMode == DragMode.NewBox)
        {
            var (curX, curY) = ScreenToImageClamped(pt);
            double w = Math.Max(minSize, Math.Abs(curX - _newStartImgX));
            double h = Math.Max(minSize, Math.Abs(curY - _newStartImgY));
            if (_lockAspect)
            {
                h = w / _targetAspect;
                if (h > _imgH) { h = _imgH; w = h * _targetAspect; }
            }
            double x = curX >= _newStartImgX ? _newStartImgX : _newStartImgX - w;
            double y = curY >= _newStartImgY ? _newStartImgY : _newStartImgY - h;
            _cw = Math.Min(_imgW, w);
            _ch = Math.Min(_imgH, h);
            _cx = Math.Clamp(x, 0, Math.Max(0, _imgW - _cw));
            _cy = Math.Clamp(y, 0, Math.Max(0, _imgH - _ch));
        }
        else
        {
            ResizeFromHandle(dx, dy, minSize);
        }

        NotifyCrop();
        InvalidateVisual();
    }

    private void ResizeFromHandle(double dx, double dy, double minSize)
    {
        double left = _origX;
        double right = _origX + _origW;
        double top = _origY;
        double bottom = _origY + _origH;

        if (_dragMode is DragMode.TopLeft or DragMode.Left or DragMode.BottomLeft)
            left = Math.Clamp(_origX + dx, 0, right - minSize);
        if (_dragMode is DragMode.TopRight or DragMode.Right or DragMode.BottomRight)
            right = Math.Clamp(_origX + _origW + dx, left + minSize, _imgW);
        if (_dragMode is DragMode.TopLeft or DragMode.Top or DragMode.TopRight)
            top = Math.Clamp(_origY + dy, 0, bottom - minSize);
        if (_dragMode is DragMode.BottomLeft or DragMode.Bottom or DragMode.BottomRight)
            bottom = Math.Clamp(_origY + _origH + dy, top + minSize, _imgH);

        double nw = right - left;
        double nh = bottom - top;

        if (_lockAspect)
        {
            if (_dragMode is DragMode.Top or DragMode.Bottom)
                nw = nh * _targetAspect;
            else
                nh = nw / _targetAspect;

            if (nw > _imgW) { nw = _imgW; nh = nw / _targetAspect; }
            if (nh > _imgH) { nh = _imgH; nw = nh * _targetAspect; }

            if (_dragMode is DragMode.TopLeft or DragMode.Left or DragMode.BottomLeft)
                left = Math.Max(0, right - nw);
            if (_dragMode is DragMode.TopLeft or DragMode.Top or DragMode.TopRight)
                top = Math.Max(0, bottom - nh);
        }

        _cw = Math.Clamp(nw, minSize, _imgW);
        _ch = Math.Clamp(nh, minSize, _imgH);
        _cx = Math.Clamp(left, 0, Math.Max(0, _imgW - _cw));
        _cy = Math.Clamp(top, 0, Math.Max(0, _imgH - _ch));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_image is null || _imgW <= 0 || _imgH <= 0) return;

        double factor = e.Delta > 0 ? 0.92 : 1.08;
        double centerX = _cx + _cw * 0.5;
        double centerY = _cy + _ch * 0.5;

        double nw = Math.Clamp(_cw * factor, 16, _imgW);
        double nh = _lockAspect ? nw / _targetAspect : Math.Clamp(_ch * factor, 16, _imgH);
        if (nh > _imgH) { nh = _imgH; if (_lockAspect) nw = nh * _targetAspect; }
        if (nw > _imgW) { nw = _imgW; if (_lockAspect) nh = nw / _targetAspect; }

        _cw = nw;
        _ch = nh;
        _cx = Math.Clamp(centerX - _cw * 0.5, 0, Math.Max(0, _imgW - _cw));
        _cy = Math.Clamp(centerY - _ch * 0.5, 0, Math.Max(0, _imgH - _ch));

        NotifyCrop();
        InvalidateVisual();
        e.Handled = true;
    }
}
