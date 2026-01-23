using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace IndiChart.UI
{
    public partial class StateTimeline : UserControl
    {
        public event Action<int, int> OnStateClicked;

        private List<StateInterval> _states;
        private int _totalLength;

        private SKPaint _fillPaint = new SKPaint { Style = SKPaintStyle.Fill };

        // --- הוספת מברשת לטקסט ---
        private SKPaint _textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 11,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) // טקסט מודגש
        };

        public StateTimeline()
        {
            InitializeComponent();
        }

        public void SetData(List<StateInterval> states, int totalLength)
        {
            _states = states;
            _totalLength = totalLength;
            TimelineCanvas.InvalidateVisual();
        }

        private void OnPaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            if (_states == null || _totalLength == 0) return;

            float w = info.Width;
            float h = info.Height;
            if (w <= 0) return;

            foreach (var st in _states)
            {
                float x1 = (float)((double)st.StartIndex / _totalLength * w);
                float x2 = (float)((double)st.EndIndex / _totalLength * w);

                if (x2 - x1 < 1) x2 = x1 + 1;

                // 1. ציור המלבן הצבעוני
                _fillPaint.Color = StateConfig.GetColor(st.StateId).WithAlpha(255);
                canvas.DrawRect(new SKRect(x1, 0, x2, h), _fillPaint);

                // 2. ציור הלייבל (טקסט)
                // נצייר רק אם יש מספיק מקום (למשל יותר מ-20 פיקסלים)
                if (x2 - x1 > 20)
                {
                    string name = StateConfig.GetName(st.StateId);
                    float textWidth = _textPaint.MeasureText(name);

                    // בדיקה שהטקסט לא חורג מרוחב המלבן (אופציונלי - כדי לשמור על סדר)
                    if (textWidth < (x2 - x1))
                    {
                        float centerX = x1 + (x2 - x1) / 2;
                        float textX = centerX - textWidth / 2;
                        float textY = h / 2 + 4; // מרכוז אנכי (בערך)

                        canvas.DrawText(name, textX, textY, _textPaint);
                    }
                }
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_states == null || _totalLength == 0) return;
            var pos = e.GetPosition(TimelineCanvas);
            float w = (float)TimelineCanvas.ActualWidth;
            int clickedIndex = (int)((pos.X / w) * _totalLength);

            foreach (var st in _states)
            {
                if (clickedIndex >= st.StartIndex && clickedIndex <= st.EndIndex)
                {
                    OnStateClicked?.Invoke(st.StartIndex, st.EndIndex);
                    return;
                }
            }
        }
    }
}