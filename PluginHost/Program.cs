using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using PluginInterface;

namespace PluginHost
{
    class Program
    {
        static Task Main(string[] args) => new Program().StartAsync();

        PhysicalFileProvider FileProvider = new PhysicalFileProvider(AppContext.BaseDirectory, ExclusionFilters.Sensitive);
        CancellationTokenSource TokenSource;
        IServiceProvider HandlerServices;

        static void Log(string format, params object[] args)
            => Console.WriteLine($"[{DateTime.Now:HH\\:mm\\:ss.fff}][{nameof(Program)}] {string.Format(format, args)}");

        public async Task StartAsync()
        {
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref TokenSource, cts, null) != null)
                throw new InvalidOperationException("Started");

            var pluginsFolder = Path.Combine(AppContext.BaseDirectory, "plugins");
            if (!Directory.Exists(pluginsFolder))
                Directory.CreateDirectory(pluginsFolder);

            Log($"Start with plugins folder:\n'{pluginsFolder}'");

            HandlePluginsFolderChange(TokenSource.Token);

            var watchPlugins = WatchPluginsFolderAsync(TokenSource.Token);
            var handleInput = HandleConsoleInputAsync(TokenSource);
            await Task.WhenAny(watchPlugins, handleInput);
        }

        async Task HandleConsoleInputAsync(CancellationTokenSource tokenSources)
        {
            Console.Title = "(input 'quit' to exit this program)";
            while (!tokenSources.IsCancellationRequested)
            {
                var input = await Task.Run(() => Console.ReadLine());
                if (string.IsNullOrWhiteSpace(input)) continue;
                if (StringComparer.OrdinalIgnoreCase.Equals(input, "quit"))
                {
                    tokenSources.Cancel();
                    break;
                }

                bool commendExecuted = false;

                var services = Interlocked.CompareExchange(ref HandlerServices, null, null);
                if (services != null)
                {
                    foreach (var handler in services.GetServices<IConsoleInputHandler>())
                    {
                        if (handler.Handle(input))
                            commendExecuted |= true;
                    }
                }

                if (!commendExecuted)
                    Log($"Unknow command '{input}'.");
            }
        }

        async Task WatchPluginsFolderAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var changeToken = FileProvider.Watch("plugins/*.dll");
                var tcs = new TaskCompletionSource<object>();
                using (token.Register(() => tcs.TrySetCanceled()))
                {
                    var register = changeToken.RegisterChangeCallback(OnFileChangeCallback, tcs);
                    await tcs.Task.ConfigureAwait(false);

                    await Task.Run(() => HandlePluginsFolderChange(token), token);
                }
            }
        }

        static void OnFileChangeCallback(object state) => ((TaskCompletionSource<object>)state).SetResult(null);

        void HandlePluginsFolderChange(CancellationToken token)
        {
            Log($"Handle plugins folder change ...");

            var interfaceType = typeof(IConsoleInputHandler);
            var pluginTypes = new List<Type>();
            var needRebuildServices = false;
            var loadedAssemblies = new HashSet<string>();
            var existsContexts = AssemblyLoadContext.All.OfType<CollectibleAssemblyLoadContext>().ToArray();

            foreach (var file in FileProvider.GetDirectoryContents("plugins"))
            {
                if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                byte[] assemblyData;
                try
                {
                    assemblyData = File.ReadAllBytes(file.PhysicalPath);
                }
                catch (Exception ex)
                {
                    Log($"Read plugins data fault, will skip it: {ex.Message ?? ex.ToString()}");
                    continue;
                }

                var checkSum = CollectibleAssemblyLoadContext.CalcCheckSum(assemblyData);
                var context = existsContexts.FirstOrDefault(c => c.FilePath == file.PhysicalPath);
                if (context != null)
                {
                    if (checkSum.SequenceEqual(context.CheckSum))
                    {
                        loadedAssemblies.Add(context.FilePath);
                        continue;
                    }

                    var removedPlugins = context.PluginClasses.Aggregate("Removed plugins:", (str, clz) => str + "\n* " + clz.FullName);
                    Log($"Unload plugins assembly with update '{context.FilePath.Replace(AppContext.BaseDirectory, "")}', {removedPlugins}");
                    context.Unload();
                }

                try
                {
                    context = CollectibleAssemblyLoadContext.LoadFromAssemblyPathAsMemoryStream<IConsoleInputHandler>(file.PhysicalPath, assemblyData, checkSum);
                }
                catch (Exception ex)
                {
                    Log($"Load plugins assembly fault, will skip it: {ex.Message ?? ex.ToString()}");
                    continue;
                }
                loadedAssemblies.Add(context.FilePath);

                var addPlugins = "Add plugins:";
                foreach (var type in context.PluginClasses)
                {
                    if (!interfaceType.IsAssignableFrom(type)) continue;

                    pluginTypes.Add(type);
                    addPlugins += "\n* " + type.FullName;

                    needRebuildServices = true;
                }
                Log($"Load plugins assembly from '{file.PhysicalPath.CutFilePathBasedAppContext()}', {addPlugins}");
            }

            foreach (var context in existsContexts)
            {
                if (loadedAssemblies.Contains(context.FilePath)) continue;

                var removedPlugins = context.PluginClasses.Aggregate("Removed plugins:", (str, clz) => str + "\n* " + clz.FullName);
                Log($"Unload plugins assembly with delete '{context.FilePath.CutFilePathBasedAppContext()}', {removedPlugins}");
                context.Unload();

                needRebuildServices = true;
            }

            if (needRebuildServices)
            {
                var collection = new ServiceCollection();
                collection.AddSingleton(this);
                pluginTypes.ForEach(type => collection.AddSingleton(interfaceType, type));
                var services = collection.BuildServiceProvider(validateScopes: true);
                var original = Interlocked.Exchange(ref HandlerServices, services);
                (original as IDisposable)?.Dispose();
            }
            else
                Log("Not any plugin change.");
        }
    }

    class CollectibleAssemblyLoadContext : AssemblyLoadContext
    {
        public byte[] CheckSum { get; protected set; }
        public string FilePath { get; protected set; }
        public IReadOnlyCollection<Type> PluginClasses { get; protected set; }

        protected CollectibleAssemblyLoadContext(string name) : base(name, isCollectible: true) { }

        public static byte[] CalcCheckSum(byte[] data) => System.Security.Cryptography.MD5.Create().ComputeHash(data);

        protected override Assembly Load(AssemblyName assemblyName) => null;

        public static CollectibleAssemblyLoadContext LoadFromAssemblyPathAsMemoryStream<TPlugins>(string path, byte[] data, byte[] checkSum)
        {
            var pluginType = typeof(TPlugins);
            var filename = Path.GetFileNameWithoutExtension(path);
            var context = new CollectibleAssemblyLoadContext(filename);
            var assembly = context.LoadFromStream(new MemoryStream(data));
            try
            {
                context.CheckSum = checkSum;
                context.FilePath = path;
                context.PluginClasses = assembly.GetTypes().Where(pluginType.IsAssignableFrom).ToArray();
            }
            catch
            {
                context.Unload();
                throw;
            }
            return context;
        }
    }

    static class StringExtensions
    {
        public static string CutFilePathBasedAppContext(this string path)
        {
            var basePath = AppContext.BaseDirectory;
            return path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? path.Substring(basePath.Length) : path;
        }
    }
}
