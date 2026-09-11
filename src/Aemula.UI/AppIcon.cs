using System.Reflection;
using System.Runtime.InteropServices;
using Hexa.NET.SDL3;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Aemula.UI;

// The window/taskbar icon, set per-window at runtime via SDL_SetWindowIcon
// (this is what shows up on Windows and Linux). On macOS this is skipped
// entirely: SDL_SetWindowIcon there doesn't set a per-window icon (macOS has
// no such concept) but instead replaces the app's Dock icon via
// NSApplication.setApplicationIconImage - stomping over the properly
// rounded/masked icon from the .app bundle's Info.plist with our raw square
// source PNG the moment the window is created. The same PNG is also embedded
// as a Win32 resource for the .exe icon via <ApplicationIcon> in the csproj.
public static class AppIcon
{
    public static unsafe void Apply(SDLWindowPtr window)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aemula.UI.logo.png")!;
        using var image = Image.Load<Rgba32>(stream);

        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);

        fixed (byte* pixelsPtr = pixels)
        {
            var surface = SDL.CreateSurfaceFrom(image.Width, image.Height, SDLPixelFormat.Abgr8888, pixelsPtr, image.Width * 4);
            if (surface.IsNull)
            {
                return;
            }

            SDL.SetWindowIcon(window, surface);
            SDL.DestroySurface(surface);
        }
    }
}
