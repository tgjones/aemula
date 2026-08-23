using System.IO;

namespace Aemula.Tests.Emulation.Output;

internal static class SmpteAsset
{
    // smpte.ntsc's raw bytes are on a 0-200 scale - its own capture's own
    // calibration - not the 0-255 scale Television expects (byte 0 = 0V
    // sync tip, byte 255 = white, matching AppleIISystem.CompositeVideo's
    // own encoder scale - see docs/television-plan.md's "Input signal
    // contract"). Rescaling once here, at the point the asset is loaded,
    // keeps Television itself agnostic to the fact that two differently-
    // calibrated producers exist.
    public static byte[] LoadNormalized()
    {
        var filePath = Path.GetFullPath(Path.Combine("Emulation", "Output", "Assets", "smpte.ntsc"));
        var rawBytes = File.ReadAllBytes(filePath);

        var normalized = new byte[rawBytes.Length];
        for (var i = 0; i < rawBytes.Length; i++)
        {
            normalized[i] = (byte)(rawBytes[i] * 255 / 200);
        }

        return normalized;
    }
}
