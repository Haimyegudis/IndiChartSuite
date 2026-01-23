using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using SkiaSharp;

namespace IndiChart.UI
{
    public enum AxisType { Left, Right }

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
        public ObservableCollection<double> Thresholds { get; set; } = new ObservableCollection<double>(); // פיצ'ר חדש: קווי גבול

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
        public List<double> Thresholds { get; set; } = new List<double>();
    }
    public class SeriesSaveData { public string Name { get; set; } public string ColorHex { get; set; } public bool IsVisible { get; set; } public AxisType Axis { get; set; } }
}