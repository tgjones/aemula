using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;

namespace Aemula;

internal class Program
{
    public static int Main(string[] args)
    {
        //BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    //public static AppBuilder BuildAvaloniaApp() =>
    //    AppBuilder.Configure<App>()
    //        .UseReactiveUI()
    //        .UsePlatformDetect()
    //        .With(new AvaloniaNativePlatformOptions
    //        {
    //            RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
    //        })
    //        .LogToTrace();
}
