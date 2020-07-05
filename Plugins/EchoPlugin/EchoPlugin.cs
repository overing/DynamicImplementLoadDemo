using System;
using System.Collections.Generic;
using PluginInterface;

namespace EchoPlugin
{
    public class EchoPlugin : IConsoleInputHandler
    {
        public string Prefix => "echo";

        public void Handle(string input)
        {
            if (input.Length <= "echo ".Length)
            {
                Console.WriteLine("echo command argument fault; use like 'echo message'");
                return;
            }
            Console.WriteLine(input.Substring("echo ".Length) + " (by echo plugin)");
        }
    }
}
