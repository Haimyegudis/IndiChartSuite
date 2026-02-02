using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Buffers.Text;
using System.Text;
using System.Linq;

namespace IndiChart.Core
{
    public enum CsvFormat
    {
        Unknown,
        PlcIos,      // Single header line with Data.OPCUAInterface... or Time,PolicyName columns
        YTScope,     // 5-line header (metadata, Name, SymbolComment, Data-Type, SampleTime)
        Legacy       // 3-line hierarchical header format
    }

    public class LogFileEngine : IDisposable
    {
        private FileStream _fileStream;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private unsafe byte* _ptr;
        private long _fileLength;
        private List<long> _lineOffsets;

        public List<string> ColumnNames { get; private set; }
        public List<string> RawColumnNames { get; private set; }
        public int TotalRows => _lineOffsets.Count;
        public int DataStartRow { get; private set; } = 3;
        public CsvFormat DetectedFormat { get; private set; } = CsvFormat.Unknown;

        public LogFileEngine()
        {
            _lineOffsets = new List<long>();
            ColumnNames = new List<string>();
            RawColumnNames = new List<string>();
        }

        public unsafe void Load(string filePath)
        {
            var info = new FileInfo(filePath);
            _fileLength = info.Length;

            // Open file with read-only access and allow sharing with other processes (e.g., OneDrive, Excel)
            _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            _accessor = _mmf.CreateViewAccessor(0, _fileLength, MemoryMappedFileAccess.Read);

            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _ptr = ptr;

            ParseStructure();
        }

        private unsafe void ParseStructure()
        {
            _lineOffsets.Clear();
            long currentOffset = 0;
            while (currentOffset < _fileLength)
            {
                _lineOffsets.Add(currentOffset);
                while (currentOffset < _fileLength && _ptr[currentOffset] != (byte)'\n')
                {
                    currentOffset++;
                }
                currentOffset++;
            }

            // Detect format
            DetectedFormat = DetectFormat();

            switch (DetectedFormat)
            {
                case CsvFormat.YTScope:
                    ParseYTScopeFormat();
                    break;
                case CsvFormat.PlcIos:
                    ParsePlcIosFormat();
                    break;
                case CsvFormat.Legacy:
                default:
                    ParseHierarchicalHeader();
                    break;
            }
        }

        private CsvFormat DetectFormat()
        {
            if (_lineOffsets.Count < 2) return CsvFormat.Legacy;

            string firstLine = ReadLineAsString(0);
            string secondLine = _lineOffsets.Count > 1 ? ReadLineAsString(1) : "";

            // Check for YT Scope format: first line starts with "Name,YT Scope Project"
            if (firstLine.StartsWith("Name,YT Scope Project") || firstLine.StartsWith("Name,YT Scope"))
            {
                return CsvFormat.YTScope;
            }

            // Check for PLC-IOS format: has timestamp in second line with T and : (ISO 8601)
            // or first line has Time,PolicyName columns or Data.OPCUAInterface
            if (firstLine.Contains("PolicyName") || firstLine.Contains("Data.OPCUAInterface") ||
                firstLine.Contains("Unix_Time") || firstLine.Contains("Machine_State") ||
                (secondLine.Contains("T") && secondLine.Contains(":") && secondLine.Contains("-")))
            {
                return CsvFormat.PlcIos;
            }

            // Default: Legacy 3-line header format
            return CsvFormat.Legacy;
        }

        private void ParsePlcIosFormat()
        {
            // Single header line format (PLC-IOS or similar)
            DataStartRow = 1;
            var line = ReadLineAsString(0).Split(',');
            ColumnNames.Clear();
            RawColumnNames.Clear();

            for (int i = 0; i < line.Length; i++)
            {
                string raw = line[i].Trim().Trim('"');
                RawColumnNames.Add(raw);

                // Simplify long OPC-UA style names
                string[] parts = raw.Split('.');
                if (parts.Length > 4)
                {
                    ColumnNames.Add(string.Join(".", parts.Skip(parts.Length - 4)));
                }
                else
                {
                    ColumnNames.Add(raw);
                }
            }
        }

        private void ParseYTScopeFormat()
        {
            // YT Scope format has multiple header lines:
            // Line 0: Name,YT Scope Project,
            // Line 1: File,path,...
            // ...
            // Line N: Name,column1,column2,... (actual column names)
            // Line N+1: SymbolComment,...
            // Line N+2: Data-Type,...
            // Line N+3: SampleTime[ms],...
            // Line N+4+: data

            DataStartRow = 9; // Default, will be updated

            // Find the line that starts with "Name," and contains actual column names
            int nameLineIndex = -1;
            for (int i = 0; i < Math.Min(10, _lineOffsets.Count); i++)
            {
                string line = ReadLineAsString(i);
                // The actual column names line starts with "Name," followed by signal names
                if (line.StartsWith("Name,") && (line.Contains("Station.") || line.Contains("gStation") || line.Contains("arrInk")))
                {
                    nameLineIndex = i;
                    break;
                }
            }

            if (nameLineIndex == -1)
            {
                // Fallback: look for the line with the most commas (likely column headers)
                int maxCommas = 0;
                for (int i = 0; i < Math.Min(10, _lineOffsets.Count); i++)
                {
                    string line = ReadLineAsString(i);
                    int commaCount = line.Count(c => c == ',');
                    if (commaCount > maxCommas)
                    {
                        maxCommas = commaCount;
                        nameLineIndex = i;
                    }
                }
            }

            // Find the SampleTime line to determine where data starts
            for (int i = 0; i < Math.Min(15, _lineOffsets.Count); i++)
            {
                string line = ReadLineAsString(i);
                if (line.StartsWith("SampleTime"))
                {
                    DataStartRow = i + 1;
                    break;
                }
            }

            // Parse column names from the name line
            ColumnNames.Clear();
            RawColumnNames.Clear();

            if (nameLineIndex >= 0)
            {
                var cols = ReadLineAsString(nameLineIndex).Split(',');
                for (int i = 0; i < cols.Length; i++)
                {
                    string raw = cols[i].Trim().Trim('"');
                    RawColumnNames.Add(raw);

                    // Simplify YT Scope names
                    string simplified = SimplifyYTScopeName(raw);
                    ColumnNames.Add(simplified);
                }
            }
        }

