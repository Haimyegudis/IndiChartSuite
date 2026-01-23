using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System.Text;

namespace IndiChart.UI
{
    public partial class GraphView : UserControl
    {
        public event Action<int, int> OnViewRangeChanged;
        public event Action<int> OnCursorMoved;
        public event Action OnChartClicked;
        public Func<int, string> GetXAxisLabel { get; set; }

        private bool _isSyncing = false;
        private bool _showStates = true;
        private bool _isProgressiveMode = false;
        public bool IsProgressiveMode { get => _isProgressiveMode; set { _isProgressiveMode = value; SkiaCanvas.InvalidateVisual(); } }

        // Paints
        private SKPaint _gridLinePaint = new SKPaint { Color = SKColors.LightGray.WithAlpha(128), IsAntialias = false, StrokeWidth = 1 };
        private SKPaint _axisLinePaint = new SKPaint { Color = SKColors.Gray, IsAntialias = false, StrokeWidth = 1 };
        private SKPaint _textPaintLeft = new SKPaint { Color = SKColors.DarkSlateGray, TextSize = 11, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) };
        private SKPaint _textPaintRight = new SKPaint { Color = SKColors.Navy, TextSize = 11, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) };
        private SKPaint _stateTextPaint = new SKPaint { Color = SKColors.Black, TextSize = 12, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) };
        private SKPaint _stateFillPaint = new SKPaint { Style = SKPaintStyle.Fill };
        private SKPaint _targetLinePaint = new SKPaint { Color = SKColors.Blue, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = false };
        private SKPaint _measureFillPaint = new SKPaint { Color = SKColors.DodgerBlue.WithAlpha(40), Style = SKPaintStyle.Fill };
        private SKPaint _measureBorderPaint = new SKPaint { Color = SKColors.DodgerBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) };

        private List<SignalSeries> _seriesList = new List<SignalSeries>();
        private List<StateInterval> _states;

        private int _viewStartIndex = 0;
        private int _viewEndIndex = 0;
        private int _totalDataLength = 0;
        private int _globalCursorIndex = -1;
        private int _targetLineIndex = -1;

        private const float LEFT_MARGIN = 50;
        private const float RIGHT_MARGIN = 50;
        private const float TOP_MARGIN = 20;
        private const float BOTTOM_MARGIN = 20;

        private bool _isDragging = false;
        private bool _isMeasuring = false;
        private Point _lastMousePos;
        private int _measureStartIndex = -1;
        private int _measureCurrentIndex = -1;

        public GraphView() { InitializeComponent(); }
        private float SnapToPixel(float coord) => (float)Math.Floor(coord) + 0.5f;

        public void SetViewModel(ChartViewModel vm)
        {
            if (vm == null) return;
            _seriesList = vm.Series.ToList();
            _states = vm.States;
            _totalDataLength = _seriesList.Any() ? _seriesList.Max(s => s.Data != null ? s.Data.Length : 0) : 0;
            if (_viewEndIndex == 0 && _totalDataLength > 0) { _viewStartIndex = 0; _viewEndIndex = _totalDataLength - 1; }
            SkiaCanvas.InvalidateVisual();
        }

        public void SetShowStates(bool show) { _showStates = show; SkiaCanvas.InvalidateVisual(); }
        public void SetTargetLine(int index) { _targetLineIndex = index; SkiaCanvas.InvalidateVisual(); }

        public void SyncViewRange(int start, int end)
        {
            if (_totalDataLength == 0 || _isSyncing) return;
            _isSyncing = true; _viewStartIndex = Math.Clamp(start, 0, _totalDataLength - 1); _viewEndIndex = Math.Clamp(end, 0, _totalDataLength - 1);
            SkiaCanvas.InvalidateVisual(); _isSyncing = false;
        }
        public void SyncCursor(int index) { _globalCursorIndex = index; UpdateLegendValues(index); SkiaCanvas.InvalidateVisual(); }

        private void UpdateLegendValues(int index)
        {
            if (index < 0 || index >= _totalDataLength) return;
            foreach (var s in _seriesList)
            {
                if (s.Data != null && index < s.Data.Length) { double val = s.Data[index]; s.CurrentValueDisplay = double.IsNaN(val) ? "NaN" : val.ToString("F2"); }
                else s.CurrentValueDisplay = "-";
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); OnChartClicked?.Invoke(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (_totalDataLength == 0) return; int totalPoints = _viewEndIndex - _viewStartIndex; if (totalPoints < 10) return;
            double zoomFactor = e.Delta > 0 ? 0.9 : 1.1; double chartWidth = ActualWidth - LEFT_MARGIN - RIGHT_MARGIN; double mouseX = e.GetPosition(this).X - LEFT_MARGIN;
            double mouseRatio = Math.Clamp(mouseX / chartWidth, 0, 1); int mouseIndex = _viewStartIndex + (int)(totalPoints * mouseRatio);
            int newSpan = (int)(totalPoints * zoomFactor); int newStart = mouseIndex - (int)(newSpan * mouseRatio); int newEnd = newStart + newSpan;
            if (newStart < 0) { newStart = 0; newEnd = newSpan; }
            if (newEnd >= _totalDataLength) { newEnd = _totalDataLength - 1; newStart = newEnd - newSpan; }
            if (newEnd - newStart > 10) { _viewStartIndex = newStart; _viewEndIndex = newEnd; SkiaCanvas.InvalidateVisual(); if (!_isSyncing) OnViewRangeChanged?.Invoke(_viewStartIndex, _viewEndIndex); }
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); var pos = e.GetPosition(this); if (Keyboard.Modifiers == ModifierKeys.Shift) { _isMeasuring = true; _measureStartIndex = PixelToIndex(pos.X); _measureCurrentIndex = _measureStartIndex; } else { _isDragging = true; _lastMousePos = pos; } CaptureMouse(); SkiaCanvas.InvalidateVisual(); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); _isDragging = false; _isMeasuring = false; if (Keyboard.Modifiers != ModifierKeys.Shift && Math.Abs(_measureStartIndex - _measureCurrentIndex) < 5) _measureStartIndex = -1; ReleaseMouseCapture(); SkiaCanvas.InvalidateVisual(); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_totalDataLength == 0) return; var currentPos = e.GetPosition(this); int cursorIdx = PixelToIndex(currentPos.X); if (cursorIdx != _globalCursorIndex) { _globalCursorIndex = cursorIdx; OnCursorMoved?.Invoke(cursorIdx); UpdateLegendValues(cursorIdx); SkiaCanvas.InvalidateVisual(); } if (_isMeasuring) { _measureCurrentIndex = cursorIdx; SkiaCanvas.InvalidateVisual(); } else if (_isDragging) { double deltaX = currentPos.X - _lastMousePos.X; double chartWidth = ActualWidth - LEFT_MARGIN - RIGHT_MARGIN; int visiblePoints = _viewEndIndex - _viewStartIndex; int shift = (int)((deltaX / chartWidth) * visiblePoints); int newStart = _viewStartIndex - shift; int newEnd = _viewEndIndex - shift; if (newStart < 0) { newStart = 0; newEnd = visiblePoints; } if (newEnd >= _totalDataLength) { newEnd = _totalDataLength - 1; newStart = newEnd - visiblePoints; } if (newStart != _viewStartIndex) { _viewStartIndex = newStart; _viewEndIndex = newEnd; _lastMousePos = currentPos; SkiaCanvas.InvalidateVisual(); if (!_isSyncing) OnViewRangeChanged?.Invoke(_viewStartIndex, _viewEndIndex); } } }
        private int PixelToIndex(double x) { double chartWidth = ActualWidth - LEFT_MARGIN - RIGHT_MARGIN; if (chartWidth <= 0 || _totalDataLength == 0) return 0; double relX = x - LEFT_MARGIN; int count = _viewEndIndex - _viewStartIndex; int offset = (int)((relX / chartWidth) * count); return Math.Clamp(_viewStartIndex + offset, 0, _totalDataLength - 1); }

        private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas; var info = e.Info; canvas.Clear(SKColors.White);
            if (_totalDataLength == 0) return;

            float w = info.Width; float h = info.Height;
            float chartLeft = LEFT_MARGIN; float chartRight = w - RIGHT_MARGIN;
            float chartTop = TOP_MARGIN; float chartBottom = h - BOTTOM_MARGIN;
            float chartW = chartRight - chartLeft; float chartH = chartBottom - chartTop;

            int start = Math.Max(0, _viewStartIndex); int end = Math.Min(_totalDataLength - 1, _viewEndIndex);
            int count = end - start + 1;
            if (count <= 1 || chartW <= 0) return;

            // Background States
            if (_showStates && _states != null)
            {
                foreach (var st in _states)
                {
                    if (st.EndIndex < start || st.StartIndex > end) continue;
                    float x1 = (float)((Math.Max(st.StartIndex, start) - start) / (double)count * chartW);
                    float x2 = (float)((Math.Min(st.EndIndex, end) - start) / (double)count * chartW);
                    canvas.DrawRect(new SKRect(chartLeft + x1, chartTop, chartLeft + x2, chartBottom), _stateFillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = StateConfig.GetColor(st.StateId) });
                    if (x2 - x1 > 35) { string nm = StateConfig.GetName(st.StateId); float tw = _stateTextPaint.MeasureText(nm); if (tw < (x2 - x1) + 10) canvas.DrawText(nm, (float)Math.Round(chartLeft + x1 + (x2 - x1) / 2 - tw / 2), (float)Math.Round(chartTop + 14), _stateTextPaint); }
                }
            }

            // Dual Scale Logic
            double lMin = double.MaxValue, lMax = double.MinValue;
            double rMin = double.MaxValue, rMax = double.MinValue;
            bool hasLeft = false, hasRight = false;
            int step = Math.Max(1, count / 1000);

            foreach (var s in _seriesList)
            {
                if (s.Data == null || !s.IsVisible) continue;
                ref double min = ref lMin; ref double max = ref lMax; ref bool has = ref hasLeft;
                if (s.YAxisType == AxisType.Right) { min = ref rMin; max = ref rMax; has = ref hasRight; }
                for (int i = start; i <= end; i += step) { if (i < s.Data.Length && !double.IsNaN(s.Data[i])) { if (s.Data[i] < min) min = s.Data[i]; if (s.Data[i] > max) max = s.Data[i]; has = true; } }
            }

            if (!hasLeft) { lMin = 0; lMax = 10; }
            if (!hasRight) { rMin = 0; rMax = 10; }
            if (Math.Abs(lMax - lMin) < 0.0001) { lMax += 1; lMin -= 1; }
            if (Math.Abs(rMax - rMin) < 0.0001) { rMax += 1; rMin -= 1; }

            double lRange = (lMax - lMin) * 1.2; double lDisplayMin = lMin - (lRange * 0.1);
            double rRange = (rMax - rMin) * 1.2; double rDisplayMin = rMin - (rRange * 0.1);

            // Grid Y
            int ySteps = 4;
            for (int i = 0; i <= ySteps; i++)
            {
                double ratio = i / (double)ySteps;
                float yPos = SnapToPixel(chartBottom - (float)(ratio * chartH));
                canvas.DrawLine(chartLeft, yPos, chartRight, yPos, _gridLinePaint);
                if (hasLeft || !hasRight)
                {
                    string lbl = (lDisplayMin + (ratio * lRange)).ToString("0.##");
                    float lblW = _textPaintLeft.MeasureText(lbl);
                    canvas.DrawText(lbl, chartLeft - lblW - 6, yPos + 4, _textPaintLeft);
                }
                if (hasRight)
                {
                    string lbl = (rDisplayMin + (ratio * rRange)).ToString("0.##");
                    canvas.DrawText(lbl, chartRight + 6, yPos + 4, _textPaintRight);
                }
            }

            // Grid X
            float stepPixels = 120; int xSteps = (int)(chartW / stepPixels); float lastTextRight = -1000;
            for (int i = 0; i <= xSteps; i++)
            {
                float xPos = SnapToPixel(chartLeft + (i * stepPixels));
                if (xPos > chartRight) break;
                double ratio = (xPos - chartLeft) / chartW; int idx = start + (int)(count * ratio);
                canvas.DrawLine(xPos, chartTop, xPos, chartBottom, _gridLinePaint);
                if (GetXAxisLabel != null)
                {
                    string t = GetXAxisLabel(idx);
                    if (!string.IsNullOrEmpty(t))
                    {
                        float txtW = _textPaintLeft.MeasureText(t); float tl = (float)Math.Round(xPos - txtW / 2);
                        if (tl > lastTextRight + 20) { canvas.DrawText(t, tl, chartBottom + 16, _textPaintLeft); lastTextRight = tl + txtW; }
                    }
                }
            }

            // Lines
            using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true })
            using (var path = new SKPath())
            {
                canvas.Save(); canvas.ClipRect(new SKRect(chartLeft, chartTop, chartRight, chartBottom));

                int drawLimit = end;
                if (_isProgressiveMode && _globalCursorIndex != -1) drawLimit = Math.Min(end, _globalCursorIndex);

                foreach (var s in _seriesList)
                {
                    if (!s.IsVisible) continue;
                    paint.Color = s.Color; path.Reset(); bool first = true;
                    double currentMin = (s.YAxisType == AxisType.Right) ? rDisplayMin : lDisplayMin;
                    double currentRange = (s.YAxisType == AxisType.Right) ? rRange : lRange;
                    int drawStep = Math.Max(1, count / (int)chartW);
                    for (int i = start; i <= drawLimit; i += drawStep)
                    {
                        if (i >= s.Data.Length) break; double val = s.Data[i]; if (double.IsNaN(val)) { first = true; continue; }
                        float x = chartLeft + (float)((i - start) / (double)count * chartW);
                        float y = chartBottom - (float)((val - currentMin) / currentRange * chartH);
                        if (first) { path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
                    }
                    if (!first) canvas.DrawPath(path, paint);
                }
                canvas.Restore();
            }

            // Border
            float L = SnapToPixel(chartLeft), R = SnapToPixel(chartRight), B = SnapToPixel(chartBottom), T = SnapToPixel(chartTop);
            canvas.DrawLine(L, T, L, B, _axisLinePaint); if (hasRight) canvas.DrawLine(R, T, R, B, _axisLinePaint); canvas.DrawLine(L, B, R, B, _axisLinePaint);

            // Target Line (Blue)
            if (_targetLineIndex >= start && _targetLineIndex <= end) { float tx = SnapToPixel(chartLeft + (float)((_targetLineIndex - start) / (double)count * chartW)); canvas.DrawLine(tx, T, tx, B, _targetLinePaint); }

            // Measure
            if (_measureStartIndex != -1 && _measureCurrentIndex != -1)
            {
                int mS = Math.Max(Math.Min(_measureStartIndex, _measureCurrentIndex), start); int mE = Math.Min(Math.Max(_measureStartIndex, _measureCurrentIndex), end);
                if (mE > mS)
                {
                    float x1 = chartLeft + (float)((mS - start) / (double)count * chartW); float x2 = chartLeft + (float)((mE - start) / (double)count * chartW);
                    var rect = new SKRect(x1, chartTop, x2, chartBottom); canvas.DrawRect(rect, _measureFillPaint); canvas.DrawRect(rect, _measureBorderPaint);
                    StringBuilder sb = new StringBuilder(); sb.AppendLine($"Range: {mE - mS}");
                    foreach (var s in _seriesList) { if (!s.IsVisible) continue; double sum = 0, mn = double.MaxValue, mx = double.MinValue; int c = 0; for (int i = mS; i <= mE; i++) { if (i < s.Data.Length && !double.IsNaN(s.Data[i])) { double v = s.Data[i]; sum += v; if (v < mn) mn = v; if (v > mx) mx = v; c++; } } if (c > 0) sb.AppendLine($"{s.Name}: Δ{(mx - mn):F2}"); }
                    DrawTooltip(canvas, sb.ToString(), x2 + 10, 50);
                }
            }
        }

        private void DrawTooltip(SKCanvas c, string t, float x, float y)
        {
            var ls = t.Split('\n'); float boxW = 150; float h = ls.Length * 16 + 10;
            if (x + boxW > c.LocalClipBounds.Width) x -= (boxW + 20);
            using (var p = new SKPaint { Color = SKColors.White.WithAlpha(230), Style = SKPaintStyle.Fill })
            using (var b = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke })
            {
                c.DrawRect(new SKRect(x, y, x + boxW, y + h), p); c.DrawRect(new SKRect(x, y, x + boxW, y + h), b);
            }
            float ty = y + 14; foreach (var l in ls) { c.DrawText(l, x + 5, ty, _textPaintLeft); ty += 16; }
        }
    }
}