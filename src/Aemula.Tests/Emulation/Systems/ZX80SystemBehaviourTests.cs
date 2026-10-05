using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.ZX80;

namespace Aemula.Tests.Emulation.Systems;

// Locks in the board's cycle-exact behaviour so that restructuring how its
// oscillator edges are evaluated can be proved to change nothing. Every
// oscillator edge, the composite sample fed to Television and the Z80's
// address, bus and control pins are folded into one FNV-1a hash across the
// boot, an idle stretch at the K cursor and a key press and release.
//
// The golden values are not derived from anything external: they are what the
// model produced when this test was written. If an intentional change to the
// board model moves them, check the change is the cause, then replace the
// constants with the values the failing assertion prints.
public class ZX80SystemBehaviourTests
{
    private const int TotalTicks = 4_000_000;
    private const int KeyDownTick = 3_000_000;
    private const int KeyUpTick = 3_300_000;

    private static ulong HashRun(TelevisionStandard standard)
    {
        var system = new ZX80System(standard);
        var hash = 14695981039346656037UL;

        void Fold(ulong value)
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        system.OscillatorEdgeProcessed += () =>
        {
            var cpu = system.Cpu;
            Fold(system.CompositeSampleForTest);
            Fold(cpu.Address);
            Fold(cpu.Data);
            Fold(
                (cpu.MReq ? 1UL : 0) | (cpu.Rd ? 2UL : 0) | (cpu.Wr ? 4UL : 0) |
                (cpu.IoRq ? 8UL : 0) | (cpu.M1 ? 16UL : 0) | (cpu.Rfsh ? 32UL : 0) |
                (cpu.Halt ? 64UL : 0));
        };

        for (var i = 0; i < TotalTicks; i++)
        {
            if (i == KeyDownTick)
            {
                system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.A, Character = 'a' });
            }
            else if (i == KeyUpTick)
            {
                system.OnKeyEvent(new KeyEvent { IsDown = false, Key = Key.A, Character = 'a' });
            }

            system.Tick();
        }

        return hash;
    }

    [Test]
    public async Task PalBoardBehavesCycleForCycleAsRecorded()
    {
        await Assert.That(HashRun(TelevisionStandard.Pal)).IsEqualTo(4910464342983445381UL);
    }

    [Test]
    public async Task NtscBoardBehavesCycleForCycleAsRecorded()
    {
        await Assert.That(HashRun(TelevisionStandard.Ntsc)).IsEqualTo(16727917313685584785UL);
    }
}
