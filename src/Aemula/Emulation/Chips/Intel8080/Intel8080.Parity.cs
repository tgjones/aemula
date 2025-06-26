using System.Runtime.Intrinsics.X86;

namespace Aemula.Chips.Intel8080;

partial class Intel8080
{
    private static readonly bool[] ParityValues;

    static Intel8080()
    {
        ParityValues = new bool[256];
        for (uint i = 0; i < ParityValues.Length; i++)
        {
            ParityValues[i] = (Popcnt.PopCount(i) % 2) == 0;
        }
    }
}
