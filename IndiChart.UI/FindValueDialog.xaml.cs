using System.Windows;

namespace IndiChart.UI
{
    public partial class FindValueDialog : Window
    {
        public string QueryText { get; private set; } = "";

        public FindValueDialog(string defaultValue = "")
        {
            InitializeComponent();
            QueryTextBox.Text = defaultValue;
            QueryTextBox.Focus();
            QueryTextBox.SelectAll();
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            QueryText = QueryTextBox.Text;
            DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }
    }
}
