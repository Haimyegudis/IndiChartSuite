using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using IndiChart.Core;
using SkiaSharp;

namespace IndiChart.UI
{
    public partial class MainWindow : Window
    {
        private LogFileEngine _engine;
        private List<StateInterval> _globalStates;
        private string _currentCsvPath;

        public ObservableCollection<ChartViewModel> ActiveCharts { get; set; } = new ObservableCollection<ChartViewModel>();
        public ObservableCollection<StateEventItem> EventsList { get; set; } = new ObservableCollection<StateEventItem>();

        private List<GraphView> _registeredViews = new List<GraphView>();
        private List<string> _allSignalNames = new List<string>();

        private DispatcherTimer _playTimer;
        private double _playbackCurrentLine = 0;
        private double _playbackSpeed = 1.0;
        private bool _isPlaying = false;

        private int _currentViewStart = 0;
        private int _currentViewEnd = 1000;

        private int _limitRangeStart = -1;
        private int _limitRangeEnd = -1;

        private readonly SKColor[] _colors = {
            SKColors.DodgerBlue, SKColors.Crimson, SKColors.SeaGreen,
            SKColors.Orange, SKColors.BlueViolet, SKColors.Teal,
            SKColors.Magenta, SKColors.SaddleBrown
        };

        // Chart resize tracking
        private bool _isResizingChart = false;
        private ChartViewModel _resizingChart = null;
        private double _resizeStartY = 0;
        private double _resizeStartHeight = 0;

        // Left panel collapse tracking
        private bool _isLeftPanelCollapsed = false;
        private double _leftPanelPreviousWidth = 280;

        public MainWindow()
        {
            InitializeComponent();
            ChartsContainer.ItemsSource = ActiveCharts;
            EventsListView.ItemsSource = EventsList;
            MainStateTimeline.OnStateClicked += (start, end) => MasterSyncHandler(start, end, -1);
            _playTimer = new DispatcherTimer();
            _playTimer.Interval = TimeSpan.FromMilliseconds(20);
            _playTimer.Tick += PlayTimer_Tick;

            // Initialize navigation slider
            NavSlider.PreviewKeyDown += NavSlider_PreviewKeyDown;
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null) return;
            if (!_isPlaying)
            {
                // Always start from the beginning of the full dataset (0) or from current view start
                _playbackCurrentLine = _currentViewStart;

                // If there's a time range limit, respect it as starting point
                if (_limitRangeStart != -1 && _playbackCurrentLine < _limitRangeStart)
                    _playbackCurrentLine = _limitRangeStart;

                MasterCursorHandler((int)_playbackCurrentLine);
                _isPlaying = true;
                PlayButton.Content = "⏸ Pause";
                PlayButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));

                // עדכון כל הגרפים למצב פרוגרסיבי (ציור חי)
                foreach (var g in _registeredViews) g.IsProgressiveMode = true;

