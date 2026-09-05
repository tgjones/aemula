using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.AppleI;

// The analog composite video summing stage (Q5 + R1/R2/R12 on the
// schematic). Monochrome: no color-burst gate exists on this board at all
// (confirmed on the schematic - there's no color-subcarrier signal
// anywhere on the terminal-section sheet), so this is a two-input (video
// bit, sync bit) table rather than AppleII's three-input (video, sync,
// burst) one - sync always
// wins (pulls to black level regardless of video), matching how a real sync
// pulse blanks the beam regardless of what the video data line is doing at
// the same instant. The exact resistor-weighted voltage levels Apple II's
// equivalent file derives from Gayler's measurements weren't available for
// this board, so this uses the same sync=0/blanking=64 byte landmarks
// (shared with every other producer's scale in this codebase) with white
// simply at 255, rather than a derived intermediate curve.
public sealed partial class AppleISystem
{
    private const byte SyncByte = 0;
    private const byte BlankByte = 64;
    private const byte WhiteByte = 255;

    private void TickCompositeVideo()
    {
        var sample = CompositeSync ? SyncByte : (VideoBit ? WhiteByte : BlankByte);

        Television.Decode(sample);
    }
}
