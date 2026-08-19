using System.IO;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Output;

// Phase 0 of docs/television-plan.md. This file replaces an earlier
// [Skip]ped prototype (System.Drawing.Bitmap-based, didn't run on CI) - see
// the plan doc's "Testing" section for why it wasn't extended instead.
public class TelevisionTests
{
    // smpte.ntsc's raw bytes are on a 0-200 scale - its own capture's own
    // calibration - not the 0-255 scale Television expects (byte 0 = 0V
    // sync tip, byte 255 = white, matching AppleIISystem.CompositeVideo's
    // own encoder scale - see the plan doc's "Input signal contract").
    // Rescaling once here, at the point the asset is loaded, keeps
    // Television itself agnostic to the fact that two differently-
    // calibrated producers exist.
    private static byte[] LoadNormalizedSmpteAsset()
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

    [Test]
    public async Task SmpteAssetNormalizesToFullByteRange()
    {
        var normalized = LoadNormalizedSmpteAsset();

        // 955,500 bytes at 910 samples/line (63.5µs at exactly 4x the NTSC
        // color subcarrier) is exactly 1050 lines, i.e. 525 lines x 2 fields
        // - see the plan doc's "Existing state" section.
        await Assert.That(normalized.Length).IsEqualTo(955_500);

        // The raw asset's actual range is [4, 199] (confirmed by inspection,
        // not assumed) - rescaled by *255/200 with integer truncation, that
        // becomes [5, 253]. Asserting the exact rescaled extremes (rather
        // than just "some values changed") catches an off-by-one in the
        // rescale formula, not just its general direction.
        var min = normalized[0];
        var max = normalized[0];

        foreach (var sample in normalized)
        {
            if (sample < min) min = sample;
            if (sample > max) max = sample;
        }

        await Assert.That(min).IsEqualTo((byte)5);
        await Assert.That(max).IsEqualTo((byte)253);
    }
}
