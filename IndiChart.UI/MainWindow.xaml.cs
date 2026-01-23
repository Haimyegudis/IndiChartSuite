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
        // המנוע לטעינת קבצים
        private LogFileEngine _engine;
        // רשימת הסטייטים הגלובלית
        private List<StateInterval> _globalStates;
        // נתיב הקובץ הנוכחי
        private string _currentCsvPath;

        // רשימות שמחוברות לממשק (Binding)
        public ObservableCollection<ChartViewModel> ActiveCharts { get; set; } = new ObservableCollection<ChartViewModel>();
        public ObservableCollection<StateEventItem> EventsList { get; set; } = new ObservableCollection<StateEventItem>();

        // רשימת הגרפים הפעילים (לצרכי סנכרון)
        private List<GraphView> _registeredViews = new List<GraphView>();
        // רשימת כל הסיגנלים הקיימים בקובץ
        private List<string> _allSignalNames = new List<string>();

        // --- משתנים לנגן (Playback) ---
        private DispatcherTimer _playTimer;
        private double _playbackCurrentLine = 0;
        private double _playbackSpeed = 1.0;
        private bool _isPlaying = false;

        // גבולות התצוגה הנוכחיים (מה רואים במסך)
        private int _currentViewStart = 0;
        private int _currentViewEnd = 1000;

        // גבולות טווח מוגדרים ידנית (Custom Range)
        private int _limitRangeStart = -1;
        private int _limitRangeEnd = -1;

        // פלטת צבעים
        private readonly SKColor[] _colors = {
            SKColors.DodgerBlue, SKColors.Crimson, SKColors.SeaGreen,
            SKColors.Orange, SKColors.BlueViolet, SKColors.Teal,
            SKColors.Magenta, SKColors.SaddleBrown
        };

        public MainWindow()
        {
            InitializeComponent();

            // חיבור הרשימות לממשק
            ChartsContainer.ItemsSource = ActiveCharts;
            EventsListView.ItemsSource = EventsList;

            // אירוע לחיצה על פס הזמן העליון
            MainStateTimeline.OnStateClicked += (start, end) => MasterSyncHandler(start, end, -1);

            // הגדרת הטיימר לנגן (50 פריימים לשנייה בערך)
            _playTimer = new DispatcherTimer();
            _playTimer.Interval = TimeSpan.FromMilliseconds(20);
            _playTimer.Tick += PlayTimer_Tick;
        }

        // ---------------------------------------------------------
        //                     לוגיקת נגן (Playback)
        // ---------------------------------------------------------

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null) return;

            if (!_isPlaying)
            {
                // התחלה: תמיד מתחילים מהצד השמאלי של מה שרואים כרגע (Replay Window)
                _playbackCurrentLine = _currentViewStart;

                // אם המשתמש הגדיר טווח ידני, נוודא שאנחנו בתוכו
                if (_limitRangeStart != -1 && _playbackCurrentLine < _limitRangeStart)
                {
                    _playbackCurrentLine = _limitRangeStart;
                }

                // עדכון הסמן הלוגי (ללא ציור קו)
                MasterCursorHandler((int)_playbackCurrentLine);

                // שינוי מצב כפתור
                _isPlaying = true;
                PlayButton.Content = "⏸ Pause";
                PlayButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")); // כתום

                // הפעלת "מצב בנייה" בכל הגרפים (מסתיר את העתיד)
                foreach (var g in _registeredViews)
                {
                    g.IsProgressiveMode = true;
                }

                _playTimer.Start();
            }
            else
            {
                // Pause
                _isPlaying = false;
                PlayButton.Content = "▶ Play";
                PlayButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // ירוק
                _playTimer.Stop();
            }
        }

        private void PlayTimer_Tick(object sender, EventArgs e)
        {
            if (_engine == null) { StopPlayback(); return; }

            // קידום המיקום
            _playbackCurrentLine += _playbackSpeed;
            int currentPos = (int)_playbackCurrentLine;

            // עדכון הגרפים
            MasterCursorHandler(currentPos);

            // --- תנאי עצירה ---
            // עוצרים אם הגענו לקצה הימני של המסך הנוכחי
            int stopLimit = _currentViewEnd;

            // או אם הגענו לקצה הטווח המוגדר
            if (_limitRangeEnd != -1 && stopLimit > _limitRangeEnd)
                stopLimit = _limitRangeEnd;

            // או אם נגמר הקובץ
            if (stopLimit >= _engine.TotalRows)
                stopLimit = _engine.TotalRows - 1;

            if (currentPos >= stopLimit)
            {
                StopPlayback();
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();

            // איפוס: חזרה להתחלה של החלון הנוכחי
            int resetPoint = _currentViewStart;
            if (_limitRangeStart != -1 && resetPoint < _limitRangeStart)
                resetPoint = _limitRangeStart;

            _playbackCurrentLine = resetPoint;
            MasterCursorHandler(resetPoint);
        }

        private void StopPlayback()
        {
            _isPlaying = false;
            _playTimer.Stop();

            PlayButton.Content = "▶ Play";
            PlayButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));

            // ביטול "מצב בנייה" - מציג את כל הגרף בחזרה
            foreach (var g in _registeredViews)
            {
                g.IsProgressiveMode = false;
            }
        }

        private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedCombo == null) return;
            var item = SpeedCombo.SelectedItem as ComboBoxItem;
            if (item != null && double.TryParse(item.Tag?.ToString(), out double s))
            {
                _playbackSpeed = s;
            }
        }

        // ---------------------------------------------------------
        //                     טעינת קבצים וניתוח
        // ---------------------------------------------------------

        private async void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV Files (*.csv)|*.csv" };
            if (dlg.ShowDialog() == true)
            {
                LoadingBar.Visibility = Visibility.Visible;
                string path = dlg.FileName;

                try
                {
                    // טעינה ברקע כדי לא לתקוע את הממשק
                    await Task.Run(() =>
                    {
                        if (_engine != null) _engine.Dispose();
                        _engine = new LogFileEngine();
                        _engine.Load(path);
                        _allSignalNames = _engine.ColumnNames;
                    });

                    _currentCsvPath = path;

                    // איפוס מסננים
                    if (CategoryFilter != null) CategoryFilter.SelectedIndex = 0;
                    ApplySignalFilter();

                    // ניתוח הנתונים (על ה-UI Thread כי זה מעדכן ObservableCollection)
                    AnalyzeStates();
                    MainStateTimeline.SetData(_globalStates, _engine.TotalRows);

                    // איפוס הגרפים
                    ActiveCharts.Clear();
                    _registeredViews.Clear();
                    CreateNewChart();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading file: {ex.Message}");
                }
                finally
                {
                    LoadingBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void AnalyzeStates()
        {
            _globalStates = new List<StateInterval>();
            EventsList.Clear();

            int stateColIndex = -1;
            // חיפוש אוטומטי של עמודת State
            var candidates = _engine.ColumnNames.Where(n => n.ToLower().Contains("state") && !n.ToLower().Contains("time")).ToList();
            if (candidates.Count > 0) stateColIndex = _engine.ColumnNames.IndexOf(candidates[0]);

            if (stateColIndex == -1) return;

            int limit = _engine.TotalRows;
            int currentStart = 0;
            int currentState = 0;

            if (limit > 3)
                currentState = StateConfig.GetId(_engine.GetStringAt(3, stateColIndex));

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
            // הגבלה כדי לא להעמיס על הרשימה אם יש מיליון שינויים
            if (EventsList.Count < 5000)
            {
                EventsList.Add(new StateEventItem
                {
                    LineIndex = lineIndex,
                    Time = GetTimeForIndex(lineIndex),
                    StateName = StateConfig.GetName(stateId),
                    Color = StateConfig.GetColor(stateId)
                });
            }
        }

        // ---------------------------------------------------------
        //                     ניהול גרפים
        // ---------------------------------------------------------

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
            foreach (var g in _registeredViews)
                g.SyncViewRange(start, end);
        }

        private void MasterCursorHandler(int cursor)
        {
            // אם אנחנו לא מנגנים, נעדכן את נקודת ההתחלה של הנגן למיקום העכבר
            if (!_isPlaying) _playbackCurrentLine = cursor;

            foreach (var g in _registeredViews)
                g.SyncCursor(cursor);
        }

        private void ComponentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_engine == null || ComponentList.SelectedItem == null) return;

            string name = ComponentList.SelectedItem.ToString();
            int idx = _engine.ColumnNames.IndexOf(name);
            if (idx == -1) return;

            // מציאת גרף פעיל
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

                // עדכון כותרת
                if (activeVm.Series.Count == 1) activeVm.Title = name;
                else activeVm.Title = string.Join(", ", activeVm.Series.Select(s => s.Name));

                if (activeVm.States == null) activeVm.States = _globalStates;

                // רענון
                var view = _registeredViews.FirstOrDefault(v => v.DataContext == activeVm);
                view?.SetViewModel(activeVm);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error adding signal: " + ex.Message);
            }
        }

        // פונקציית עזר לשליפת נתונים בטוחה
        private double[] ExtractDataColumn(int idx)
        {
            if (_engine == null || idx < 0) return new double[0];

            int limit = _engine.TotalRows;
            double[] data = new double[limit];

            for (int i = 0; i < limit; i++)
            {
                // דילוג על כותרות (בד"כ שורות ראשונות)
                if (i < 3)
                {
                    data[i] = double.NaN;
                    continue;
                }
                data[i] = _engine.GetValueAt(i, idx);
            }

            // תיקון חורים (Forward Fill)
            double last = double.NaN;
            for (int i = 0; i < limit; i++)
            {
                if (!double.IsNaN(data[i])) last = data[i];
                else if (!double.IsNaN(last)) data[i] = last;
            }
            return data;
        }

        // ---------------------------------------------------------
        //                     כפתורים ופונקציונליות
        // ---------------------------------------------------------

        private void SetTimeRange_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null) return;

            Window rangeWin = new Window
            {
                Title = "Set Range",
                Width = 300,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            StackPanel sp = new StackPanel { Margin = new Thickness(10) };

            sp.Children.Add(new TextBlock { Text = "Start Index:", Margin = new Thickness(0, 0, 0, 5) });
            TextBox txtStart = new TextBox { Text = (_currentViewStart).ToString(), Padding = new Thickness(5) };
            sp.Children.Add(txtStart);

            sp.Children.Add(new TextBlock { Text = "End Index:", Margin = new Thickness(0, 10, 0, 5) });
            TextBox txtEnd = new TextBox { Text = (_currentViewEnd).ToString(), Padding = new Thickness(5) };
            sp.Children.Add(txtEnd);

            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            Button btnOk = new Button { Content = "Apply", Width = 80, Margin = new Thickness(5), Padding = new Thickness(5) };

            btnOk.Click += (s, args) =>
            {
                if (int.TryParse(txtStart.Text, out int sVal) && int.TryParse(txtEnd.Text, out int eVal))
                {
                    if (sVal < eVal)
                    {
                        _limitRangeStart = Math.Max(0, sVal);
                        _limitRangeEnd = Math.Min(_engine.TotalRows, eVal);

                        MasterSyncHandler(_limitRangeStart, Math.Min(_limitRangeStart + 1000, _limitRangeEnd), -1);
                        rangeWin.DialogResult = true;
                    }
                    else MessageBox.Show("Invalid Range");
                }
            };

            Button btnClear = new Button { Content = "Clear", Width = 80, Margin = new Thickness(5), Padding = new Thickness(5) };
            btnClear.Click += (s, args) => { _limitRangeStart = -1; _limitRangeEnd = -1; rangeWin.DialogResult = true; };

            btns.Children.Add(btnClear);
            btns.Children.Add(btnOk);
            sp.Children.Add(btns);
            rangeWin.Content = sp;
            rangeWin.ShowDialog();
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
                        StringBuilder sb = new StringBuilder();
                        sb.Append("Line,Time");
                        foreach (var ser in vm.Series) sb.Append("," + ser.Name);
                        sb.AppendLine();

                        int startIdx = _currentViewStart;
                        int endIdx = Math.Min(_currentViewEnd, _engine.TotalRows);

                        for (int i = startIdx; i <= endIdx; i++)
                        {
                            sb.Append($"{i},{GetTimeForIndex(i)}");
                            foreach (var ser in vm.Series)
                            {
                                double val = (i < ser.Data.Length) ? ser.Data[i] : 0;
                                sb.Append($",{val}");
                            }
                            sb.AppendLine();
                        }
                        File.WriteAllText(dlg.FileName, sb.ToString());
                    }
                    catch (Exception ex) { MessageBox.Show("Export failed: " + ex.Message); }
                }
            }
        }

        private void Snapshot_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;

            // מציאת הפקד הגרפי המלא (הכרטיסייה)
            DependencyObject current = btn;
            while (current != null && !(current is Border && (current as Border).Style == FindResource("ChartCardStyle") as Style))
            {
                current = VisualTreeHelper.GetParent(current);
            }

            var cardBorder = current as Border;
            if (cardBorder == null) return;

            var dlg = new SaveFileDialog { Filter = "PNG Image (*.png)|*.png" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    double scale = 2.0;
                    int w = (int)(cardBorder.ActualWidth * scale);
                    int h = (int)(cardBorder.ActualHeight * scale);

                    RenderTargetBitmap renderBmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

                    DrawingVisual dv = new DrawingVisual();
                    using (DrawingContext dc = dv.RenderOpen())
                    {
                        VisualBrush vb = new VisualBrush(cardBorder);
                        dc.DrawRectangle(vb, null, new Rect(0, 0, cardBorder.ActualWidth, cardBorder.ActualHeight));
                        dc.PushTransform(new ScaleTransform(scale, scale));
                    }

                    cardBorder.Measure(new Size(cardBorder.ActualWidth, cardBorder.ActualHeight));
                    cardBorder.Arrange(new Rect(0, 0, cardBorder.ActualWidth, cardBorder.ActualHeight));

                    renderBmp.Render(cardBorder);

                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(renderBmp));

                    using (FileStream fs = new FileStream(dlg.FileName, FileMode.Create))
                    {
                        encoder.Save(fs);
                    }
                }
                catch (Exception ex) { MessageBox.Show("Snapshot failed: " + ex.Message); }
            }
        }

        // ---------------------------------------------------------
        //                     פונקציות עזר קטנות
        // ---------------------------------------------------------

        private void ApplySignalFilter()
        {
            if (SearchBox == null || CategoryFilter == null || _allSignalNames == null) return;

            string query = SearchBox.Text.ToLower();
            int catIdx = CategoryFilter.SelectedIndex;

            var filtered = _allSignalNames.Where(name =>
            {
                string n = name.ToLower();
                if (!string.IsNullOrWhiteSpace(query) && !n.Contains(query)) return false;

                if (catIdx == 1) return n.Contains("axis") || n.Contains("ax") || n.Contains("pos");
                if (catIdx == 2) return n.Contains("in") || n.Contains("out") || n.Contains("di") || n.Contains("do");
                if (catIdx == 3) return n.Contains("state") || n.Contains("mode");

                return true;
            }).ToList();

            ComponentList.ItemsSource = filtered;
        }

        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySignalFilter();
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySignalFilter();

        private void ToggleAxis_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Tag is SignalSeries ser)
            {
                ser.YAxisType = (ser.YAxisType == AxisType.Left) ? AxisType.Right : AxisType.Left;

                foreach (var chart in ActiveCharts)
                {
                    if (chart.Series.Contains(ser))
                    {
                        var view = _registeredViews.FirstOrDefault(v => v.DataContext == chart);
                        view?.SetViewModel(chart);
                        break;
                    }
                }
            }
        }

        private void FindValue_Click(object sender, RoutedEventArgs e)
        {
            var activeVm = ActiveCharts.FirstOrDefault(c => c.IsSelected);
            if (activeVm == null || activeVm.Series.Count == 0) { MessageBox.Show("Select a chart first."); return; }

            string input = Microsoft.VisualBasic.Interaction.InputBox("Value (>100 or 50):", "Find Value", "0");
            if (string.IsNullOrEmpty(input)) return;

            bool greater = input.StartsWith(">");
            double target;
            double.TryParse(greater ? input.Substring(1) : input, out target);

            var sig = activeVm.Series.First();
            for (int i = 0; i < sig.Data.Length; i++)
            {
                if ((greater && sig.Data[i] > target) || (!greater && Math.Abs(sig.Data[i] - target) < 0.01))
                {
                    int range = 1000;
                    MasterSyncHandler(Math.Max(0, i - range / 2), Math.Min(_engine.TotalRows, i + range / 2), -1);
                    foreach (var g in _registeredViews) g.SetTargetLine(i);
                    return;
                }
            }
            MessageBox.Show("Value not found.");
        }

        private void SaveLayout_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveCharts.Count == 0) return;
            var dlg = new SaveFileDialog { Filter = "Layout (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                var ws = new WorkspaceModel { SourceCsvPath = _currentCsvPath };
                foreach (var c in ActiveCharts)
                    ws.Charts.Add(new ChartSaveData
                    {
                        Title = c.Title,
                        Series = c.Series.Select(s => new SeriesSaveData { Name = s.Name, IsVisible = s.IsVisible, ColorHex = s.Color.ToString(), Axis = s.YAxisType }).ToList()
                    });
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(ws));
            }
        }

        private void LoadLayout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Layout (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                var ws = JsonSerializer.Deserialize<WorkspaceModel>(File.ReadAllText(dlg.FileName));
                if (!string.IsNullOrEmpty(ws.SourceCsvPath) && _currentCsvPath != ws.SourceCsvPath && File.Exists(ws.SourceCsvPath))
                {
                    if (_engine != null) _engine.Dispose();
                    _engine = new LogFileEngine(); _engine.Load(ws.SourceCsvPath);
                    _allSignalNames = _engine.ColumnNames;
                    _currentCsvPath = ws.SourceCsvPath;
                    AnalyzeStates();
                }

                ActiveCharts.Clear();
                _registeredViews.Clear();
                foreach (var cData in ws.Charts)
                {
                    var vm = new ChartViewModel { Title = cData.Title, States = _globalStates };
                    foreach (var sData in cData.Series)
                    {
                        int idx = _engine.ColumnNames.IndexOf(sData.Name);
                        if (idx != -1) vm.Series.Add(new SignalSeries { Name = sData.Name, Data = ExtractDataColumn(idx), Color = SKColor.Parse(sData.ColorHex), IsVisible = sData.IsVisible, YAxisType = sData.Axis });
                    }
                    ActiveCharts.Add(vm);
                }
            }
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("IndiChart Analytics Pro\n\n1. Load CSV data.\n2. Double click signals to add to charts.\n3. Use Playback to replay data.\n4. Use [L]/[R] to switch axis.");
        }

        private ChartViewModel CreateNewChart()
        {
            var vm = new ChartViewModel { States = _globalStates, Title = "Chart " + (ActiveCharts.Count + 1) };
            ActiveCharts.Add(vm);
            SelectChart(vm);
            return vm;
        }

        private void AddEmptyChart_Click(object sender, RoutedEventArgs e) => CreateNewChart();
        private void SelectChart(ChartViewModel vm) { foreach (var c in ActiveCharts) c.IsSelected = false; vm.IsSelected = true; }
        private void ChartBorder_MouseDown(object sender, MouseButtonEventArgs e) { if ((sender as Border)?.DataContext is ChartViewModel vm) SelectChart(vm); }
        private void ResetZoom_Click(object sender, RoutedEventArgs e) { if (_engine != null) MasterSyncHandler(0, _engine.TotalRows, -1); }
        private void RemoveChart_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is ChartViewModel vm) { ActiveCharts.Remove(vm); _registeredViews.RemoveAll(v => v.DataContext == vm); } }
        private void RemoveSeries_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Tag is SignalSeries s)
            {
                foreach (var c in ActiveCharts) if (c.Series.Contains(s)) { c.Series.Remove(s); var v = _registeredViews.FirstOrDefault(view => view.DataContext == c); v?.SetViewModel(c); break; }
            }
        }
        private void ShowStates_CheckedChanged(object sender, RoutedEventArgs e) { if (MainStateTimeline != null && _registeredViews != null) { bool s = ShowStatesCheck.IsChecked == true; foreach (var g in _registeredViews) g.SetShowStates(s); MainStateTimeline.Visibility = s ? Visibility.Visible : Visibility.Collapsed; } }
        private void EventsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) { var it = ((ListView)sender).SelectedItem as StateEventItem; if (it != null) { int r = 2000; MasterSyncHandler(Math.Max(0, it.LineIndex - r / 2), Math.Min(_engine.TotalRows, it.LineIndex + r / 2), -1); foreach (var g in _registeredViews) g.SetTargetLine(it.LineIndex); } }
        private void JumpToLine_Click(object sender, RoutedEventArgs e) { if (_engine != null && int.TryParse(JumpBox.Text, out int t)) { int r = 1000; MasterSyncHandler(Math.Max(0, t - r / 2), Math.Min(_engine.TotalRows, t + r / 2), -1); foreach (var g in _registeredViews) g.SetTargetLine(t); } }
        private void JumpBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) JumpToLine_Click(sender, e); }
        private string GetTimeForIndex(int i) => _engine?.GetStringAt(i, 0) ?? i.ToString();
    }
}