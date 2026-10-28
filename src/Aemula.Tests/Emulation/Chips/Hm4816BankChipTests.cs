using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Hm4816BankChipTests
{
    private static void Cycle(Hm4816BankChip chip, byte row, byte column, bool weBar, byte din = 0)
    {
        chip.Address = row;
        chip.RasBar = false;
        chip.Address = column;
        chip.WeBar = weBar;
        chip.Din = din;
        chip.CasBar = false;
    }

    private static void End(Hm4816BankChip chip)
    {
        chip.CasBar = true;
        chip.RasBar = true;
        chip.WeBar = true;
    }

    [Test]
    public async Task WriteThenReadReturnsTheByte()
    {
        var chip = new Hm4816BankChip();

        Cycle(chip, row: 0x15, column: 0x2A, weBar: false, din: 0xA5);
        await Assert.That(chip.Dout).IsNull();
        End(chip);

        Cycle(chip, row: 0x15, column: 0x2A, weBar: true);
        await Assert.That(chip.Dout).IsEqualTo((byte)0xA5);
        End(chip);

        await Assert.That(chip.Peek((0x2A << 7) | 0x15)).IsEqualTo((byte)0xA5);
    }

    [Test]
    public async Task RowAndColumnAddressDifferentCells()
    {
        var chip = new Hm4816BankChip();

        Cycle(chip, row: 1, column: 2, weBar: false, din: 0xFF);
        End(chip);

        Cycle(chip, row: 2, column: 1, weBar: true);
        await Assert.That(chip.Dout).IsEqualTo((byte)0);
        End(chip);
    }

    [Test]
    public async Task OutputIsHighImpedanceOutsideCas()
    {
        var chip = new Hm4816BankChip();

        await Assert.That(chip.Dout).IsNull();

        Cycle(chip, row: 0, column: 0, weBar: true);
        await Assert.That(chip.Dout).IsEqualTo((byte)0);

        chip.CasBar = true;
        await Assert.That(chip.Dout).IsNull();
    }

    [Test]
    public async Task AddressIsLatchedOnTheStrobeEdges()
    {
        var chip = new Hm4816BankChip();

        chip.Address = 5;
        chip.RasBar = false;
        chip.Address = 9;
        chip.WeBar = false;
        chip.Din = 0x81;
        chip.CasBar = false;

        // Moving the address pins afterwards changes nothing.
        chip.Address = 0x7F;
        End(chip);

        await Assert.That(chip.Peek((9 << 7) | 5)).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task CasWithoutRasDoesNothing()
    {
        var chip = new Hm4816BankChip();

        chip.Address = 3;
        chip.WeBar = false;
        chip.Din = 0xFF;
        chip.CasBar = false;

        await Assert.That(chip.Peek(3)).IsEqualTo((byte)0);
        await Assert.That(chip.Dout).IsNull();
    }
}
