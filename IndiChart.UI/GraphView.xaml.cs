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
using System.Collections.ObjectModel;

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

        // משתנה שליטה במצב ניגון
        private bool _isProgressiveMode = false;
        public bool IsProgressiveMode
        {
            get => _isProgressiveMode;
            set
            {
                _isProgressiveMode = value;
                SkiaCanvas.InvalidateVisual();
            }
        }

        // Paints
        private SKPaint _gridLinePaint = new SKPaint { Color = SKColors.LightGray.WithAlpha(128), IsAntialias = false, StrokeWidth = 1 };
        private SKPaint _axisLinePaint = new SKPaint { Color = SKColors.Gray, IsAntialias = false, StrokeWidth = 1 };
        private SKPaint _textPaintLeft = new SKPaint { Color = SKColors.DarkSlateGray, TextSize = 11, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) };
        private SKPaint _textPaintRight = new SKPaint { Color = SKColors.Navy, TextSize = 11, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) };
        private SKPaint _stateTextPaint = new SKPaint { Color = SKColors.Black, TextSize = 12, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) };
        private SKPaint _stateFillPaint = new SKPaint { Style = SKPaintStyle.Fill };

        private SKPaint _targetLinePaint = new SKPaint { Color = SKColors.Blue, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = false };
        private SKPaint _cursorLinePaint = new SKPaint { Color = SKColors.Red, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = false };

        private SKPaint _measureFillPaint = new SKPaint { Color = SKColors.DodgerBlue.WithAlpha(40), Style = SKPaintStyle.Fill };
        private SKPaint _measureBorderPaint = new SKPaint { Color = SKColors.DodgerBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) };

        private List<SignalSeries> _seriesList = new List<SignalSeries>();
        private ObservableCollection<ReferenceLine> _referenceLines; // רשימה לציור
        private List<StateInterval> _states;

        private int _viewStartIndex = 0;
        private int _viewEndIndex = 0;
        private int _totalDataLength = 0;
        private int _globalCursorIndex = -1;
        private int _targetLineIndex = -1;

        private const float LEFT_MARGIN = 60;
        private const float RIGHT_MARGIN = 55;
        private const float TOP_MARGIN = 20;
        private const float BOTTOM_MARGIN = 20;

        private bool _isDragging = false;
        private bool _isMeasuring = false;
        private Point _lastMousePos;
        private int _measureStartIndex = -1;
        private int _measureCurrentIndex = -1;

        // Ctrl + Click measurement (2-point measurement with X and Y)
        private bool _isCtrlMeasuring = false;
        private int _ctrlPoint1 = -1;
        private int _ctrlPoint2 = -1;
        private Point _ctrlPoint1Pos;
        private Point _ctrlPoint2Pos;

        private bool _showHoverTooltip = false;
        private Point _hoverPos;

        public GraphView() { InitializeComponent(); }
        private float SnapToPixel(float coord) => (float)Math.Floor(coord) + 0.5f;

        public void SetViewModel(ChartViewModel vm)
        {
            if (vm == null) return;
            _seriesList = vm.Series.ToList();
            _referenceLines = vm.ReferenceLines; // חיבור לרשימה
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

        public void SyncCursor(int index)
        {
            _globalCursorIndex = index;
            UpdateLegendValues(index);
            SkiaCanvas.InvalidateVisual();
        }

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
            if (_totalDataLength == 0) return;
            int totalPoints = _viewEndIndex - _viewStartIndex;
            if (totalPoints < 10) return;

            // Fixed zoom factor for stability
            double zoomFactor = e.Delta > 0 ? 0.85 : 1.15;

            double chartWidth = ActualWidth - LEFT_MARGIN - RIGHT_MARGIN;
            double mouseX = e.GetPosition(this).X - LEFT_MARGIN;
            double mouseRatio = Math.Clamp(mouseX / chartWidth, 0, 1);

            int mouseIndex = _viewStartIndex + (int)(totalPoints * mouseRatio);
            int newSpan = Math.Max(10, (int)(totalPoints * zoomFactor));

            // Calculate new range centered on mouse position
            int newStart = mouseIndex - (int)(newSpan * mouseRatio);
            int newEnd = newStart + newSpan;

            // Clamp to valid range
            if (newStart < 0)
            {
                newStart = 0;
                newEnd = Math.Min(newSpan, _totalDataLength - 1);
            }
            if (newEnd >= _totalDataLength)
            {
                newEnd = _totalDataLength - 1;
                newStart = Math.Max(0, newEnd - newSpan);
            }

            // Only update if range is valid and changed
            if (newEnd > newStart && newEnd - newStart >= 10 && (newStart != _viewStartIndex || newEnd != _viewEndIndex))
            {
                _viewStartIndex = newStart;
                _viewEndIndex = newEnd;
                SkiaCanvas.InvalidateVisual();
                if (!_isSyncing) OnViewRangeChanged?.Invoke(_viewStartIndex, _viewEndIndex);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var pos = e.GetPosition(this);

            // Ctrl+Click for 2-point measurement (supports different X and Y positions)
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!_isCtrlMeasuring)
                {
                    // First click - set point 1 with X,Y position
                    _ctrlPoint1 = PixelToIndex(pos.X);
                    _ctrlPoint1Pos = pos;
                    _ctrlPoint2 = -1;
                    _isCtrlMeasuring = true;
                }
                else
                {
                    // Second click - set point 2 with X,Y position
                    _ctrlPoint2 = PixelToIndex(pos.X);
                    _ctrlPoint2Pos = pos;
                    _isCtrlMeasuring = false; // Measurement complete
                }
                SkiaCanvas.InvalidateVisual();
                return;
            }

            // Shift+Click for area measurement
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _isMeasuring = true;
                _measureStartIndex = PixelToIndex(pos.X);
                _measureCurrentIndex = _measureStartIndex;
                CaptureMouse();
            }
            else
            {
                _isDragging = true;
                _lastMousePos = pos;
                CaptureMouse();
            }
            SkiaCanvas.InvalidateVisual();
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _isDragging = false;

            // Keep measurement visible unless it's too small
            if (_isMeasuring && Math.Abs(_measureStartIndex - _measureCurrentIndex) < 5)
            {
                _measureStartIndex = -1;
            }
            _isMeasuring = false;

            ReleaseMouseCapture();
            SkiaCanvas.InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_totalDataLength == 0) return;

            var currentPos = e.GetPosition(this);

            // Enable hover tooltip only when Alt key is held
            _showHoverTooltip = Keyboard.Modifiers == ModifierKeys.Alt &&
                               currentPos.X >= LEFT_MARGIN &&
                               currentPos.X <= ActualWidth - RIGHT_MARGIN;
            _hoverPos = currentPos;

            // --- Fix mouse logic during playback ---
            // If in Progressive Mode (playback), ignore mouse movement to prevent red line from jumping
            if (_isProgressiveMode) return;

            int cursorIdx = PixelToIndex(currentPos.X);

            if (!_isProgressiveMode && cursorIdx != _globalCursorIndex)
            {
                _globalCursorIndex = cursorIdx;
                OnCursorMoved?.Invoke(cursorIdx);
                UpdateLegendValues(cursorIdx);
                SkiaCanvas.InvalidateVisual();
            }

            if (_isMeasuring)
            {
                _measureCurrentIndex = cursorIdx;
                SkiaCanvas.InvalidateVisual();
            }
            else if (_isDragging)
            {
                double deltaX = currentPos.X - _lastMousePos.X; double chartWidth = ActualWidth - LEFT_MARGIN - RIGHT_MARGIN; int visiblePoints = _viewEndIndex - _viewStartIndex; int shift = (int)((deltaX / chartWidth) * visiblePoints); int newStart = _viewStartIndex - shift; int newEnd = _viewEndIndex - shift; if (newStart < 0) { newStart = 0; newEnd = visiblePoints; }
                if (newEnd >= _totalDataLength) { newEnd = _totalDataLength - 1; newStart = newEnd - visiblePoints; }
                if (newStart != _viewStartIndex) { _viewStartIndex = newStart; _viewEndIndex = newEnd; _lastMousePos = currentPos; SkiaCanvas.InvalidateVisual(); if (!_isSyncing) OnViewRangeChanged?.Invoke(_viewStartIndex, _viewEndIndex); }
            }
            else if (_showHoverTooltip)
            {
                SkiaCanvas.InvalidateVisual(); // Refresh to show tooltip
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Escape key to clear measurements
            if (e.Key == Key.Escape)
            {
                ClearAllMeasurements();
            }
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            // Right-click clears all measurements
            ClearAllMeasurements();
        }

        private void ClearAllMeasurements()
        {
            // Clear Ctrl measurement
            _ctrlPoint1 = -1;
            _ctrlPoint2 = -1;
            _isCtrlMeasuring = false;

            // Clear Shift measurement
            _measureStartIndex = -1;
            _measureCurrentIndex = -1;
            _isMeasuring = false;

            SkiaCanvas.InvalidateVisual();
        }

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

                    // Always show state label if there's enough space for the text
                    string nm = StateConfig.GetName(st.StateId);
                    float tw = _stateTextPaint.MeasureText(nm);
                    if (tw < (x2 - x1) - 4) // Only 4px margin needed
                    {
                        canvas.DrawText(nm, (float)Math.Round(chartLeft + x1 + (x2 - x1) / 2 - tw / 2), (float)Math.Round(chartTop + 14), _stateTextPaint);
                    }
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

            // Add 10% padding on top and bottom for better visualization
            double lPadding = (lMax - lMin) * 0.1;
            double lDisplayMin = lMin - lPadding;
            double lRange = (lMax - lMin) + (2 * lPadding);

            double rPadding = (rMax - rMin) * 0.1;
            double rDisplayMin = rMin - rPadding;
            double rRange = (rMax - rMin) + (2 * rPadding);

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

            // --- מימוש ציור קווי ייחוס (Reference Lines) ---
            if (_referenceLines != null)
            {
                foreach (var line in _referenceLines)
                {
                    using (var paint = new SKPaint
                    {
                        Color = line.Color,
                        StrokeWidth = line.Thickness,
                        Style = SKPaintStyle.Stroke,
                        IsAntialias = true,
                        // תמיכה בקו מקווקו
                        PathEffect = line.IsDashed ? SKPathEffect.CreateDash(new float[] { 10, 5 }, 0) : null
                    })
                    {
                        if (line.Type == ReferenceLineType.Vertical)
                        {
                            // קו אנכי (לפי אינדקס זמן)
                            int idx = (int)line.Value;
                            if (idx >= start && idx <= end)
                            {
                                float x = chartLeft + (float)((idx - start) / (double)count * chartW);
                                canvas.DrawLine(x, chartTop, x, chartBottom, paint);

                                // שם הקו
                                if (!string.IsNullOrEmpty(line.Name))
                                {
                                    using (var tp = new SKPaint { TextSize = 11, Color = line.Color, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) })
                                    {
                                        canvas.DrawText(line.Name, x + 4, chartTop + 12, tp);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // קו אופקי (לפי ערך Y)
                            double range = (line.YAxis == AxisType.Left) ? lRange : rRange;
                            double dMin = (line.YAxis == AxisType.Left) ? lDisplayMin : rDisplayMin;

                            // בדיקה אם הערך בטווח הציר הרלוונטי
                            if (line.Value >= dMin && line.Value <= (dMin + range))
                            {
                                float y = chartBottom - (float)((line.Value - dMin) / range * chartH);
                                canvas.DrawLine(chartLeft, y, chartRight, y, paint);

                                // שם הקו
                                if (!string.IsNullOrEmpty(line.Name))
                                {
                                    using (var tp = new SKPaint { TextSize = 11, Color = line.Color, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) })
                                    {
                                        canvas.DrawText(line.Name, chartLeft + 4, y - 4, tp);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Signal Lines
            using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true })
            using (var path = new SKPath())
            {
                canvas.Save(); canvas.ClipRect(new SKRect(chartLeft, chartTop, chartRight, chartBottom));

                int drawLimit = end;
                // בקרת ציור פרוגרסיבי (ציור עד הקו האדום)
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

            // Target Line (Blue) - תוצאת חיפוש
            if (_targetLineIndex >= start && _targetLineIndex <= end)
            {
                float tx = SnapToPixel(chartLeft + (float)((_targetLineIndex - start) / (double)count * chartW));
                canvas.DrawLine(tx, T, tx, B, _targetLinePaint);
            }

            // Cursor Line (Red)
            if (_globalCursorIndex >= start && _globalCursorIndex <= end)
            {
                float cx = SnapToPixel(chartLeft + (float)((_globalCursorIndex - start) / (double)count * chartW));
                canvas.DrawLine(cx, T, cx, B, _cursorLinePaint);
            }

            // Measure Box - Enhanced with Average, Min, Max, Time Delta (Shift+Drag)
            if (_measureStartIndex != -1 && _measureCurrentIndex != -1)
            {
                int mS = Math.Max(Math.Min(_measureStartIndex, _measureCurrentIndex), start);
                int mE = Math.Min(Math.Max(_measureStartIndex, _measureCurrentIndex), end);
                if (mE > mS)
                {
                    float x1 = chartLeft + (float)((mS - start) / (double)count * chartW);
                    float x2 = chartLeft + (float)((mE - start) / (double)count * chartW);
                    var rect = new SKRect(x1, chartTop, x2, chartBottom);
                    canvas.DrawRect(rect, _measureFillPaint);
                    canvas.DrawRect(rect, _measureBorderPaint);

                    // Only show tooltip when measurement is complete (not during drag)
                    if (!_isMeasuring)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("=== AREA MEASUREMENT ===");
                        sb.AppendLine($"Index Range: {mS} -> {mE}");
                        sb.AppendLine($"Points: {mE - mS + 1}");

                        // Time labels if available
                        if (GetXAxisLabel != null)
                        {
                            string t1 = GetXAxisLabel(mS);
                            string t2 = GetXAxisLabel(mE);
                            if (!string.IsNullOrEmpty(t1) && !string.IsNullOrEmpty(t2))
                            {
                                sb.AppendLine($"Time: {t1} -> {t2}");
                            }
                        }

                        sb.AppendLine("-------------------");

                        foreach (var s in _seriesList)
                        {
                            if (!s.IsVisible || s.Data == null) continue;
                            double sum = 0, mn = double.MaxValue, mx = double.MinValue;
                            int c = 0;
                            for (int i = mS; i <= mE; i++)
                            {
                                if (i < s.Data.Length && !double.IsNaN(s.Data[i]))
                                {
                                    double v = s.Data[i];
                                    sum += v;
                                    if (v < mn) mn = v;
                                    if (v > mx) mx = v;
                                    c++;
                                }
                            }
                            if (c > 0)
                            {
                                double avg = sum / c;
                                sb.AppendLine($"{s.Name}:");
                                sb.AppendLine($"  Avg: {avg:F3}");
                                sb.AppendLine($"  Min: {mn:F3}");
                                sb.AppendLine($"  Max: {mx:F3}");
                                sb.AppendLine($"  Delta: {(mx - mn):F3}");
                            }
                        }
                        // Position tooltip next to the last drag point (end of selection)
                        // If dragging left to right, place tooltip to the right of x2
                        // If dragging right to left, place tooltip to the left of x1
                        float tooltipX = (_measureCurrentIndex > _measureStartIndex) ? x2 + 15 : x1 - 170;
                        float tooltipY = chartTop + 10;
                        DrawTooltip(canvas, sb.ToString(), tooltipX, tooltipY);
                    }
                }
            }

            // Ctrl+Click 2-Point Measurement (supports different X and Y positions)
            if (_ctrlPoint1 != -1)
            {
                // Draw first point marker
                if (_ctrlPoint1 >= start && _ctrlPoint1 <= end)
                {
                    float x1 = chartLeft + (float)((_ctrlPoint1 - start) / (double)count * chartW);
                    float y1 = (float)_ctrlPoint1Pos.Y;

                    // Draw crosshair at first point
                    using (var paint = new SKPaint { Color = SKColors.Green, StrokeWidth = 2, Style = SKPaintStyle.Stroke })
                    {
                        canvas.DrawLine(x1, chartTop, x1, chartBottom, paint);
                        canvas.DrawLine(chartLeft, y1, chartRight, y1, paint);
                    }

                    // Draw circle at intersection
                    using (var paint = new SKPaint { Color = SKColors.Green, StrokeWidth = 2, Style = SKPaintStyle.Stroke })
                    {
                        canvas.DrawCircle(x1, y1, 5, paint);
                    }

                    // If second point is set, draw complete measurement
                    if (_ctrlPoint2 != -1 && _ctrlPoint2 >= start && _ctrlPoint2 <= end)
                    {
                        float x2 = chartLeft + (float)((_ctrlPoint2 - start) / (double)count * chartW);
                        float y2 = (float)_ctrlPoint2Pos.Y;

                        // Draw crosshair at second point
                        using (var paint = new SKPaint { Color = SKColors.Green, StrokeWidth = 2, Style = SKPaintStyle.Stroke })
                        {
                            canvas.DrawLine(x2, chartTop, x2, chartBottom, paint);
                            canvas.DrawLine(chartLeft, y2, chartRight, y2, paint);
                            canvas.DrawCircle(x2, y2, 5, paint);
                        }

                        // Draw diagonal connecting line between the two points
                        using (var paint = new SKPaint { Color = SKColors.Green, StrokeWidth = 2, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) })
                        {
                            canvas.DrawLine(x1, y1, x2, y2, paint);
                        }

                        // Only show tooltip when measurement is complete (not during active Ctrl+Click)
                        if (!_isCtrlMeasuring)
                        {
                            // Calculate distance in pixels and graph units
                            double pixelDistanceX = Math.Abs(x2 - x1);
                            double pixelDistanceY = Math.Abs(y2 - y1);
                            double pixelDistanceDiagonal = Math.Sqrt(pixelDistanceX * pixelDistanceX + pixelDistanceY * pixelDistanceY);

                            // Calculate statistics
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine("=== 2-POINT MEASUREMENT ===");
                            sb.AppendLine($"Point 1 Index: {_ctrlPoint1}");
                            sb.AppendLine($"Point 2 Index: {_ctrlPoint2}");
                            sb.AppendLine($"X Distance: {Math.Abs(_ctrlPoint2 - _ctrlPoint1)} points");
                            sb.AppendLine($"Pixel Distance: {pixelDistanceDiagonal:F1} px");

                            // Time labels if available
                            if (GetXAxisLabel != null)
                            {
                                string t1 = GetXAxisLabel(_ctrlPoint1);
                                string t2 = GetXAxisLabel(_ctrlPoint2);
                                if (!string.IsNullOrEmpty(t1) && !string.IsNullOrEmpty(t2))
                                {
                                    sb.AppendLine($"Time 1: {t1}");
                                    sb.AppendLine($"Time 2: {t2}");
                                }
                            }

                            sb.AppendLine("-------------------");

                            // Calculate stats for each series at both points
                            foreach (var s in _seriesList)
                            {
                                if (!s.IsVisible || s.Data == null) continue;

                                if (_ctrlPoint1 < s.Data.Length && _ctrlPoint2 < s.Data.Length)
                                {
                                    double v1 = s.Data[_ctrlPoint1];
                                    double v2 = s.Data[_ctrlPoint2];

                                    if (!double.IsNaN(v1) && !double.IsNaN(v2))
                                    {
                                        double delta = v2 - v1;
                                        double avg = (v1 + v2) / 2.0;
                                        double min = Math.Min(v1, v2);
                                        double max = Math.Max(v1, v2);

                                        sb.AppendLine($"{s.Name}:");
                                        sb.AppendLine($"  P1: {v1:F3}");
                                        sb.AppendLine($"  P2: {v2:F3}");
                                        sb.AppendLine($"  Delta: {delta:F3}");
                                        sb.AppendLine($"  Avg: {avg:F3}");
                                        sb.AppendLine($"  Min: {min:F3}");
                                        sb.AppendLine($"  Max: {max:F3}");
                                    }
                                }
                            }

                            // Position tooltip next to the second (last) clicked point
                            // If second point is to the right of first, place tooltip to the right
                            // If second point is to the left of first, place tooltip to the left
                            float tooltipX = (_ctrlPoint2 > _ctrlPoint1) ? x2 + 15 : x2 - 170;
                            float tooltipY = y2;
                            DrawTooltip(canvas, sb.ToString(), tooltipX, tooltipY);
                        }
                    }
                }
            }

            // Hover Tooltip - shows exact values at cursor position (activated with Ctrl key)
            if (_showHoverTooltip && _hoverPos.X >= chartLeft && _hoverPos.X <= chartRight)
            {
                int hoverIdx = PixelToIndex(_hoverPos.X);
                if (hoverIdx >= start && hoverIdx <= end)
                {
                    StringBuilder tooltipText = new StringBuilder();
                    tooltipText.AppendLine($"Index: {hoverIdx}");
                    if (GetXAxisLabel != null)
                    {
                        string timeLabel = GetXAxisLabel(hoverIdx);
                        if (!string.IsNullOrEmpty(timeLabel))
                            tooltipText.AppendLine($"Time: {timeLabel}");
                    }

                    foreach (var s in _seriesList)
                    {
                        if (!s.IsVisible || s.Data == null || hoverIdx >= s.Data.Length) continue;
                        double val = s.Data[hoverIdx];
                        string valStr = double.IsNaN(val) ? "NaN" : val.ToString("F3");
                        tooltipText.AppendLine($"{s.Name}: {valStr}");
                    }

                    float tooltipX = (float)_hoverPos.X + 15;
                    float tooltipY = (float)_hoverPos.Y + 15;
                    DrawTooltip(canvas, tooltipText.ToString(), tooltipX, tooltipY);
                }
            }
        }

        private void DrawTooltip(SKCanvas c, string t, float x, float y)
        {
            var ls = t.Split('\n');
            // Calculate required width based on content
            float maxWidth = 0;
            using (var measurePaint = new SKPaint { TextSize = 11, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) })
            {
                foreach (var line in ls)
                {
                    float w = measurePaint.MeasureText(line);
                    if (w > maxWidth) maxWidth = w;
                }
            }
            float boxW = Math.Max(150, maxWidth + 15);
            float h = ls.Length * 16 + 10;

            // Adjust position if tooltip goes off screen
            if (x + boxW > c.LocalClipBounds.Width) x -= (boxW + 20);
            if (y + h > c.LocalClipBounds.Height) y = c.LocalClipBounds.Height - h - 10;

            using (var p = new SKPaint { Color = SKColors.White.WithAlpha(240), Style = SKPaintStyle.Fill })
            using (var b = new SKPaint { Color = SKColors.DarkSlateGray, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
            using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(50), Style = SKPaintStyle.Fill })
            {
                // Shadow
                c.DrawRect(new SKRect(x + 2, y + 2, x + boxW + 2, y + h + 2), shadow);
                // Background
                c.DrawRect(new SKRect(x, y, x + boxW, y + h), p);
                // Border
                c.DrawRect(new SKRect(x, y, x + boxW, y + h), b);
            }

            float ty = y + 14;
            foreach (var l in ls)
            {
                c.DrawText(l, x + 5, ty, _textPaintLeft);
                ty += 16;
            }
        }
    }
}