                _playTimer.Start();
            }
            else
            {
                _isPlaying = false;
                PlayButton.Content = "▶ Play";
                PlayButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                _playTimer.Stop();
                foreach (var g in _registeredViews) g.IsProgressiveMode = false;
            }
        }

        private void PlayTimer_Tick(object sender, EventArgs e)
        {
            if (_engine == null) { StopPlayback(); return; }
            _playbackCurrentLine += _playbackSpeed;
            int currentPos = (int)_playbackCurrentLine;
            MasterCursorHandler(currentPos);

            // Play until the end of the full dataset or until the time range limit
            int stopLimit = _engine.TotalRows - 1;
            if (_limitRangeEnd != -1 && _limitRangeEnd < stopLimit)
                stopLimit = _limitRangeEnd;

            if (currentPos >= stopLimit) StopPlayback();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            int resetPoint = _currentViewStart;
            if (_limitRangeStart != -1 && resetPoint < _limitRangeStart) resetPoint = _limitRangeStart;
            _playbackCurrentLine = resetPoint;
            MasterCursorHandler(resetPoint);
        }

        private void StopPlayback()
        {
            _isPlaying = false;
            _playTimer.Stop();
            PlayButton.Content = "▶ Play";
            PlayButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            foreach (var g in _registeredViews) g.IsProgressiveMode = false;
        }

        private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedCombo == null) return;
            var item = SpeedCombo.SelectedItem as ComboBoxItem;
            if (item != null && double.TryParse(item.Tag?.ToString(), out double s)) _playbackSpeed = s;
        }

        private async void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV Files (*.csv)|*.csv" };
            if (dlg.ShowDialog() == true)
            {
                LoadingBar.Visibility = Visibility.Visible;
                string path = dlg.FileName;
                try
                {
                    await Task.Run(() =>
                    {
                        if (_engine != null) _engine.Dispose();
                        _engine = new LogFileEngine();
                        _engine.Load(path);
                        _allSignalNames = _engine.ColumnNames;
                    });
                    _currentCsvPath = path;
                    if (CategoryFilter != null) CategoryFilter.SelectedIndex = 0;
                    ApplySignalFilter();
                    AnalyzeStates();
                    MainStateTimeline.SetData(_globalStates, _engine.TotalRows);
                    ActiveCharts.Clear();
                    _registeredViews.Clear();
                    CreateNewChart();
                }
                catch (Exception ex) { MessageBox.Show($"Error loading file: {ex.Message}"); }
                finally { LoadingBar.Visibility = Visibility.Collapsed; }
            }
        }

        private void AnalyzeStates()
        {
            _globalStates = new List<StateInterval>();
            EventsList.Clear();
            int stateColIndex = -1;
            var candidates = _engine.ColumnNames.Where(n => n.ToLower().Contains("state") && !n.ToLower().Contains("time")).ToList();
            if (candidates.Count > 0) stateColIndex = _engine.ColumnNames.IndexOf(candidates[0]);
            if (stateColIndex == -1) return;
            int limit = _engine.TotalRows;
            int currentStart = 0;
            int currentState = 0;
            if (limit > 3) currentState = StateConfig.GetId(_engine.GetStringAt(3, stateColIndex));
            AddEventToList(currentStart, currentState);
            for (int i = 3; i < limit; i++)
            {
                int newState = StateConfig.GetId(_engine.GetStringAt(i, stateColIndex));
                if (newState != currentState)
                {
                    _globalStates.Add(new StateInterval { StartIndex = currentStart, EndIndex = i - 1, StateId = currentState });
                    currentState = newState;
                    currentStart = i;
                    AddEventToList(currentStart, currentState);
                }
            }
            _globalStates.Add(new StateInterval { StartIndex = currentStart, EndIndex = limit - 1, StateId = currentState });
        }

        private void AddEventToList(int lineIndex, int stateId)
        {
            if (EventsList.Count < 5000)
            {
                EventsList.Add(new StateEventItem { LineIndex = lineIndex, Time = GetTimeForIndex(lineIndex), StateName = StateConfig.GetName(stateId), Color = StateConfig.GetColor(stateId) });
            }
        }

        private void GraphView_Loaded(object sender, RoutedEventArgs e)
        {
            var graph = sender as GraphView;
            if (graph?.DataContext is ChartViewModel vm)
            {
                graph.SetViewModel(vm);
                graph.GetXAxisLabel = GetTimeForIndex;
                graph.SetShowStates(ShowStatesCheck.IsChecked == true);
                graph.OnChartClicked += () => SelectChart(vm);
                graph.OnViewRangeChanged += (start, end) => MasterSyncHandler(start, end, -1);
                graph.OnCursorMoved += (cursor) => MasterCursorHandler(cursor);
                _registeredViews.Add(graph);
            }
        }

        private void MasterSyncHandler(int start, int end, int initiatorId)
        {
            _currentViewStart = start;
            _currentViewEnd = end;
            foreach (var g in _registeredViews) g.SyncViewRange(start, end);
            UpdateSliderPosition();
        }

        private void MasterCursorHandler(int cursor)
        {
            if (!_isPlaying) _playbackCurrentLine = cursor;
            foreach (var g in _registeredViews) g.SyncCursor(cursor);
        }

        private void ComponentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_engine == null || ComponentList.SelectedItem == null) return;
            string name = ComponentList.SelectedItem.ToString();
            int idx = _engine.ColumnNames.IndexOf(name);
            if (idx == -1) return;
            var activeVm = ActiveCharts.FirstOrDefault(c => c.IsSelected);
            if (activeVm == null)
            {
                if (ActiveCharts.Count > 0) activeVm = ActiveCharts.Last();
                else activeVm = CreateNewChart();
                SelectChart(activeVm);
            }
            try
            {
                double[] data = ExtractDataColumn(idx);
                var color = _colors[activeVm.Series.Count % _colors.Length];
                activeVm.Series.Add(new SignalSeries { Name = name, Data = data, Color = color });
                if (activeVm.Series.Count == 1) activeVm.Title = name;
                else activeVm.Title = string.Join(", ", activeVm.Series.Select(s => s.Name));
                if (activeVm.States == null) activeVm.States = _globalStates;
                var view = _registeredViews.FirstOrDefault(v => v.DataContext == activeVm);
                view?.SetViewModel(activeVm);
            }
            catch (Exception ex) { MessageBox.Show("Error adding signal: " + ex.Message); }
        }

        private double[] ExtractDataColumn(int idx)
        {
            if (_engine == null || idx < 0) return new double[0];
            int limit = _engine.TotalRows;
            double[] data = new double[limit];
            for (int i = 0; i < limit; i++)
            {
                if (i < 3) { data[i] = double.NaN; continue; }
                data[i] = _engine.GetValueAt(i, idx);
            }
            double last = double.NaN;
            for (int i = 0; i < limit; i++)
            {
                if (!double.IsNaN(data[i])) last = data[i];
                else if (!double.IsNaN(last)) data[i] = last;
            }
            return data;
        }

        private string _lastFindValue = "";
        private bool _lastFindGreater = false;

        // --- Improved Find Logic: scoped to current view with Next/Prev ---
        private void FindValue_Click(object sender, RoutedEventArgs e)
        {
            var activeVm = ActiveCharts.FirstOrDefault(c => c.IsSelected);
            if (activeVm == null || activeVm.Series.Count == 0) { MessageBox.Show("Please select a chart first.", "No Chart Selected"); return; }

            // Dialog for input with comprehensive instructions
            string instructions = "═══ FIND VALUE - QUERY SYNTAX ═══\n\n" +
                "BASIC SEARCH:\n" +
                "  • Exact value: 100\n" +
                "  • Greater than: >100\n" +
                "  • Less than: <50\n\n" +
                "DIRECTIONAL SEARCH:\n" +
                "  • Next occurrence: N:100 or N:>100\n" +
                "  • Previous occurrence: P:100 or P:<50\n\n" +
                "EXAMPLES:\n" +
                "  • Find exact 42: 42\n" +
                "  • Find next value > 100: N:>100\n" +
                "  • Find previous value < 20: P:<20\n" +
                "  • Find next exact 50: N:50\n\n" +
                "NOTES:\n" +
                "  • Search is within current view range\n" +
                "  • Uses first visible signal in chart\n" +
                "  • Decimal numbers supported (e.g., 23.5)\n" +
                "  • Red line = cursor, Blue line = result\n\n" +
                "Enter query below:";

            string input = Microsoft.VisualBasic.Interaction.InputBox(instructions, "Find Value", _lastFindValue);

            if (string.IsNullOrEmpty(input)) return;

            bool searchNext = input.StartsWith("N:", StringComparison.OrdinalIgnoreCase);
            bool searchPrev = input.StartsWith("P:", StringComparison.OrdinalIgnoreCase);
            string cleanInput = input;

            if (searchNext || searchPrev) cleanInput = input.Substring(2).Trim();

            bool greater = cleanInput.StartsWith(">");
            bool less = cleanInput.StartsWith("<");
            if (greater || less) cleanInput = cleanInput.Substring(1).Trim();

            if (!double.TryParse(cleanInput, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double target))
            {
                MessageBox.Show("Invalid number format. Please enter a valid number.", "Invalid Input");
                return;
            }

            // Save for next search
            _lastFindValue = input;
            _lastFindGreater = greater;

            var sig = activeVm.Series.FirstOrDefault(s => s.IsVisible);
            if (sig == null) { MessageBox.Show("No visible signals in selected chart.", "No Data"); return; }

            // Search limited to current view (considering time range limits)
            int startSearch = _currentViewStart;
            int endSearch = _currentViewEnd;

            // Apply time range limits if set
            if (_limitRangeStart != -1 && startSearch < _limitRangeStart) startSearch = _limitRangeStart;
            if (_limitRangeEnd != -1 && endSearch > _limitRangeEnd) endSearch = _limitRangeEnd;

            // Current cursor position (red line)
            int currentCursor = (int)_playbackCurrentLine;

            // Ensure cursor is within search range
            if (currentCursor < startSearch) currentCursor = startSearch;
            if (currentCursor > endSearch) currentCursor = endSearch;

            int foundIndex = -1;

            if (searchPrev)
            {
                // Search backward: from cursor-1 to start of window
                for (int i = currentCursor - 1; i >= startSearch; i--)
                {
                    if (i < sig.Data.Length && CheckMatch(sig.Data[i], target, greater, less)) { foundIndex = i; break; }
                }
            }
            else // Default or Next
            {
                // Search forward: if Next requested start from cursor+1, otherwise from start of window
                int from = searchNext ? currentCursor + 1 : currentCursor;

                for (int i = from; i <= endSearch; i++)
                {
                    if (i >= sig.Data.Length) break;
                    if (CheckMatch(sig.Data[i], target, greater, less)) { foundIndex = i; break; }
                }
            }

            if (foundIndex != -1)
            {
                MasterCursorHandler(foundIndex); // Move red line to result
                foreach (var g in _registeredViews) g.SetTargetLine(foundIndex); // Mark with blue line
            }
            else
            {
                string direction = searchPrev ? "before cursor" : "after cursor";
                MessageBox.Show($"Value not found {direction} in current view ({startSearch}-{endSearch}).", "Not Found");
            }
        }

        private bool CheckMatch(double val, double target, bool greater, bool less)
        {
            if (double.IsNaN(val)) return false;
            if (greater) return val > target;
            if (less) return val < target;
            return Math.Abs(val - target) < 0.01;
        }

        // --- הוספת קווי ייחוס (Reference Lines) ---
        private void AddReferenceLine_Click(object sender, RoutedEventArgs e)
        {
            var activeVm = ActiveCharts.FirstOrDefault(c => c.IsSelected);
            if (activeVm == null) { MessageBox.Show("Select a chart first."); return; }

            Window w = new Window { Title = "Add Reference Line", Width = 300, Height = 400, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
            StackPanel sp = new StackPanel { Margin = new Thickness(10) };

            sp.Children.Add(new TextBlock { Text = "Label Name:", FontWeight = FontWeights.Bold });
            TextBox txtName = new TextBox { Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(txtName);

            sp.Children.Add(new TextBlock { Text = "Value (Y) or Index (X):", FontWeight = FontWeights.Bold });
            TextBox txtValue = new TextBox { Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(txtValue);

            sp.Children.Add(new TextBlock { Text = "Type:", FontWeight = FontWeights.Bold });
            ComboBox cmbType = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
            cmbType.ItemsSource = Enum.GetValues(typeof(ReferenceLineType));
            cmbType.SelectedIndex = 1; // Default Horizontal
            sp.Children.Add(cmbType);

            sp.Children.Add(new TextBlock { Text = "Color (Name or Hex):", FontWeight = FontWeights.Bold });
            TextBox txtColor = new TextBox { Text = "Red", Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(txtColor);

            CheckBox chkDashed = new CheckBox { Content = "Dashed Line", IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(chkDashed);

            sp.Children.Add(new TextBlock { Text = "Thickness:", FontWeight = FontWeights.Bold });
            TextBox txtThick = new TextBox { Text = "2", Margin = new Thickness(0, 0, 0, 10) }; sp.Children.Add(txtThick);

            Button btnAdd = new Button { Content = "Add Line", Height = 30, Margin = new Thickness(0, 10, 0, 0) };
            btnAdd.Click += (s, args) =>
            {
                if (double.TryParse(txtValue.Text, out double val) && float.TryParse(txtThick.Text, out float thick))
                {
                    SKColor color = SKColors.Red;
                    try { color = SKColor.Parse(txtColor.Text); } catch { }

                    activeVm.ReferenceLines.Add(new ReferenceLine
                    {
                        Name = txtName.Text,
                        Value = val,
                        Type = (ReferenceLineType)cmbType.SelectedItem,
                        Color = color,
                        IsDashed = chkDashed.IsChecked == true,
                        Thickness = thick
                    });

                    var view = _registeredViews.FirstOrDefault(v => v.DataContext == activeVm);
                    view?.SetViewModel(activeVm); // רענון תצוגה
                    w.Close();
                }
                else MessageBox.Show("Invalid Value or Thickness");
            };
            sp.Children.Add(btnAdd);
            w.Content = sp;
            w.ShowDialog();
        }

        private void SetTimeRange_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null) return;
            Window rangeWin = new Window { Title = "Set Range", Width = 300, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
            StackPanel sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = "Start Index:", Margin = new Thickness(0, 0, 0, 5) });
            TextBox txtStart = new TextBox { Text = (_currentViewStart).ToString(), Padding = new Thickness(5) }; sp.Children.Add(txtStart);
            sp.Children.Add(new TextBlock { Text = "End Index:", Margin = new Thickness(0, 10, 0, 5) });
            TextBox txtEnd = new TextBox { Text = (_currentViewEnd).ToString(), Padding = new Thickness(5) }; sp.Children.Add(txtEnd);
            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            Button btnOk = new Button { Content = "Apply", Width = 80, Margin = new Thickness(5), Padding = new Thickness(5) };
            btnOk.Click += (s, args) => { if (int.TryParse(txtStart.Text, out int sVal) && int.TryParse(txtEnd.Text, out int eVal)) { if (sVal < eVal) { _limitRangeStart = Math.Max(0, sVal); _limitRangeEnd = Math.Min(_engine.TotalRows, eVal); MasterSyncHandler(_limitRangeStart, Math.Min(_limitRangeStart + 1000, _limitRangeEnd), -1); rangeWin.DialogResult = true; } else MessageBox.Show("Invalid Range"); } };
            Button btnClear = new Button { Content = "Clear", Width = 80, Margin = new Thickness(5), Padding = new Thickness(5) };
            btnClear.Click += (s, args) => { _limitRangeStart = -1; _limitRangeEnd = -1; rangeWin.DialogResult = true; };
            btns.Children.Add(btnClear); btns.Children.Add(btnOk); sp.Children.Add(btns); rangeWin.Content = sp; rangeWin.ShowDialog();
        }

        private void ExportChart_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Tag is ChartViewModel vm)
            {
                var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = vm.Title };
                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(); sb.Append("Line,Time"); foreach (var ser in vm.Series) sb.Append("," + ser.Name); sb.AppendLine();
                        int startIdx = _currentViewStart; int endIdx = Math.Min(_currentViewEnd, _engine.TotalRows);
                        for (int i = startIdx; i <= endIdx; i++) { sb.Append($"{i},{GetTimeForIndex(i)}"); foreach (var ser in vm.Series) { double val = (i < ser.Data.Length) ? ser.Data[i] : 0; sb.Append($",{val}"); } sb.AppendLine(); }
                        File.WriteAllText(dlg.FileName, sb.ToString());
                    }
                    catch (Exception ex) { MessageBox.Show("Export failed: " + ex.Message); }
                }
            }
        }
        // הוסף את הפונקציה הזו למחלקה MainWindow
        private void ManageLines_Click(object sender, RoutedEventArgs e)
        {
            var activeVm = ActiveCharts.FirstOrDefault(c => c.IsSelected);
            if (activeVm == null)
            {
                MessageBox.Show("Please select a chart first (click on a chart).", "No Chart Selected");
                return;
            }

            var activeView = _registeredViews.FirstOrDefault(v => v.DataContext == activeVm);
            if (activeView == null)
            {
                MessageBox.Show("Cannot find the chart view.", "Error");
                return;
            }

            // Open the reference lines manager window
            ReferenceLineManager manager = new ReferenceLineManager(activeVm, activeView);
            manager.Owner = this;
            manager.ShowDialog(); // Use ShowDialog to make it modal
        }
        private void Snapshot_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            DependencyObject current = btn; while (current != null && !(current is Border && (current as Border).Style == FindResource("ChartCardStyle") as Style)) current = VisualTreeHelper.GetParent(current);
            var cardBorder = current as Border; if (cardBorder == null) return;
            var dlg = new SaveFileDialog { Filter = "PNG Image (*.png)|*.png" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    double scale = 2.0; int w = (int)(cardBorder.ActualWidth * scale); int h = (int)(cardBorder.ActualHeight * scale);
                    RenderTargetBitmap renderBmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    DrawingVisual dv = new DrawingVisual(); using (DrawingContext dc = dv.RenderOpen()) { VisualBrush vb = new VisualBrush(cardBorder); dc.DrawRectangle(vb, null, new Rect(0, 0, cardBorder.ActualWidth, cardBorder.ActualHeight)); dc.PushTransform(new ScaleTransform(scale, scale)); }
                    cardBorder.Measure(new Size(cardBorder.ActualWidth, cardBorder.ActualHeight)); cardBorder.Arrange(new Rect(0, 0, cardBorder.ActualWidth, cardBorder.ActualHeight));
                    renderBmp.Render(cardBorder); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(renderBmp));
                    using (FileStream fs = new FileStream(dlg.FileName, FileMode.Create)) { encoder.Save(fs); }
                }
                catch (Exception ex) { MessageBox.Show("Snapshot failed: " + ex.Message); }
            }
        }

        private void ApplySignalFilter()
        {
            if (SearchBox == null || CategoryFilter == null || _allSignalNames == null) return;
            string query = SearchBox.Text.ToLower(); int catIdx = CategoryFilter.SelectedIndex;
            var filtered = _allSignalNames.Where(name => { string n = name.ToLower(); if (!string.IsNullOrWhiteSpace(query) && !n.Contains(query)) return false; if (catIdx == 1) return n.Contains("axis") || n.Contains("ax") || n.Contains("pos"); if (catIdx == 2) return n.Contains("in") || n.Contains("out") || n.Contains("di") || n.Contains("do"); if (catIdx == 3) return n.Contains("state") || n.Contains("mode"); return true; }).ToList();
            ComponentList.ItemsSource = filtered;
        }

        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySignalFilter();
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySignalFilter();
        private void ToggleAxis_Click(object sender, RoutedEventArgs e) { var btn = sender as Button; if (btn?.Tag is SignalSeries ser) { ser.YAxisType = (ser.YAxisType == AxisType.Left) ? AxisType.Right : AxisType.Left; foreach (var chart in ActiveCharts) { if (chart.Series.Contains(ser)) { var view = _registeredViews.FirstOrDefault(v => v.DataContext == chart); view?.SetViewModel(chart); break; } } } }
        private void SeriesVisibility_Changed(object sender, RoutedEventArgs e)
        {
            // Refresh all graph views when series visibility changes
            foreach (var view in _registeredViews)
            {
                if (view.DataContext is ChartViewModel vm)
                {
                    view.SetViewModel(vm);
                }
            }
        }
        private void SaveLayout_Click(object sender, RoutedEventArgs e) { if (ActiveCharts.Count == 0) return; var dlg = new SaveFileDialog { Filter = "Layout (*.json)|*.json" }; if (dlg.ShowDialog() == true) { var ws = new WorkspaceModel { SourceCsvPath = _currentCsvPath }; foreach (var c in ActiveCharts) ws.Charts.Add(new ChartSaveData { Title = c.Title, Series = c.Series.Select(s => new SeriesSaveData { Name = s.Name, IsVisible = s.IsVisible, ColorHex = s.Color.ToString(), Axis = s.YAxisType }).ToList() }); File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(ws)); } }
        private void LoadLayout_Click(object sender, RoutedEventArgs e) { var dlg = new OpenFileDialog { Filter = "Layout (*.json)|*.json" }; if (dlg.ShowDialog() == true) { var ws = JsonSerializer.Deserialize<WorkspaceModel>(File.ReadAllText(dlg.FileName)); if (!string.IsNullOrEmpty(ws.SourceCsvPath) && _currentCsvPath != ws.SourceCsvPath && File.Exists(ws.SourceCsvPath)) { if (_engine != null) _engine.Dispose(); _engine = new LogFileEngine(); _engine.Load(ws.SourceCsvPath); _allSignalNames = _engine.ColumnNames; _currentCsvPath = ws.SourceCsvPath; AnalyzeStates(); } ActiveCharts.Clear(); _registeredViews.Clear(); foreach (var cData in ws.Charts) { var vm = new ChartViewModel { Title = cData.Title, States = _globalStates }; foreach (var sData in cData.Series) { int idx = _engine.ColumnNames.IndexOf(sData.Name); if (idx != -1) vm.Series.Add(new SignalSeries { Name = sData.Name, Data = ExtractDataColumn(idx), Color = SKColor.Parse(sData.ColorHex), IsVisible = sData.IsVisible, YAxisType = sData.Axis }); } ActiveCharts.Add(vm); } } }
        private void Help_Click(object sender, RoutedEventArgs e)
        {
            string helpText =
                "═══════════════════════════════════════════════════\n" +
                "     IndiChart Analytics Pro - Complete Guide\n" +
                "═══════════════════════════════════════════════════\n\n" +

                "FILE OPERATIONS:\n" +
                "  • Load CSV File (Ctrl+O): Import data from CSV files\n" +
                "  • Save Layout (Ctrl+S): Save current workspace configuration\n" +
                "  • Load Layout: Restore saved workspace\n\n" +

                "CHART MANAGEMENT:\n" +
                "  • Double-click signal: Add to selected chart\n" +
                "  • Add Chart: Create new empty chart\n" +
                "  • Click chart: Select it for signal additions\n" +
                "  • Close button (✖): Remove chart\n" +
                "  • Series checkbox: Show/hide individual signals\n" +
                "  • Axis button (L/R): Switch signal between left/right axis\n\n" +

                "NAVIGATION & ZOOM:\n" +
                "  • Mouse wheel: Zoom in/out on charts\n" +
                "  • Click States window: Jump to that state period\n" +
                "  • Navigation slider: Scroll through data horizontally\n" +
                "  • Arrow buttons: Navigate left/right by 100 points\n" +
                "  • Keyboard arrows: Navigate when slider focused\n" +
                "  • Ctrl+Left/Right: Navigate from charts area\n" +
                "  • Reset Zoom: Return to full data view\n" +
                "  • Jump to Line (Ctrl+G): Go to specific data index\n\n" +

                "MEASUREMENT TOOLS:\n" +
                "  • Shift+Drag: Measure area (shows avg, min, max, delta)\n" +
                "  • Ctrl+Click twice: 2-point measurement (distance, values)\n" +
                "  • Alt+Hover: Show exact values at cursor position\n" +
                "  • Escape: Clear all measurements\n" +
                "  • Tooltips appear AFTER selection is complete\n\n" +

                "SEARCH & FIND:\n" +
                "  • Find Value (Ctrl+F): Search for specific values\n" +
                "    - Exact: 100\n" +
                "    - Greater than: >100\n" +
                "    - Less than: <50\n" +
                "    - Next: N:>100\n" +
                "    - Previous: P:<20\n" +
                "  • Red line: Current cursor position\n" +
                "  • Blue line: Search result target\n\n" +

                "REFERENCE LINES:\n" +
                "  • Manage Reference Lines: Add/edit guide lines\n" +
                "  • Horizontal: Fixed Y-value across chart\n" +
                "  • Vertical: Fixed X-index position\n" +
                "  • Customizable: Name, color, thickness, dashed style\n" +
                "  • Per-axis: Lines can follow left or right Y-axis\n\n" +

                "STATES & TIMELINE:\n" +
                "  • Show States: Toggle state display on/off\n" +
                "  • State colors: Each state has unique color\n" +
                "  • State labels: Shown in both timeline and charts\n" +
                "  • States Transitions tab: List of all state changes\n" +
                "  • Double-click state event: Jump to that time\n\n" +

                "PLAYBACK CONTROLS:\n" +
                "  • Play/Pause: Animate data progression\n" +
                "  • Stop: Reset to view start\n" +
                "  • Speed: Control playback rate (x0.5 to x50)\n" +
                "  • Progressive mode: Chart draws up to cursor\n\n" +

                "DATA FILTERING:\n" +
                "  • Category Filter: Filter signals by type\n" +
                "    - All Signals\n" +
                "    - Axis / Motion\n" +
                "    - IO / Sensors\n" +
                "    - States / Logic\n" +
                "  • Search box: Filter signals by name\n\n" +

                "EXPORT & CAPTURE:\n" +
                "  • Export (💾): Save chart data to CSV\n" +
                "  • Snapshot (📷): Capture chart as PNG image\n\n" +

                "TIME RANGE:\n" +
                "  • Set Time Range: Limit analysis to specific period\n" +
                "  • Applies to playback and searches\n" +
                "  • Clear to restore full dataset\n\n" +

                "APPEARANCE:\n" +
                "  • Dark Mode: Toggle between light/dark themes\n" +
                "  • Chart cards: Modern card-based layout\n" +
                "  • Multiple Y-axes: Left and right axes per chart\n\n" +

                "TIPS:\n" +
                "  • Measurements don't interfere with zoom/pan\n" +
                "  • Multiple charts can show same signals\n" +
                "  • State colors are configurable in StateConfig\n" +
                "  • CSV first row: Headers, subsequent rows: Data\n" +
                "  • Time column should be first in CSV\n\n" +

                "═══════════════════════════════════════════════════\n" +
                "Version 1.0 | Built with SkiaSharp & WPF";

            MessageBox.Show(helpText, "IndiChart Analytics Pro - Help", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ShowStates_MenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                bool isChecked = menuItem.IsChecked;
                ShowStatesCheck.IsChecked = isChecked;
                ShowStates_CheckedChanged(sender, e);
            }
        }

        private bool _isDarkMode = false;
        private void DarkMode_Click(object sender, RoutedEventArgs e)
        {
            _isDarkMode = !_isDarkMode;
            ApplyTheme(_isDarkMode);
        }

        private void ApplyTheme(bool isDark)
        {
            var menuBar = (Menu)this.FindName("MenuBar");
            var toolbar = this.FindResource("ModernButton") as Style;

            if (isDark)
            {
                // Dark mode colors
                this.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F2937"));

                // Apply dark theme to all UI elements
                ApplyDarkThemeRecursive(this);

                MessageBox.Show("Dark mode enabled! Please note: some elements may require restart for full effect.", "Dark Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // Light mode colors
                this.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F4F6"));

                // Apply light theme to all UI elements
                ApplyLightThemeRecursive(this);

                MessageBox.Show("Light mode enabled!", "Light Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ApplyDarkThemeRecursive(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is Border border)
                {
                    if (border.Background is SolidColorBrush brush && brush.Color == Colors.White)
                    {
                        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
                    }
                }
                else if (child is Panel panel)
                {
                    if (panel.Background is SolidColorBrush brush && brush.Color == Colors.White)
                    {
                        panel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
                    }
                }
                else if (child is TextBlock textBlock)
                {
                    textBlock.Foreground = Brushes.White;
                }
                else if (child is Menu menu)
                {
                    menu.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
                    menu.Foreground = Brushes.White;
                }

                ApplyDarkThemeRecursive(child);
            }
        }

        private void ApplyLightThemeRecursive(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is Border border)
                {
                    if (border.Background is SolidColorBrush brush)
                    {
                        border.Background = Brushes.White;
                    }
                }
                else if (child is Panel panel)
                {
                    if (panel.Background is SolidColorBrush)
                    {
                        panel.Background = Brushes.White;
                    }
                }
                else if (child is TextBlock textBlock)
                {
                    textBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111827"));
                }
                else if (child is Menu menu)
                {
                    menu.Background = Brushes.White;
                    menu.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111827"));
                }

                ApplyLightThemeRecursive(child);
            }
        }
        private ChartViewModel CreateNewChart() { var vm = new ChartViewModel { States = _globalStates, Title = "Chart " + (ActiveCharts.Count + 1) }; ActiveCharts.Add(vm); SelectChart(vm); return vm; }
        private void AddEmptyChart_Click(object sender, RoutedEventArgs e) => CreateNewChart();
        private void SelectChart(ChartViewModel vm) { foreach (var c in ActiveCharts) c.IsSelected = false; vm.IsSelected = true; }
        private void ChartBorder_MouseDown(object sender, MouseButtonEventArgs e) { if ((sender as Border)?.DataContext is ChartViewModel vm) SelectChart(vm); }
        private void ResetZoom_Click(object sender, RoutedEventArgs e) { if (_engine != null) MasterSyncHandler(0, _engine.TotalRows, -1); }
        private void RemoveChart_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is ChartViewModel vm) { ActiveCharts.Remove(vm); _registeredViews.RemoveAll(v => v.DataContext == vm); } }
        private void RemoveSeries_Click(object sender, RoutedEventArgs e) { var btn = sender as Button; if (btn?.Tag is SignalSeries s) { foreach (var c in ActiveCharts) if (c.Series.Contains(s)) { c.Series.Remove(s); var v = _registeredViews.FirstOrDefault(view => view.DataContext == c); v?.SetViewModel(c); break; } } }
        private void ShowStates_CheckedChanged(object sender, RoutedEventArgs e) { if (MainStateTimeline != null && _registeredViews != null) { bool s = ShowStatesCheck.IsChecked == true; foreach (var g in _registeredViews) g.SetShowStates(s); MainStateTimeline.Visibility = s ? Visibility.Visible : Visibility.Collapsed; } }
        private void EventsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) { var it = ((ListView)sender).SelectedItem as StateEventItem; if (it != null) { int r = 2000; MasterSyncHandler(Math.Max(0, it.LineIndex - r / 2), Math.Min(_engine.TotalRows, it.LineIndex + r / 2), -1); foreach (var g in _registeredViews) g.SetTargetLine(it.LineIndex); } }

        private void JumpToLine_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null)
            {
                MessageBox.Show("Please load a CSV file first.", "No Data");
                return;
            }

            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter line index to jump to:",
                "Jump to Line",
                "0");

            if (string.IsNullOrEmpty(input)) return;

            if (int.TryParse(input, out int lineIndex))
            {
                if (lineIndex < 0 || lineIndex >= _engine.TotalRows)
                {
                    MessageBox.Show($"Line index must be between 0 and {_engine.TotalRows - 1}", "Invalid Index");
                    return;
                }

                int range = 1000;
                MasterSyncHandler(Math.Max(0, lineIndex - range / 2), Math.Min(_engine.TotalRows, lineIndex + range / 2), -1);
                foreach (var g in _registeredViews) g.SetTargetLine(lineIndex);
            }
            else
            {
                MessageBox.Show("Please enter a valid number.", "Invalid Input");
            }
        }

        // Navigation Slider Handlers
        private void NavSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_engine == null || _isUpdatingSlider) return;

            // Calculate the start position based on slider value (0-100%)
            int totalDataRange = _engine.TotalRows - 1000; // Reserve space for the view window
            if (totalDataRange < 0) totalDataRange = 0;

            int newStart = (int)((NavSlider.Value / 100.0) * totalDataRange);
            int newEnd = Math.Min(newStart + 1000, _engine.TotalRows);

            MasterSyncHandler(newStart, newEnd, -1);
        }

        private bool _isUpdatingSlider = false;

        private void NavLeft_Click(object sender, RoutedEventArgs e)
        {
            NavigateHorizontal(-100); // Move left by 100 data points
        }

        private void NavRight_Click(object sender, RoutedEventArgs e)
        {
            NavigateHorizontal(100); // Move right by 100 data points
        }

        private void NavSlider_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left)
            {
                NavigateHorizontal(-100);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                NavigateHorizontal(100);
                e.Handled = true;
            }
        }

        private void ChartsArea_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                NavigateHorizontal(-100);
                e.Handled = true;
            }
            else if (e.Key == Key.Right && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                NavigateHorizontal(100);
                e.Handled = true;
            }
        }

        private void NavigateHorizontal(int offset)
        {
            if (_engine == null) return;

            int newStart = _currentViewStart + offset;
            int viewRange = _currentViewEnd - _currentViewStart;

            // Clamp to valid range
            if (newStart < 0) newStart = 0;
            if (newStart + viewRange > _engine.TotalRows)
                newStart = _engine.TotalRows - viewRange;

            int newEnd = newStart + viewRange;

            MasterSyncHandler(newStart, newEnd, -1);

            // Update slider position
            UpdateSliderPosition();
        }

        private void UpdateSliderPosition()
        {
            if (_engine == null) return;

            _isUpdatingSlider = true;

            int totalDataRange = _engine.TotalRows - 1000;
            if (totalDataRange <= 0)
            {
                NavSlider.Value = 0;
            }
            else
            {
                double percentage = ((double)_currentViewStart / totalDataRange) * 100.0;
                NavSlider.Value = Math.Max(0, Math.Min(100, percentage));
            }

            _isUpdatingSlider = false;
        }

        private string GetTimeForIndex(int i) => _engine?.GetStringAt(i, 0) ?? i.ToString();

        // Chart height resize handlers
        private void ChartResizeGripper_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is ChartViewModel vm)
            {
                _isResizingChart = true;
                _resizingChart = vm;
                _resizeStartY = e.GetPosition(this).Y;
                _resizeStartHeight = vm.ChartHeight;
                border.CaptureMouse();
                e.Handled = true;
            }
        }

        private void ChartResizeGripper_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizingChart && _resizingChart != null)
            {
                double deltaY = e.GetPosition(this).Y - _resizeStartY;
                double newHeight = _resizeStartHeight + deltaY;

                // Constrain height between min and max values
                newHeight = Math.Max(100, Math.Min(600, newHeight));
                _resizingChart.ChartHeight = newHeight;
                e.Handled = true;
            }
        }

        private void ChartResizeGripper_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizingChart)
            {
                _isResizingChart = false;
                _resizingChart = null;
                var border = sender as Border;
                border?.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        // Left panel collapse/expand handler
        private void ToggleLeftPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_isLeftPanelCollapsed)
            {
                // Expand panel
                LeftPanelColumn.Width = new GridLength(_leftPanelPreviousWidth);
                LeftPanelBorder.Visibility = Visibility.Visible;
                CollapseButton.Content = "◀";
                _isLeftPanelCollapsed = false;
            }
            else
            {
                // Collapse panel
                _leftPanelPreviousWidth = LeftPanelColumn.Width.Value;
                LeftPanelColumn.Width = new GridLength(0);
                LeftPanelBorder.Visibility = Visibility.Collapsed;
                CollapseButton.Content = "▶";
                _isLeftPanelCollapsed = true;
            }
        }
    }
}