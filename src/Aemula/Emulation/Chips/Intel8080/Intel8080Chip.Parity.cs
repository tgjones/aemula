using System.Runtime.Intrinsics.X86;

namespace Aemula.Emulation.Chips.Intel8080;

partial class Intel8080Chip
{
    private static readonly bool[] ParityValues;

    static Intel8080Chip()
    {
        ParityValues = new bool[256];
        for (uint i = 0; i < ParityValues.Length; i++)
        {
            ParityValues[i] = (Popcnt.PopCount(i) % 2) == 0;
        }
    }
}
