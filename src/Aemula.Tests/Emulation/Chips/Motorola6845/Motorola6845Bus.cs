using Aemula.Emulation.Chips.Motorola6845;

namespace Aemula.Tests.Emulation.Chips.Motorola6845;

// Drives a Motorola6845Chip the way a 6502 and the character clock do.
internal static class Motorola6845Bus
{
    public static Motorola6845Chip Reset()
    {
        var crtc = new Motorola6845Chip { Clk = true, Res = false };
        crtc.Res = true;
        return crtc;
    }

    public static void PulseReset(Motorola6845Chip crtc)
    {
        crtc.Res = false;
        crtc.Res = true;
    }

    public static void WriteRegister(Motorola6845Chip crtc, int register, byte value)
    {
        Write(crtc, false, (byte)register);
        Write(crtc, true, value);
    }

    public static byte ReadRegister(Motorola6845Chip crtc, int register)
    {
        Write(crtc, false, (byte)register);
        return Read(crtc, true);
    }

    public static void Write(Motorola6845Chip crtc, bool rs, byte value)
    {
        crtc.Cs = false;
        crtc.RS = rs;
        crtc.RW = false;
        crtc.DB = value;
        crtc.E = true;
        crtc.E = false;
        crtc.Cs = true;
    }

    public static byte Read(Motorola6845Chip crtc, bool rs)
    {
        crtc.Cs = false;
        crtc.RS = rs;
        crtc.RW = true;
        crtc.DB = 0xEE;
        crtc.E = true;
        var value = crtc.DB;
        crtc.E = false;
        crtc.Cs = true;
        return value;
    }

    /// <summary>
    /// One character clock.
    /// </summary>
    public static void Tick(Motorola6845Chip crtc)
    {
        crtc.Clk = true;
        crtc.Clk = false;
    }
}
