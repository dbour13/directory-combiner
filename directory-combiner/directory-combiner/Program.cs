using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using DokanNet;
using DokanNet.Logging;
using System.Collections.Generic;
using System.CommandLine.Invocation;
using DokanNetMirror;

namespace directory_combiner
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            var cmd = new RootCommand
            {
                new Option<string?>(
                  new string[] {"-md", "--mount-drive-letter" },
                  () => "N:\\",
                  "The drive letter to mount to."),
                new Option<IEnumerable<FolderMap>>(
                  new string[] {"-mf", "--mirror-folders" },
                  parseArgument: (val) => {
                      return val.Tokens.Select(t => {
                          string[] paths = t.Value.Split("|");
                          return new FolderMap(paths[0], paths[1]);
                      });
                  },
                  false,
                  "Folder to mirror in the virtual drive.  Can specify multiple.  Each should be in the format \"\\physical\\path\\to\\directory|\\virtual\\directory\".  Will mount C:\\ to \\ by default")
            };

            cmd.Handler = CommandHandler.Create<string, IEnumerable<FolderMap>>(HandleMount);

            return cmd.Invoke(args);
        }

        public static int HandleMount(string md, IEnumerable<FolderMap> mf)
        {
            try
            {
                using (var mre = new System.Threading.ManualResetEvent(false))
                using (var dokanNetLogger = new ConsoleLogger("[Mirror] "))
                {
                    Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
                    {
                        e.Cancel = true;
                        mre.Set();
                    };

                    var mirror = new Mirror(dokanNetLogger, mf ?? new List<FolderMap> { new FolderMap("C:\\", "\\") });

                    try
                    {
                        Dokan.Init();
                    }
                    catch (System.DllNotFoundException ex)
                    {
                        Console.WriteLine("Dolkan is not installed.  Please download it here: https://github.com/dokan-dev/dokany/releases");
                        Console.WriteLine(ex.Message);
                        return 1;
                    }

                    using (var dokanInstance = mirror.CreateFileSystem(md, DokanOptions.DebugMode | DokanOptions.EnableNotificationAPI))
                    //using (var notify = new Notify(folderMaps[0].MapFrom, mountPath, dokanInstance))
                    {
                        mre.WaitOne();
                    }

                    Dokan.Shutdown();
                }

                Console.WriteLine(@"Success");
            }
            catch (DokanException ex)
            {
                Console.WriteLine(@"Error: " + ex.Message);
            }

            return 0;
        }
    }
}