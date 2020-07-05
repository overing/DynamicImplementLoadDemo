using System;
using System.Collections.Generic;

namespace PluginInterface
{
    public interface IConsoleInputHandler
    {
        string Prefix { get; }
        void Handle(string input);
    }
}
