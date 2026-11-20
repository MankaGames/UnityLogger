using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MankaGames
{
    public class MankaLogHandler : ILogHandler
    {
        private readonly ILogHandler _defaultLogHandler = Debug.unityLogger.logHandler;
        private readonly object _sync = new();
        private readonly string _filePath;
        private readonly string[] _prefsKeys;
        private readonly string _applicationVersion;
        private readonly string _deviceInfo;
        private readonly string _initialPrefsInfo;

        private readonly Queue<string> _duplicateQueue = new();   // eviction order
        private readonly HashSet<string> _duplicateSet = new();   // O(1) lookup

        private readonly List<string> _writeBuffer = new(256);
        private bool _lastEntryIsDuplicate;
        private bool _disposed;

        private int _rewriteLogCount;
        private StreamWriter _streamWriter;

        private const int MaxLogSizeMb = 10;
        private const long MaxLogSizeBytes = MaxLogSizeMb * 1024L * 1024L; // avoids Mathf.Pow at runtime
        private const int MaxBufferEntries = 10000;
        private const int MaxDuplicateBufferCount = 10;
        private const string DuplicateSymbol = "*";
        private const string PrefsKeyLastVersion = "last_app_version";
        private const int MaxReportLogSize = 200 * 1024; // 200 KB

        /// <param name="prefsKeys">Optional PlayerPrefs keys to dump in the system-info header.</param>
        public MankaLogHandler(string[] prefsKeys = null)
        {
            _prefsKeys = prefsKeys;
            _applicationVersion = Application.version;
            _filePath = Path.Combine(Application.persistentDataPath, Application.productName + ".log");

            CheckVersionAndReset();
            // Rotation can run on any logging thread. Read Unity's device info and prefs at startup.
            _deviceInfo = BuildDeviceInfo();
            _initialPrefsInfo = BuildPrefsInfo();

            var prevFile = File.Exists(_filePath);

            _streamWriter = new StreamWriter(new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            if (!prevFile)
            {
                _streamWriter.Write(BuildSystemInfo(_initialPrefsInfo));
                _streamWriter.Flush();
            }
            else
            {
                _streamWriter.WriteLine(BuildTimeStamp());
                _streamWriter.Flush();
            }

            Debug.unityLogger.logHandler = this;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                if (ReferenceEquals(Debug.unityLogger.logHandler, this))
                    Debug.unityLogger.logHandler = _defaultLogHandler;

                try
                {
                    FlushBufferedEntries();
                }
                finally
                {
                    _streamWriter.Dispose();
                }
            }
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string message = args == null || args.Length == 0 ? format ?? string.Empty : string.Format(format, args);
            lock (_sync)
            {
                if (!_disposed)
                    WriteToBuffer(logType, message);
            }

            _defaultLogHandler.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            lock (_sync)
            {
                if (!_disposed)
                    WriteToBuffer(LogType.Exception, exception.ToString());
            }

            _defaultLogHandler.LogException(exception, context);
        }

        public void FlushBuffer()
        {
            lock (_sync)
            {
                if (!_disposed)
                    FlushBufferedEntries();
            }
        }

        private void FlushBufferedEntries()
        {
            if (_writeBuffer.Count > 0)
            {
                TryResetFile();
                for (int i = 0; i < _writeBuffer.Count; i++)
                    _streamWriter.WriteLine(_writeBuffer[i]);
                _writeBuffer.Clear();
                _lastEntryIsDuplicate = false;
            }

            _streamWriter.Flush();
        }

        public string GetLogForReport()
        {
            lock (_sync)
            {
                if (!_disposed)
                    FlushBufferedEntries();

                return ReadLogForReport();
            }
        }

        private string ReadLogForReport()
        {
            if (!File.Exists(_filePath))
                return "no log file found";

            using var readFs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileLength = readFs.Length;

            if (fileLength <= MaxReportLogSize)
            {
                using var reader = new StreamReader(readFs);
                string content = reader.ReadToEnd();
                // Small file: return as-is (already contains the system-info header written on first launch).
                return string.IsNullOrEmpty(content) ? BuildSystemInfo(BuildPrefsInfo()) + "\n\n[Log file was empty]" : content;
            }

            // Large file: prepend a fresh header and append the last 200 KB.
            byte[] lastPartBytes = new byte[MaxReportLogSize];
            readFs.Seek(-MaxReportLogSize, SeekOrigin.End);
            int bytesRead = readFs.Read(lastPartBytes, 0, MaxReportLogSize);
            string lastPart = Encoding.UTF8.GetString(lastPartBytes, 0, bytesRead);

            int firstNewline = lastPart.IndexOf('\n');
            if (firstNewline != -1 && firstNewline < lastPart.Length - 1)
                lastPart = lastPart.Substring(firstNewline + 1);

            return BuildSystemInfo(BuildPrefsInfo()) + "\n\n[... Log truncated ...]\n\n" + lastPart;
        }

        // ── Private ───────────────────────────────────────────────────

        private void WriteToBuffer(LogType logType, string message)
        {
            if (_duplicateSet.Contains(message))
            {
                if (_lastEntryIsDuplicate && _writeBuffer.Count > 0)
                    _writeBuffer[_writeBuffer.Count - 1] += DuplicateSymbol;
                else
                {
                    _writeBuffer.Add(DuplicateSymbol);
                    _lastEntryIsDuplicate = true;
                }
            }
            else
            {
                _lastEntryIsDuplicate = false;
                _duplicateSet.Add(message);
                _duplicateQueue.Enqueue(message);
                if (_duplicateQueue.Count > MaxDuplicateBufferCount)
                    _duplicateSet.Remove(_duplicateQueue.Dequeue());

                _writeBuffer.Add(message);
                if (logType != LogType.Log)
                    _writeBuffer.Add(ParseStackTrace());
            }

            // Flush immediately on errors/exceptions so crashes don't lose context.
            // Also flush periodically to guard against SIGKILL on mobile.
            bool isError = logType == LogType.Error || logType == LogType.Exception || logType == LogType.Warning;
            if (isError || _writeBuffer.Count >= MaxBufferEntries)
                FlushBufferedEntries();
        }

        private string BuildTimeStamp() =>
            $"--------- ({_applicationVersion}) {DateTime.Now:yyyy-MM-dd HH:mm:ss} ------------";

        private string BuildDeviceInfo()
        {
            var sb = new StringBuilder(1024);

            sb.AppendLine($"--------- {Application.productName} {_applicationVersion} ------------");
            sb.AppendLine();

            sb.AppendLine("Device");
            sb.AppendLine($"  Type: {SystemInfo.deviceType}");
            sb.AppendLine($"  Model: {SystemInfo.deviceModel}");
            sb.AppendLine($"  Name: {SystemInfo.deviceName}");
            sb.AppendLine();

            sb.AppendLine("Operating system");
            sb.AppendLine($"  Family: {SystemInfo.operatingSystemFamily}");
            sb.AppendLine($"  Name: {SystemInfo.operatingSystem}");
            sb.AppendLine($"  System memory: {SystemInfo.systemMemorySize}");
            sb.AppendLine();

            sb.AppendLine("Graphics device");
            sb.AppendLine($"  Vendor: {SystemInfo.graphicsDeviceVendor}");
            sb.AppendLine($"  Name: {SystemInfo.graphicsDeviceName}");
            sb.AppendLine($"  Type: {SystemInfo.graphicsDeviceType}");
            sb.AppendLine($"  Version: {SystemInfo.graphicsDeviceVersion}");
            sb.AppendLine($"  Memory: {SystemInfo.graphicsMemorySize}");
            sb.AppendLine($"  Multi threaded: {SystemInfo.graphicsMultiThreaded}");
            sb.AppendLine($"  Shader level: {SystemInfo.graphicsShaderLevel}");
            sb.AppendLine();

            sb.AppendLine("Processor");
            sb.AppendLine($"  Type: {SystemInfo.processorType}");
            sb.AppendLine($"  Frequency: {SystemInfo.processorFrequency}");
            sb.AppendLine($"  Count: {SystemInfo.processorCount}");

            return sb.ToString();
        }

        private string BuildPrefsInfo()
        {
            if (_prefsKeys == null || _prefsKeys.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("--------- ----------- ------------");
            sb.AppendLine();
            sb.AppendLine("Player Prefs");
            foreach (var key in _prefsKeys)
                sb.AppendLine($"  {key}: {GetPrefsValue(key)}");
            sb.AppendLine();
            return sb.ToString();
        }

        private string BuildSystemInfo(string prefsInfo) =>
            $"{_deviceInfo}{Environment.NewLine}  Rewrite log file count: {_rewriteLogCount}" +
            $"{Environment.NewLine}{Environment.NewLine}{prefsInfo}" +
            $"--------- ----------- ------------{Environment.NewLine}{BuildTimeStamp()}{Environment.NewLine}";

        private void TryResetFile()
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length <= MaxLogSizeBytes)
                return;

            _rewriteLogCount++;
            _streamWriter.Dispose();
            _streamWriter = new StreamWriter(new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read));
            _streamWriter.Write(BuildSystemInfo(_initialPrefsInfo));
        }

        private string ParseStackTrace()
        {
            var stackTrace = StackTraceUtility.ExtractStackTrace();
            var lines = stackTrace.Split('\n');
            const int skipLoggerLines = 4;
            var sb = new StringBuilder(512);
            for (int i = skipLoggerLines; i < lines.Length; i++)
                sb.AppendLine($"  {lines[i]}");
            return sb.ToString();
        }

        /// <summary>
        /// Reads a PlayerPrefs value without knowing its type.
        /// Tries string → int → float, since GetString returns "" for numeric keys.
        /// </summary>
        private static string GetPrefsValue(string key)
        {
            if (!PlayerPrefs.HasKey(key))
                return "not set";
            string s = PlayerPrefs.GetString(key, "");
            if (s.Length > 0)
                return s;
            int i = PlayerPrefs.GetInt(key, int.MinValue);
            if (i != int.MinValue)
                return i.ToString();
            return PlayerPrefs.GetFloat(key, 0f).ToString("G");
        }

        private void CheckVersionAndReset()
        {
            string lastVersion = PlayerPrefs.GetString(PrefsKeyLastVersion, string.Empty);
            if (lastVersion == _applicationVersion)
                return;
            if (File.Exists(_filePath))
            {
                try { File.Delete(_filePath); }
                catch (Exception e) { Debug.LogException(e); }
            }
            PlayerPrefs.SetString(PrefsKeyLastVersion, _applicationVersion);
            PlayerPrefs.Save();
        }
    }
}
