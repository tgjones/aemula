namespace Aemula.Emulation.Systems.Nes.Mappers;

/// <summary>
/// How the cartridge wires the console's 2 KB name-table SRAM (CIRAM). On real
/// hardware this is not a mode the PPU knows about - it is the cartridge driving
/// the <c>CIRAM A10</c> connector pin from one of the PPU address lines (or a
/// fixed level, or a mapper register). The enum values match iNES flags-6 bit 0
/// (0 = <see cref="Horizontal"/>, 1 = <see cref="Vertical"/>).
/// </summary>
public enum NametableMirroring
{
    /// <summary>"Horizontal mirroring": CIRAM A10 follows PPU A11, so $2000/$2400
    /// share a page and $2800/$2C00 share the other (screen mirrored left/right).</summary>
    Horizontal,

    /// <summary>"Vertical mirroring": CIRAM A10 follows PPU A10, so $2000/$2800
    /// share a page and $2400/$2C00 share the other (screen mirrored top/bottom).</summary>
    Vertical,

    /// <summary>Both name-tables forced to the lower CIRAM page (CIRAM A10 = 0).</summary>
    SingleScreenLower,

    /// <summary>Both name-tables forced to the upper CIRAM page (CIRAM A10 = 1).</summary>
    SingleScreenUpper,

    /// <summary>Cartridge supplies its own 4 KB of name-table RAM; the console
    /// CIRAM is not selected. Not yet fully modelled - treated as
    /// <see cref="Vertical"/> against the 2 KB CIRAM for now.</summary>
    FourScreen,
}
