using System;
using System.IO;

namespace Aemula.Emulation.Systems.AppleII;

public sealed class AppleIISystem : EmulatedSystem
{
    // The Apple II's master oscillator: 4x the NTSC color subcarrier (3.579545MHz).
    // The CPU clock, video dot clock, and color subcarrier are all synchronous
    // divisions of this one crystal, so everything ticks from it directly rather
    // than from a derived, coarser rate.
    public override ulong CyclesPerSecond => 14_318_180;

    // Autostart Monitor + Applesoft BASIC, mapped at $D000-$FFFF.
    private readonly byte[] _rom = new byte[0x3000];

    // Character generator ROM (Signetics 2513 / Apple 341-0036).
    private readonly byte[] _characterRom = new byte[0x800];

    public override void LoadProgram(string filePath)
    {
        var romsDirectory = Path.Combine(AppContext.BaseDirectory, "Emulation", "Systems", "AppleII", "Roms");

        using (var romStream = File.OpenRead(Path.Combine(romsDirectory, "Apple2_Plus.rom")))
        {
            romStream.ReadExactly(_rom);
        }

        using (var characterRomStream = File.OpenRead(Path.Combine(romsDirectory, "Apple2_Video.rom")))
        {
            characterRomStream.ReadExactly(_characterRom);
        }

        RaiseProgramLoaded();
    }

    public override void Tick()
    {
        // TODO: Wire up CPU, RAM, and address decode chips (phase 2 onward).
    }
}
