using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Output;

internal class TelevisionTests
{
    [Test]
    public async Task CanDecodeNtsc()
    {
        var ntscFilePath = Path.GetFullPath(Path.Combine("Emulation", "Output", "Assets", "smpte.ntsc"));
        var ntscBytes = File.ReadAllBytes(ntscFilePath);

        // NTSC signal format:
        //
        // Scanline:        63.5 µs (15.734 kHz)
        //   Blanking:        10.9 µs
        //     Front Porch:    1.5 µs at   0 IRE
        //     Sync Tip:       4.7 µs at -40 IRE, 0V
        //     Back Porch:     4.7 µs at   0 IRE
        //       Breezeway:    0.6 µs at   0 IRE
        //       Color Burst:  2.5 µs centred at 0 IRE, with 40 IRE peak-to-peak amplitude, 9 +- 1 cycles
        //     Active Video:   52.6 µs between 7.5 +- 2.5 and 100 IRE
        //
        // IRE:
        //   -40 IRE = -286mV
        //     0 IRE =    0mV
        //   100 IRE = +714mV
        //
        // Use a low-pass filter to separate the vertical sync.

        // If vsync is not detected, the vertical oscillator should free-run at ~59.94 Hz.

        const float ntscColorCarrierFrequency = 3_579_545f;
        const float ntscSamplesPerSecond = ntscColorCarrierFrequency * 4;
        const float ntscSamplesPerMicrosecond = ntscSamplesPerSecond / 1_000_000f;

        const int syncLevel = 4; // Sync level should be 0, but we allow some leeway.

        var syncSamples = 0;
        var foundHSync = false;
        var foundHSyncAtLeastOnce = false;

        var samples = new List<double>();

        for (var i = 0; i < ntscBytes.Length; i++)
        {
            var b = ntscBytes[i];

            await Assert.That(b).IsLessThanOrEqualTo((byte)200);

            samples.Add(b / 200.0f);

            if (b <= syncLevel)
            {
                syncSamples++;
            }

            // HSYNC is 4.7 microseconds long, +- 0.2 microseconds.
            const float hSyncNominalDurationInMicroseconds = 4.7f;
            const float hSyncToleranceInMicroseconds = 0.2f;
            const float hSyncMinimumDurationInMicroseconds = hSyncNominalDurationInMicroseconds - hSyncToleranceInMicroseconds;
            const int hSyncDuration = (int)(hSyncMinimumDurationInMicroseconds * ntscSamplesPerMicrosecond);
            if (syncSamples >= hSyncDuration)
            {
                if (foundHSyncAtLeastOnce)
                {
                    break;
                }
                foundHSync = true;
            }

            if (foundHSync && b > syncLevel)
            {
                foundHSyncAtLeastOnce = true;
                foundHSync = false;
                syncSamples = 0;
            }
        }

        var myPlot = new ScottPlot.Plot(4000, 600);
        myPlot.AddSignal(samples.ToArray());
        myPlot.SaveFig("signal.png");
    }

    [Test]
    public void CanDecodePal()
    {
        var wfmFilePath = Path.GetFullPath(Path.Combine("Emulation", "Output", "Assets", "nes.wmf"));
        var wmfFile = WfmFile.FromFile(wfmFilePath);
    }
}

internal class WfmFile
{
    public static WfmFile FromFile(string filePath)
    {
        using var fileStream = File.OpenRead(filePath);
        using var binaryReader = new BinaryReader(fileStream);

        return new WfmFile();
    }
}
