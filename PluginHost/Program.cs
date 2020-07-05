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

        public async Task StartAsync()
        {
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref TokenSource, cts, null) != null)
                throw new InvalidOperationException("Started");

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
                if (input == null) continue;
                if (StringComparer.OrdinalIgnoreCase.Equals(input, "quit"))
                {
                    tokenSources.Cancel();
                    break;
                }

                var services = Interlocked.CompareExchange(ref HandlerServices, null, null);
                if (services == null) continue;

                bool commendExecuted = false;

                foreach (var handler in services.GetServices<IConsoleInputHandler>())
                {
                    if (!input.StartsWith(handler.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    handler.Handle(input);

                    commendExecuted = true;
                }

                if (!commendExecuted)
                    Console.WriteLine($"Command '{input}' not execute.");
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
            Console.WriteLine($"Handle plugins folder change ...");

            var interfaceType = typeof(IConsoleInputHandler);

            var collection = new ServiceCollection();
            collection.AddSingleton(this);

            bool needRebuildServices = false;

            var existsContexts = AssemblyLoadContext.All.OfType<CollectibleAssemblyLoadContext<IConsoleInputHandler>>().ToArray();

            var loadedAssemblies = new HashSet<string>();

            foreach (var file in FileProvider.GetDirectoryContents("plugins"))
            {
                if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                var exists = existsContexts.FirstOrDefault(c => c.FilePath == file.PhysicalPath);
                if (exists == null)
                {
                    var data = File.ReadAllBytes(file.PhysicalPath);
                    var check = CollectibleAssemblyLoadContext<IConsoleInputHandler>.CalcCheckSum(data);
                    try
                    {
                        exists = CollectibleAssemblyLoadContext<IConsoleInputHandler>.LoadFromAssemblyPathAsMemoryStream(file.PhysicalPath, check);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Load plugin assembly fault, will skip it: {ex}");
                        continue;
                    }
                    loadedAssemblies.Add(exists.FilePath);
                }
                else
                {
                    var data = File.ReadAllBytes(file.PhysicalPath);
                    var check = CollectibleAssemblyLoadContext<IConsoleInputHandler>.CalcCheckSum(data);
                    if (check.SequenceEqual(exists.CheckSum))
                    {
                        loadedAssemblies.Add(exists.FilePath);
                        continue;
                    }

                    foreach (var type in exists.PluginClasses)
                        Console.WriteLine($"Remove IConsoleInputandler '{type.FullName}'");

                    Console.WriteLine($"Unload context with update '{exists.FilePath.Replace(AppContext.BaseDirectory, "")}'");
                    exists.Unload();

                    try
                    {
                        exists = CollectibleAssemblyLoadContext<IConsoleInputHandler>.LoadFromAssemblyPathAsMemoryStream(file.PhysicalPath, check);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Load plugin assembly fault, will skip it: {ex}");
                        continue;
                    }
                    loadedAssemblies.Add(exists.FilePath);
                }

                var path = file.PhysicalPath;
                if (path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                    path = path.Substring(AppContext.BaseDirectory.Length);
                Console.WriteLine($"Load plugins assembly from '{path}'");

                foreach (var type in exists.PluginClasses)
                {
                    if (!interfaceType.IsAssignableFrom(type)) continue;

                    collection.AddSingleton(interfaceType, type);
                    Console.WriteLine($"Add IConsoleInputandler '{type.FullName}'");
                    needRebuildServices = true;
                }
            }

            foreach (var context in existsContexts)
            {
                if (loadedAssemblies.Contains(context.FilePath)) continue;

                var path = context.FilePath;
                if (path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                    path = path.Substring(AppContext.BaseDirectory.Length);
                Console.WriteLine($"Unload context with delete '{path}'");
                context.Unload();
                needRebuildServices = true;
            }

            if (needRebuildServices)
            {
                var services = collection.BuildServiceProvider(validateScopes: true);
                var original = Interlocked.Exchange(ref HandlerServices, services);
                (original as IDisposable)?.Dispose();
            }
            else
                Console.WriteLine("Not any plugin change.");
        }
    }

    public class CollectibleAssemblyLoadContext<TPlugins> : AssemblyLoadContext
    {
        public Type PluginType = typeof(TPlugins);
        public IReadOnlyCollection<Type> PluginClasses { get; private set; }
        public byte[] CheckSum { get; private set; }
        public string FilePath { get; private set; }

        CollectibleAssemblyLoadContext(string name) : base(name, isCollectible: true) { }

        protected override Assembly Load(AssemblyName assemblyName) => null;

        public static byte[] CalcCheckSum(byte[] data) => System.Security.Cryptography.MD5.Create().ComputeHash(data);

        public static CollectibleAssemblyLoadContext<TPlugins> LoadFromAssemblyPathAsMemoryStream(string path, byte[] checkSum)
        {
            var data = File.ReadAllBytes(path);
            var filename = Path.GetFileNameWithoutExtension(path);
            var context = new CollectibleAssemblyLoadContext<TPlugins>(filename);
            var assembly = context.LoadFromStream(new MemoryStream(data));
            try
            {
                context.CheckSum = checkSum;
                context.FilePath = path;
                context.PluginClasses = assembly.GetTypes().Where(context.PluginType.IsAssignableFrom).ToArray();
            }
            catch
            {
                context.Unload();
                throw;
            }
            return context;
        }
    }
}
