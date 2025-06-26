namespace Aemula.Chips.Mos6502;

public readonly struct Mos6502Options
{
    public static readonly Mos6502Options Default = new Mos6502Options(true);

    public readonly bool BcdEnabled;

    public Mos6502Options(bool bcdEnabled)
    {
        BcdEnabled = bcdEnabled;
    }
}
