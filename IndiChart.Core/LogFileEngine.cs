using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Buffers.Text;
using System.Text;

namespace IndiChart.Core
{
    public class LogFileEngine : IDisposable
    {
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private unsafe byte* _ptr;
        private long _fileLength;
        private List<long> _lineOffsets;

        public List<string> ColumnNames { get; private set; }
        public int TotalRows => _lineOffsets.Count;

        public LogFileEngine()
        {
            _lineOffsets = new List<long>();
            ColumnNames = new List<string>();
        }

        public unsafe void Load(string filePath)
        {
            var info = new FileInfo(filePath);
            _fileLength = info.Length;

            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
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
            ParseHierarchicalHeader();
        }

        private unsafe void ParseHierarchicalHeader()
        {
            if (_lineOffsets.Count < 3) return;
            var line1 = ReadLineAsString(0).Split(',');
            var line2 = ReadLineAsString(1).Split(',');
            var line3 = ReadLineAsString(2).Split(',');

            int cols = line1.Length;
            ColumnNames.Clear();

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
            }
        }

        // שימוש פנימי לקריאת שורה מלאה
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

        // --- הפונקציה החדשה לקריאת טקסט מתא ספציפי ---
        public unsafe string GetStringAt(int rowIndex, int colIndex)
        {
            if (rowIndex >= _lineOffsets.Count) return "";

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
                        // המרה מ-Bytes ל-String ישירות
                        return Encoding.UTF8.GetString(slice.ToArray()).Trim();
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
        }
    }
}