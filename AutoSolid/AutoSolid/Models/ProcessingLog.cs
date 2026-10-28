using System;

namespace AutoSolid.Models
{
    public enum LogType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class ProcessingLog
    {
        public DateTime Timestamp { get; } = DateTime.Now;
        public LogType Type { get; set; }
        public string Message { get; set; }

        public string DisplayText => $"[{Timestamp:HH:mm:ss}] [{Type.ToString().ToUpper()}] {Message}";
    }
}
