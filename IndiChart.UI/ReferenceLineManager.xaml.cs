using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IndiChart.UI
{
    public partial class ReferenceLineManager : Window
    {
        private ChartViewModel _viewModel;
        private GraphView _graphView;

        public ReferenceLineManager(ChartViewModel vm, GraphView view)
        {
            InitializeComponent();
            _viewModel = vm;
            _graphView = view;

            // Create a working copy to prevent auto-save
            LinesListBox.ItemsSource = _viewModel.ReferenceLines;

            // Initialize dropdowns
            CmbType.ItemsSource = Enum.GetValues(typeof(ReferenceLineType));
            CmbAxis.ItemsSource = Enum.GetValues(typeof(AxisType));
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            // יצירת קו ברירת מחדל
            var newLine = new ReferenceLine
            {
                Name = $"Line {_viewModel.ReferenceLines.Count + 1}",
                Value = 0,
                Type = ReferenceLineType.Horizontal,
                Color = SkiaSharp.SKColors.Red,
                Thickness = 2
            };
            _viewModel.ReferenceLines.Add(newLine);
            LinesListBox.SelectedItem = newLine;
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (LinesListBox.SelectedItem is ReferenceLine line)
            {
                _viewModel.ReferenceLines.Remove(line);
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            // Refresh the graph view
            if (_graphView != null)
            {
                _graphView.SetViewModel(_viewModel);
            }

            // Close the window after applying
            this.Close();
        }

        private void CmbColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LinesListBox.SelectedItem is ReferenceLine line && CmbColor.SelectedItem is ComboBoxItem item)
            {
                string colorHex = item.Tag?.ToString();
                if (!string.IsNullOrEmpty(colorHex))
                {
                    line.ColorString = colorHex;
                }
            }
        }

        private void LinesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // When a line is selected, update the color picker to show its current color
            if (LinesListBox.SelectedItem is ReferenceLine line)
            {
                string currentColor = line.ColorString;
                for (int i = 0; i < CmbColor.Items.Count; i++)
                {
                    if (CmbColor.Items[i] is ComboBoxItem item && item.Tag?.ToString() == currentColor)
                    {
                        CmbColor.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
    }

    // ממיר פשוט שבודק אם נבחר משהו ברשימה כדי לאפשר/לחסום את צד העריכה
    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}