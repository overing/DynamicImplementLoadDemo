using System;
using PluginInterface;

namespace EchoPlugin
{
    public class EchoPlugin : IConsoleInputHandler
    {
        IConsoleLogger Logger;

        public EchoPlugin(IConsoleLogger logger) => Logger = logger;

        public bool Handle(string input)
        {
            if (!input.StartsWith("echo", StringComparison.OrdinalIgnoreCase)) return false;
            if (input.Length <= "echo ".Length)
                Logger.Log("echo command argument fault; use like 'echo message'");
            else
                Logger.Log(input.Substring("echo ".Length) + " (by echo plugin)");
            return true;
        }
    }
}
