using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace IndiChart.UI
{
    public static class StateConfig
    {
        // 1. מיפוי שמות
        public static readonly Dictionary<int, string> StateNames = new Dictionary<int, string>
        {
            { 0, "UNDEFINED" }, { 1, "INIT" }, { 2, "POWER_DISABLE" }, { 3, "OFF" },
            { 4, "SERVICE" }, { 5, "MECH_INIT" }, { 6, "STANDBY" }, { 7, "GET_READY" },
            { 8, "READY" }, { 9, "PRE_PRINT" }, { 10, "PRINT" }, { 11, "POST_PRINT" },
            { 12, "PAUSE" }, { 13, "RECOVERY" }, { 14, "GO_TO_OFF" }, { 15, "GO_TO_STANDBY" },
            { 16, "GO_TO_SERVICE" }, { 17, "SML_OFF" }, { 18, "DYNAMIC_READY" }
        };

        // 2. מיפוי הפוך (שם -> מספר) לזיהוי חכם
        public static readonly Dictionary<string, int> StateNameToId;

        // אתחול המילון ההפוך
        static StateConfig()
        {
            StateNameToId = StateNames.ToDictionary(x => x.Value, x => x.Key);
        }

        // 3. מיפוי צבעים
        public static readonly Dictionary<int, SKColor> StateColors = new Dictionary<int, SKColor>
        {
            { 0, SKColor.Parse("#D3D3D3") },
            { 1, SKColor.Parse("#FFE135") },
            { 2, SKColor.Parse("#FF6B6B") },
            { 3, SKColor.Parse("#808080") },
            { 4, SKColor.Parse("#8B4513") },
            { 5, SKColor.Parse("#FFA500") },
            { 6, SKColor.Parse("#FFFF00") },
            { 7, SKColor.Parse("#FFA500") },
            { 8, SKColor.Parse("#90EE90") },
            { 9, SKColor.Parse("#26C6DA") },
            { 10, SKColor.Parse("#228B22") },
            { 11, SKColor.Parse("#4169E1") },
            { 12, SKColor.Parse("#FFA726") },
            { 13, SKColor.Parse("#EC407A") },
            { 17, SKColor.Parse("#C62828") },
            { 18, SKColor.Parse("#32CD32") }
        };

        public static SKColor GetColor(int stateId)
        {
            if (StateColors.TryGetValue(stateId, out SKColor color))
            {
                return color.WithAlpha(60); // שקיפות
            }
            return SKColors.Transparent;
        }

        public static string GetName(int stateId)
        {
            return StateNames.ContainsKey(stateId) ? StateNames[stateId] : stateId.ToString();
        }

        // פונקציית הזיהוי החכם
        public static int GetId(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) return 0;

            // נסה להמיר למספר ישירות
            if (int.TryParse(rawValue, out int id)) return id;

            string clean = rawValue.Trim().ToUpper();

            // נסה לחפש לפי שם מדויק
            if (StateNameToId.TryGetValue(clean, out int mappedId)) return mappedId;

            // נסה חיפוש חלקי (למקרה שהשם מופיע בתוך משפט)
            foreach (var kvp in StateNameToId)
            {
                if (clean.Contains(kvp.Key)) return kvp.Value;
            }

            return 0; // לא נמצא
        }
    }
}