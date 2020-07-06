using System;
using PluginInterface;

namespace EchoPlugin
{
    public class EchoPlugin : IConsoleInputHandler
    {
        const string LoggerName = nameof(EchoPlugin);

        static void Log(string format, params object[] args)
            => Console.WriteLine($"[{DateTime.Now:HH\\:mm\\:ss.fff}][{LoggerName}] {string.Format(format, args)}");

        public bool Handle(string input)
        {
            if (!input.StartsWith("echo", StringComparison.OrdinalIgnoreCase)) return false;
            if (input.Length <= "echo ".Length)
                Log("echo command argument fault; use like 'echo message'");
            else
                Log(input.Substring("echo ".Length) + " (by echo plugin)");
            return true;
        }
    }
}
