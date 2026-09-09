using System.Numerics;

namespace Aemula.Emulation.Chips.Z80;

public sealed partial class Z80Chip
{
    /// <summary>
    /// Z80 parity lookup: <c>ParityTable[b]</c> is true when <c>b</c> has an even
    /// number of set bits (the Z80 sets P/V on even parity). Deliberately a
    /// separate table from the Intel 8080's identical-looking one - the two cores
    /// share no code.
    /// </summary>
    internal static readonly bool[] ParityTable = BuildParityTable();

    private static bool[] BuildParityTable()
    {
        var table = new bool[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (BitOperations.PopCount((uint)i) % 2) == 0;
        }
        return table;
    }
}
