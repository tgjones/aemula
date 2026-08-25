using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7442ChipTests
{
    [Test]
    [Arguments(false, false, false, false, 0)]
    [Arguments(true, false, false, false, 1)]
    [Arguments(false, true, false, false, 2)]
    [Arguments(true, true, false, false, 3)]
    [Arguments(false, false, true, false, 4)]
    [Arguments(true, false, true, false, 5)]
    [Arguments(false, true, true, false, 6)]
    [Arguments(true, true, true, false, 7)]
    [Arguments(false, false, false, true, 8)]
    [Arguments(true, false, false, true, 9)]
    public async Task DrivesOnlySelectedOutputLow(bool a, bool b, bool c, bool d, int selected)
    {
        var chip = new Ttl7442Chip { A = a, B = b, C = c, D = d };

        var outputs = new[]
        {
            chip.Y0, chip.Y1, chip.Y2, chip.Y3, chip.Y4,
            chip.Y5, chip.Y6, chip.Y7, chip.Y8, chip.Y9,
        };

        for (var i = 0; i < outputs.Length; i++)
        {
            await Assert.That(outputs[i]).IsEqualTo(i != selected);
        }
    }

    [Test]
    [Arguments(false, true, false, true)] // 10
    [Arguments(true, true, false, true)] // 11
    [Arguments(false, false, true, true)] // 12
    [Arguments(true, false, true, true)] // 13
    [Arguments(false, true, true, true)] // 14
    [Arguments(true, true, true, true)] // 15
    public async Task AllOutputsInactiveForInvalidBcdCodes(bool a, bool b, bool c, bool d)
    {
        var chip = new Ttl7442Chip { A = a, B = b, C = c, D = d };

        var outputs = new[]
        {
            chip.Y0, chip.Y1, chip.Y2, chip.Y3, chip.Y4,
            chip.Y5, chip.Y6, chip.Y7, chip.Y8, chip.Y9,
        };

        foreach (var output in outputs)
        {
            await Assert.That(output).IsEqualTo(true);
        }
    }
}
