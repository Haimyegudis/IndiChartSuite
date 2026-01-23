using System.Collections.Generic;

namespace IndiChart.UI
{
    public class ChartItem
    {
        // הכותרת של הגרף (שם הסיגנל)
        public string Title { get; set; }

        // הנתונים של הסיגנל הספציפי הזה
        public double[] Data { get; set; }

        // הסטייטים (צבעי הרקע) - משותפים לכולם
        public List<StateInterval> States { get; set; }
    }
}