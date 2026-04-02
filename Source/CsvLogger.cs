using System;
using System.IO;

namespace NetData
{
    internal class CsvLogger : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _lock = new();
        private bool _disposed;

        public CsvLogger(string filename, string[] headers)
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, filename);
            bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            if (writeHeader)
                _writer.WriteLine(string.Join(",", headers));
        }

        public void WriteRow(params object[] values)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _writer.WriteLine(string.Join(",", values));
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _writer?.Dispose();
            }
        }
    }
}
