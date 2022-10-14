using System;

namespace OSBuilder.Utils
{
    public sealed class Logger
    {
        private static readonly Logger _instance = new Logger();

        private LogLevel _logLevel = LogLevel.INFO;
         
        public static Logger Instance { get { return _instance; } }

        static Logger() { }

        private Logger() { }

        private void Print(string message)
        {
            Console.WriteLine(message);
        }

        public void Debug(string message)
        {
            if (_logLevel > LogLevel.DEBUG)
                return;
            Print(message);
        }

        public void Info(string message)
        {
            if (_logLevel > LogLevel.INFO)
                return;
            Print(message);
        }
        
        public void Warning(string message)
        {
            if (_logLevel > LogLevel.WARNING)
                return;
            Print(message);
        }

        public void Error(string message)
        {
            Print(message);
        }

        public void SetLevel(LogLevel logLevel)
        {
            _logLevel = logLevel;
        }
    }
}
