using Aemula.Emulation.Chips.Mos6502;

namespace Aemula.Emulation.Systems.Atari2600;

public sealed class Mos6507 : Mos6502Chip
{
    public Mos6507()
        : base(Mos6502Options.Default)
    {
    }
}
