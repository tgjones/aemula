using Aemula.Emulation.Chips.Mos6522;

namespace Aemula.Tests.Emulation.Chips.Mos6522;

// Drives a Mos6522Chip the way a 6502 does: every access is one phi2 cycle,
// with the read presented on the rising edge and the write latched (and the
// timers stepped) on the falling edge.
internal static class Mos6522Bus
{
    public const byte Orb = 0x0;
    public const byte Ora = 0x1;
    public const byte Ddrb = 0x2;
    public const byte Ddra = 0x3;
    public const byte T1Cl = 0x4;
    public const byte T1Ch = 0x5;
    public const byte T1Ll = 0x6;
    public const byte T1Lh = 0x7;
    public const byte T2Cl = 0x8;
    public const byte T2Ch = 0x9;
    public const byte Sr = 0xA;
    public const byte Acr = 0xB;
    public const byte Pcr = 0xC;
    public const byte Ifr = 0xD;
    public const byte Ier = 0xE;
    public const byte OraNoHandshake = 0xF;

    public static Mos6522Chip Reset()
    {
        // Unconnected port pins float high.
        var via = new Mos6522Chip { PA = 0xFF, PB = 0xFF };
        via.Res = true;
        return via;
    }

    public static byte Read(Mos6522Chip via, byte register)
    {
        via.Cs1 = true;
        via.Cs2 = false;
        via.RS = register;
        via.RW = true;
        via.Phi2 = true;
        var value = via.DB;
        via.Phi2 = false;
        return value;
    }

    public static void Write(Mos6522Chip via, byte register, byte value)
    {
        via.Cs1 = true;
        via.Cs2 = false;
        via.RS = register;
        via.RW = false;
        via.DB = value;
        via.Phi2 = true;
        via.Phi2 = false;
    }

    /// <summary>
    /// Runs cycles in which the chip is not addressed.
    /// </summary>
    public static void Idle(Mos6522Chip via, int cycles = 1)
    {
        via.Cs1 = false;

        for (var i = 0; i < cycles; i++)
        {
            via.Phi2 = true;
            via.Phi2 = false;
        }
    }
}
