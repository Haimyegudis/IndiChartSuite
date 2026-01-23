using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using SkiaSharp;
using System.Windows.Media; // לשימוש ב-Colors של WPF להמרה

namespace IndiChart.UI
{
    public enum AxisType { Left, Right }
    public enum ReferenceLineType { Vertical, Horizontal }

    // מחלקה המייצגת קו ייחוס בודד
    public class ReferenceLine : INotifyPropertyChanged
    {
        private string _name = "Line";
        public string Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); } }

        private double _value = 0;
        public double Value { get => _value; set { _value = value; OnPropertyChanged(nameof(Value)); } }

        private ReferenceLineType _type = ReferenceLineType.Horizontal;
        public ReferenceLineType Type { get => _type; set { _type = value; OnPropertyChanged(nameof(Type)); } }

        private SKColor _color = SKColors.Red;
        public SKColor Color { get => _color; set { _color = value; OnPropertyChanged(nameof(Color)); OnPropertyChanged(nameof(ColorString)); } }

        // מאפיין עזר לקשירה ל-TextBox (למשל כתיבת "Blue" או "#00FF00")
        public string ColorString
        {
            get => _color.ToString();
            set
            {
                // נסיון המרה פשוט
                if (SKColor.TryParse(value, out SKColor c))
                {
                    Color = c;
                }
                else
                {
                    try
                    {
                        // תמיכה בשמות צבעים של WPF
                        var wpfColor = (Color)ColorConverter.ConvertFromString(value);
                        Color = new SKColor(wpfColor.R, wpfColor.G, wpfColor.B, wpfColor.A);
                    }
                    catch { }
                }
            }
        }

        private float _thickness = 2.0f;
        public float Thickness { get => _thickness; set { _thickness = value; OnPropertyChanged(nameof(Thickness)); } }

        private bool _isDashed = true;
        public bool IsDashed { get => _isDashed; set { _isDashed = value; OnPropertyChanged(nameof(IsDashed)); } }

        private AxisType _yAxis = AxisType.Left;
        public AxisType YAxis { get => _yAxis; set { _yAxis = value; OnPropertyChanged(nameof(YAxis)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SignalSeries : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public double[] Data { get; set; }
        public SKColor Color { get; set; }

        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); } }

        private AxisType _axisType = AxisType.Left;
        public AxisType YAxisType { get => _axisType; set { _axisType = value; OnPropertyChanged(nameof(YAxisType)); OnPropertyChanged(nameof(AxisDisplay)); } }
        public string AxisDisplay => YAxisType == AxisType.Left ? "L" : "R";

        private string _currentValueDisplay = "-";
        public string CurrentValueDisplay { get => _currentValueDisplay; set { _currentValueDisplay = value; OnPropertyChanged(nameof(CurrentValueDisplay)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ChartViewModel : INotifyPropertyChanged
    {
        private string _title;
        public string Title { get => _title; set { _title = value; OnPropertyChanged(nameof(Title)); } }

        public ObservableCollection<SignalSeries> Series { get; set; } = new ObservableCollection<SignalSeries>();

        // אוסף קווי הייחוס - כאן יאוחסנו הקווים שהמשתמש יוצר
        public ObservableCollection<ReferenceLine> ReferenceLines { get; set; } = new ObservableCollection<ReferenceLine>();

        public List<StateInterval> States { get; set; }
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class StateEventItem
    {
        public string Time { get; set; }
        public string StateName { get; set; }
        public int LineIndex { get; set; }
        public SKColor Color { get; set; }
    }

    public struct StateInterval { public int StartIndex; public int EndIndex; public int StateId; }

    public class WorkspaceModel { public string SourceCsvPath { get; set; } public List<ChartSaveData> Charts { get; set; } = new List<ChartSaveData>(); }

    public class ChartSaveData
    {
        public string Title { get; set; }
        public List<SeriesSaveData> Series { get; set; } = new List<SeriesSaveData>();
        public List<ReferenceLine> ReferenceLines { get; set; } = new List<ReferenceLine>();
    }

    public class SeriesSaveData { public string Name { get; set; } public string ColorHex { get; set; } public bool IsVisible { get; set; } public AxisType Axis { get; set; } }
}