
namespace PluginInterface
{
    public interface IConsoleLogger
    {
        void Log(string format, params object[] args);
    }

    public interface IConsoleInputHandler
    {
        bool Handle(string input);
    }
}
