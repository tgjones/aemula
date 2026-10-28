using Aemula.Emulation.Chips.VideoUla;

namespace Aemula.Tests.Emulation.Chips.VideoUla;

internal static class VideoUlaChipExtensions
{
    // One cycle of the 16MHz clock.
    public static void Tick(this VideoUlaChip ula)
    {
        ula.Clk16MHz = true;
        ula.Clk16MHz = false;
    }

    // A register write as the board makes it: the data on the bus, A0 set and
    // CS low across the 2MHz clock's rising edge.
    public static void Write(this VideoUlaChip ula, bool a0, byte value)
    {
        ula.A0 = a0;
        ula.Data = value;
        ula.CS = false;

        // Run to the next count of 4 in the divider, where the write lands.
        for (var i = 0; i < 16; i++)
        {
            ula.Tick();
        }

        ula.CS = true;
    }
}
