using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Output;

// Phase 0 of docs/television-plan.md. This file replaces an earlier
// [Skip]ped prototype (System.Drawing.Bitmap-based, didn't run on CI) - see
// the plan doc's "Testing" section for why it wasn't extended instead.
public class TelevisionTests
{
    [Test]
    public async Task SmpteAssetNormalizesToFullByteRange()
    {
        var normalized = SmpteAsset.LoadNormalized();

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
