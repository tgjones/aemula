using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74283ChipTests
{
    private static Ttl74283Chip Create(int a, int b, bool c0)
    {
        return new Ttl74283Chip
        {
            A1 = (a & 1) != 0,
            A2 = (a & 2) != 0,
            A3 = (a & 4) != 0,
            A4 = (a & 8) != 0,
            B1 = (b & 1) != 0,
            B2 = (b & 2) != 0,
            B3 = (b & 4) != 0,
            B4 = (b & 8) != 0,
            C0 = c0,
        };
    }

    private static int ReadSum(Ttl74283Chip chip)
    {
        return (chip.S1 ? 1 : 0) | (chip.S2 ? 2 : 0) | (chip.S3 ? 4 : 0) | (chip.S4 ? 8 : 0);
    }

    [Test]
    [Arguments(0, 0, false, 0, false)]
    [Arguments(3, 5, false, 8, false)]
    [Arguments(3, 5, true, 9, false)]
    [Arguments(15, 15, false, 14, true)]
    [Arguments(15, 15, true, 15, true)]
    [Arguments(7, 8, false, 15, false)]
    [Arguments(7, 9, false, 0, true)]
    public async Task AddsCorrectly(int a, int b, bool c0, int expectedSum, bool expectedCarry)
    {
        var chip = Create(a, b, c0);

        await Assert.That(ReadSum(chip)).IsEqualTo(expectedSum);
        await Assert.That(chip.C4).IsEqualTo(expectedCarry);
    }
}
