using System;

namespace Aemula.Emulation.Systems.BbcMicro;

// The board's monochrome composite output: IC41's fourth gate makes composite
// sync from the 6845's HS and VS, and the video ULA's red, green and blue
// outputs are summed. The resistor networks and the UHF modulator after
// them are analogue and reduced to levels.
//
// The colours reach Q7's base (_N418) through R117 (red, 2.2k), R118 (green,
// 1k) and R116 (blue, 3.9k), with R123 (470) to ground and R115 (1k) to +5V.
// The node is linear in the three inputs, so each colour adds a level in
// proportion to its conductance: green is weighted most and blue least,
// close to the eye's luma weights.
public sealed partial class BbcMicroSystem
{
    private const byte SyncTipByte = 0;
    private const byte BlackByte = 77;
    private const byte WhiteByte = 255;

    // Conductances of R117, R118 and R116 in millisiemens (1/kOhm), and their sum.
    private const double RedWeight = 1 / 2.2;
    private const double GreenWeight = 1 / 1.0;
    private const double BlueWeight = 1 / 3.9;
    private const double TotalWeight = RedWeight + GreenWeight + BlueWeight;

    // The byte most recently handed to Television.Decode.
    internal byte CompositeSampleForTest { get; private set; }

    // The summed output for each combination of red, green and blue (bit 0, 1
    // and 2), worked out once rather than on every tick.
    private static readonly byte[] LitSamples = BuildLitSamples();

    private static byte[] BuildLitSamples()
    {
        var samples = new byte[8];

        for (var rgb = 0; rgb < samples.Length; rgb++)
        {
            var lit = ((rgb & 1) != 0 ? RedWeight : 0)
                + ((rgb & 2) != 0 ? GreenWeight : 0)
                + ((rgb & 4) != 0 ? BlueWeight : 0);
            samples[rgb] = (byte)Math.Round(BlackByte + (WhiteByte - BlackByte) * lit / TotalWeight);
        }

        return samples;
    }

    private void TickCompositeVideo()
    {
        // CSYNC# is the NOR of HS and VS, so it is low while either pulse is
        // active.
        _ic41.A4 = Crtc.HSync;
        _ic41.B4 = Crtc.VSync;
        var sync = !_ic41.Y4;

        byte sample;

        if (sync)
        {
            // Sync pulls the summed output to its tip whatever the picture is
            // doing.
            sample = SyncTipByte;
        }
        else
        {
            sample = LitSamples[(VideoUla.Red ? 1 : 0) | (VideoUla.Green ? 2 : 0) | (VideoUla.Blue ? 4 : 0)];
        }

        CompositeSampleForTest = sample;
        Television.Decode(sample);
    }
}
