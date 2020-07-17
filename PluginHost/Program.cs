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
    class Program : IConsoleLogger
    {
        static Task Main(string[] args) => new Program().StartAsync();

        CancellationTokenSource TokenSource;
        IServiceProvider HandlerServices;

        public void Log(string format, params object[] args)
            => Console.WriteLine($"[{DateTime.Now:HH\\:mm\\:ss.fff}][{nameof(Program)}] {string.Format(format, args)}");

        public async Task StartAsync()
        {
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref TokenSource, cts, null) != null)
            {
                cts.Dispose();
                throw new InvalidOperationException("Started");
            }

            var cp = Console.OutputEncoding;
            if (cp != System.Text.Encoding.UTF8)
            {
                Log("Change output encoding to UTF8");
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }

            var pluginsFolder = Path.Combine(AppContext.BaseDirectory, "plugins");
            if (!Directory.Exists(pluginsFolder))
                Directory.CreateDirectory(pluginsFolder);

            var fileProvider = new PhysicalFileProvider(pluginsFolder, ExclusionFilters.Sensitive);

            Log($"Start host with plugins folder:\n'{pluginsFolder}'");

            HandlePluginsFolderChange(fileProvider, cts.Token);

            var watchPlugins = WatchPluginsFolderAsync(fileProvider, cts.Token);
            var handleInput = HandleConsoleInputAsync(cts);
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

        async Task WatchPluginsFolderAsync(IFileProvider fileProvider, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var changeToken = fileProvider.Watch("*.dll");
                var tcs = new TaskCompletionSource<object>();
                using (token.Register(() => tcs.TrySetCanceled()))
                {
                    var register = changeToken.RegisterChangeCallback(OnFileChangeCallback, tcs);
                    if (await tcs.Task.ContinueWith(t => t.IsCanceled).ConfigureAwait(false))
                    {
                        register.Dispose();
                        return;
                    }

                    await Task.Run(() => HandlePluginsFolderChange(fileProvider, token), token);
                }
            }
        }

        static void OnFileChangeCallback(object state) => ((TaskCompletionSource<object>)state).SetResult(null);

        void HandlePluginsFolderChange(IFileProvider fileProvider, CancellationToken token)
        {
            Log("Handle plugins folder change ...");

            var interfaceType = typeof(IConsoleInputHandler);
            var needRebuildServices = false;
            var pluginImplementTypes = new List<Type>();
            var loadedAssemblies = new HashSet<string>();
            var existsContexts = AssemblyLoadContext.All.OfType<CollectibleAssemblyLoadContext>().ToDictionary(c => c.FilePath);

            foreach (var file in fileProvider.GetDirectoryContents(string.Empty).Where(f => f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                CollectibleAssemblyLoadContext context;
                try
                {
                    var assemblyData = File.ReadAllBytes(file.PhysicalPath);
                    var checkSum = CollectibleAssemblyLoadContext.CalcCheckSum(assemblyData);
                    if (existsContexts.TryGetValue(file.PhysicalPath, out context))
                    {
                        if (checkSum.SequenceEqual(context.CheckSum))
                        {
                            loadedAssemblies.Add(context.FilePath);
                            pluginImplementTypes.AddRange(context.PluginClasses);
                            continue;
                        }

                        var removedPlugins = context.PluginClasses.Aggregate(", Removed plugins:", (str, type) => str + "\n* " + type.FullName);
                        Log($"Unload '{context.FilePath.CutFilePathBasedAppContext()}' plugins assembly with update{removedPlugins}");
                        context.Unload();
                    }

                    context = CollectibleAssemblyLoadContext.LoadFromAssemblyData(interfaceType, file.PhysicalPath, assemblyData, checkSum);
                }
                catch (Exception ex)
                {
                    Log($"Load '{file.PhysicalPath.CutFilePathBasedAppContext()}' plugins assembly faulted, will skip it: {ex.Message ?? ex.ToString()}");
                    continue;
                }

                loadedAssemblies.Add(context.FilePath);
                var addPlugins = ", Add plugins:";
                foreach (var type in context.PluginClasses)
                {
                    pluginImplementTypes.Add(type);
                    addPlugins += "\n* " + type.FullName;

                    needRebuildServices = true;
                }
                Log($"Load '{file.PhysicalPath.CutFilePathBasedAppContext()}' plugins assembly succeed{addPlugins}");
            }

            foreach (var pair in existsContexts.Where(p => !loadedAssemblies.Contains(p.Key)))
            {
                var context = pair.Value;
                var removedPlugins = context.PluginClasses.Aggregate(", Removed plugins:", (str, type) => str + "\n* " + type.FullName);
                Log($"Unload '{context.FilePath.CutFilePathBasedAppContext()}' plugins assembly with delete{removedPlugins}");
                context.Unload();

                needRebuildServices = true;
            }

            if (needRebuildServices)
            {
                var collection = new ServiceCollection();
                collection.AddSingleton<IConsoleLogger>(this);
                pluginImplementTypes.ForEach(type => collection.AddSingleton(interfaceType, type));
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

        CollectibleAssemblyLoadContext(string name) : base(name, isCollectible: true) { }

        public static byte[] CalcCheckSum(byte[] data) => System.Security.Cryptography.MD5.Create().ComputeHash(data);

        protected override Assembly Load(AssemblyName assemblyName) => null;

        public static CollectibleAssemblyLoadContext LoadFromAssemblyData(Type pluginType, string path, byte[] data, byte[] checkSum)
        {
            var filename = Path.GetFileNameWithoutExtension(path);
            var context = new CollectibleAssemblyLoadContext(filename); // will be add to AssemblyLoadContext.All
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(data));
                context.CheckSum = checkSum;
                context.FilePath = path;
                context.PluginClasses = assembly.GetTypes().Where(pluginType.IsAssignableFrom).ToArray();
                if (context.PluginClasses.Count == 0)
                    throw new TypeLoadException($"Not contains any '{pluginType}' interface implement.");
            }
            catch
            {
                context.Unload(); // unload for remove out from AssemblyLoadContext.All
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
