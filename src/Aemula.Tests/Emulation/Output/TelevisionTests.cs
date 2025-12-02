using System;
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
        // Scanline:          63.5 µs (15.734 kHz)
        //   Blanking:        10.9 µs
        //     Front Porch:    1.5 µs at   0 IRE
        //     Sync Tip:       4.7 µs at -40 IRE, 0V
        //     Back Porch:     4.7 µs at   0 IRE
        //       Breezeway:    0.6 µs at   0 IRE
        //       Color Burst:  2.5 µs centred at 0 IRE, with 40 IRE peak-to-peak amplitude, 9 +- 1 cycles
        //   Active Video:    52.6 µs between 7.5 +- 2.5 and 100 IRE
        //
        // IRE:
        //   -40 IRE = -286mV
        //     0 IRE =    0mV
        //   100 IRE = +714mV
        //
        // Use a low-pass filter to separate the vertical sync.

        // If vsync is not detected, the vertical oscillator should free-run at a slightly lower frequency than ~59.94 Hz.

        const float ntscColorCarrierFrequency = 3_579_545f;
        const float ntscSamplesPerSecond = ntscColorCarrierFrequency * 4;
        const float ntscSamplesPerMicrosecond = ntscSamplesPerSecond / 1_000_000f;

        const int syncLevel = 4; // Sync level should be 0, but we allow some leeway.
        const int blankLevel = 40 / 140 * 200;

        // Implement vertical and horizontal oscillator, which will free-run if no sync is detected.

        var syncSamples = 0;
        var foundHSync = false;
        var foundVSync = false;

        var samples = new List<double>();

        var yPos = 0;
        var xPos = 0;

        var bitmap = new System.Drawing.Bitmap(2000, 2000);

        for (var i = 0; i < ntscBytes.Length; i++)
        {
            var b = ntscBytes[i];

            await Assert.That(b).IsLessThanOrEqualTo((byte)200);

            samples.Add(b / 200.0f);

            var isBelowSyncLevel = false;

            if (b <= syncLevel)
            {
                syncSamples++;
                isBelowSyncLevel = true;
            }

            var isBlanked = b < blankLevel;

            // HSYNC is 4.7 microseconds long, +- 0.2 microseconds.
            const float hSyncNominalDurationInMicroseconds = 4.7f;
            const float hSyncToleranceInMicroseconds = 0.2f;
            const float hSyncMinimumDurationInMicroseconds = hSyncNominalDurationInMicroseconds - hSyncToleranceInMicroseconds;
            const int hSyncDuration = (int)(hSyncMinimumDurationInMicroseconds * ntscSamplesPerMicrosecond);
            if (!isBelowSyncLevel && syncSamples >= hSyncDuration)
            {
                //if (foundHSyncAtLeastOnce)
                //{
                //    break;
                //}
                foundHSync = true;
                xPos = 0;
                yPos++;
            }

            // VSYNC
            const int vSyncDuration = 380; // TODO
            if (!isBelowSyncLevel && syncSamples >= vSyncDuration)
            {
                foundVSync = true;
                yPos = 0;
            }

            if (!isBelowSyncLevel)
            {
                syncSamples = 0;
            }

            if (!isBlanked)
            {
                // Active video.
                var y = (b - blankLevel) * 2; // Normalize to 0 IRE.
                y = Math.Clamp(y, 0, 255);

                var actualX = Math.Clamp(xPos, 0, bitmap.Width - 1);
                bitmap.SetPixel(actualX, yPos, System.Drawing.Color.FromArgb(255, y, y, y));
            }

            // TODO: If it's been a certain amount of time since the last horizontal sync, we should assume the horizontal sync is starting.
            // TODO: If it's been a certain amount of time since the last vertical sync, we should assume the vertical sync is starting.

            xPos++;
        }

        bitmap.Save("ntsc.png");

        //var myPlot = new ScottPlot.Plot(4000, 600);
        //myPlot.AddSignal(samples.ToArray());
        //myPlot.SaveFig("signal.png");
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
