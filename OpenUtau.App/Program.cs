﻿using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using Serilog;

namespace OpenUtau.App {
    public class Program {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        public static async Task Main(string[] args) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitLogging();
            await InitOpenUtau();
            Run(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();

        public static void InitInterop()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI()
                .SetupWithoutStarting();

        public static void Run(string[] args)
            => BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(
                    args, ShutdownMode.OnMainWindowClose);

        public static void InitLogging() {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Debug()
                .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day, encoding: Encoding.UTF8)
                .CreateLogger();
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler((sender, args) => {
                Log.Error((Exception)args.ExceptionObject, "Unhandled exception");
            });
        }

        public static async Task InitOpenUtau(string[] args = null!) {
            await Core.DocManager.Inst.Initialize(args);
        }

        public static Action? AutoUpdate { get;set; }
    }
}
