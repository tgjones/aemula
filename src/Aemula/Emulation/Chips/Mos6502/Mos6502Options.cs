namespace Aemula.Emulation.Chips.Mos6502;

public readonly struct Mos6502Options
{
    public static readonly Mos6502Options Default = new Mos6502Options(true, Mos6502CompatibilityMode.Normal);

    public readonly bool BcdEnabled;
    public readonly Mos6502CompatibilityMode CompatibilityMode;

    public Mos6502Options(bool bcdEnabled, Mos6502CompatibilityMode compatibilityMode)
    {
        BcdEnabled = bcdEnabled;
        CompatibilityMode = compatibilityMode;
    }
}