        private string SimplifyYTScopeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            string result = raw;

            // Handle Station.pArr* or gStation* patterns
            if (result.StartsWith("Station.pArr"))
            {
                result = result.Substring("Station.pArr".Length);
            }
            else if (result.StartsWith("gStationAxes_"))
            {
                result = result.Replace("gStationAxes_", "Stn");
            }
            else if (result.StartsWith("arrInk["))
            {
                result = "Ink" + result.Substring("arrInk".Length);
            }

            // Remove ^. (pointer dereference notation)
            result = result.Replace("^.", ".");

            return result;
        }

        private unsafe void ParseHierarchicalHeader()
        {
            if (_lineOffsets.Count < 3) return;
            DataStartRow = 3;

            var line1 = ReadLineAsString(0).Split(',');
            var line2 = ReadLineAsString(1).Split(',');
            var line3 = ReadLineAsString(2).Split(',');

            int cols = line1.Length;
            ColumnNames.Clear();
            RawColumnNames.Clear();

            for (int i = 0; i < cols; i++)
            {
                string p1 = (i < line1.Length) ? line1[i].Trim() : "";
                string p2 = (i < line2.Length) ? line2[i].Trim() : "";
                string p3 = (i < line3.Length) ? line3[i].Trim() : "";

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(p1)) parts.Add(p1);
                if (!string.IsNullOrWhiteSpace(p2)) parts.Add(p2);
                if (!string.IsNullOrWhiteSpace(p3)) parts.Add(p3);

                string fullName = string.Join("_", parts);
                if (string.IsNullOrWhiteSpace(fullName)) fullName = $"Column_{i}";

                ColumnNames.Add(fullName);
                RawColumnNames.Add(fullName);
            }
        }

        private unsafe string ReadLineAsString(int index)
        {
            if (index >= _lineOffsets.Count) return "";
            long start = _lineOffsets[index];
            long end = (index + 1 < _lineOffsets.Count) ? _lineOffsets[index + 1] - 1 : _fileLength;
            if (end > start && _ptr[end - 1] == '\r') end--;

            int len = (int)(end - start);
            if (len <= 0) return "";

            byte[] buffer = new byte[len];
            Marshal.Copy((IntPtr)(_ptr + start), buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }

        public unsafe string GetStringAt(int rowIndex, int colIndex)
        {
            if (rowIndex >= _lineOffsets.Count) return "";

            long start = _lineOffsets[rowIndex];
            long end = (rowIndex + 1 < _lineOffsets.Count) ? _lineOffsets[rowIndex + 1] - 1 : _fileLength;
            if (end > start && _ptr[end - 1] == '\r') end--;

            int len = (int)(end - start);
            ReadOnlySpan<byte> lineSpan = new ReadOnlySpan<byte>(_ptr + start, len);

            // Handle CSV with quoted fields containing commas
            int current = 0;
            int lastComma = -1;
            bool inQuotes = false;

            for (int i = 0; i <= lineSpan.Length; i++)
            {
                if (i < lineSpan.Length && lineSpan[i] == (byte)'"')
                {
                    inQuotes = !inQuotes;
                }

                if (i == lineSpan.Length || (lineSpan[i] == (byte)',' && !inQuotes))
                {
                    if (current == colIndex)
                    {
                        var slice = lineSpan.Slice(lastComma + 1, i - lastComma - 1);
                        return Encoding.UTF8.GetString(slice.ToArray()).Trim().Trim('"');
                    }
                    current++;
                    lastComma = i;
                }
            }
            return "";
        }

        public unsafe double GetValueAt(int rowIndex, int colIndex)
        {
            if (rowIndex >= _lineOffsets.Count) return double.NaN;

            long start = _lineOffsets[rowIndex];
            long end = (rowIndex + 1 < _lineOffsets.Count) ? _lineOffsets[rowIndex + 1] - 1 : _fileLength;
            if (end > start && _ptr[end - 1] == '\r') end--;

            int len = (int)(end - start);
            ReadOnlySpan<byte> lineSpan = new ReadOnlySpan<byte>(_ptr + start, len);

            int current = 0;
            int lastComma = -1;

            for (int i = 0; i <= lineSpan.Length; i++)
            {
                if (i == lineSpan.Length || lineSpan[i] == (byte)',')
                {
                    if (current == colIndex)
                    {
                        var slice = lineSpan.Slice(lastComma + 1, i - lastComma - 1);
                        if (Utf8Parser.TryParse(slice, out double val, out _))
                        {
                            return val;
                        }
                        return double.NaN;
                    }
                    current++;
                    lastComma = i;
                }
            }
            return double.NaN;
        }

        public void Dispose()
        {
            if (_accessor != null) _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor?.Dispose();
            _mmf?.Dispose();
            _fileStream?.Dispose();
        }
    }
}
