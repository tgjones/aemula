using System;

namespace Aemula.Emulation.Chips.Ricoh2C02;

// Optional straight-to-RGB frame output, parallel to the analog composite path
// in Ricoh2C02Chip.Video.cs and off by default. The 2C02's real output is the
// NTSC waveform the Television decodes; this per-dot palette lookup plus array
// store is pure overhead for that path, so it only runs when a caller opts in
// via RenderFramebuffer - in practice the framebuffer-hash test oracle
// (Oracle C in docs/nes-ppu-plan.md). With the gate clear FramebufferTick
// returns before touching anything.
partial class Ricoh2C02Chip
{
    private readonly Color[] _framebuffer = new Color[256 * 240];
    private ulong _framebufferFrame;

    /// <summary>
    /// When true, each visible dot writes its post-mux colour - background /
    /// sprite priority already resolved, grayscale and colour emphasis applied -
    /// into <see cref="Framebuffer"/>. Defaults to false: the display path is
    /// the composite waveform, for which filling this buffer is wasted work.
    /// </summary>
    public bool RenderFramebuffer { get; set; }

    /// <summary>
    /// The 256x240 picture, row-major, one entry per pixel. Only written while
    /// <see cref="RenderFramebuffer"/> is set; otherwise stale. Holds the most
    /// recently completed frame - see <see cref="FramebufferFrame"/>.
    /// </summary>
    public ReadOnlySpan<Color> Framebuffer => _framebuffer;

    /// <summary>
    /// Frame-complete signal: the value of <see cref="Frames"/> at the moment
    /// the visible area of the current <see cref="Framebuffer"/> contents
    /// finished (post-render line, dot 0). A caller polls it for a change to
    /// know a fresh frame is ready without subscribing to anything.
    /// </summary>
    public ulong FramebufferFrame => _framebufferFrame;

    // One dot, called from RenderTick after the pixel mux has produced
    // CurrentPixelColor. A no-op unless the gate is on; then it stores visible
    // dots and latches the frame-complete marker at (240, 0).
    private void FramebufferTick()
    {
        if (!RenderFramebuffer)
        {
            return;
        }

        if (CurrentScanline <= 239 && CurrentDot >= 1 && CurrentDot <= 256)
        {
            _framebuffer[CurrentScanline * 256 + (CurrentDot - 1)] =
                ApplyEmphasis(_systemPalette[CurrentPixelColor & 0x3F]);
        }
        else if (CurrentScanline == 240 && CurrentDot == 0)
        {
            _framebufferFrame = Frames;
        }
    }

    // NES colour emphasis ($2001 bits 5-7) as a per-channel attenuation of the
    // decoded RGB: each bit holds its own primary and pulls the other two down
    // to ~0.746 (Bisqwit's measured factor). The bits stack, so $2001 = $E0
    // darkens all three channels. A no-op with no emphasis bit set - grayscale
    // has already been folded into CurrentPixelColor by ReadPaletteMemory.
    private Color ApplyEmphasis(Color c)
    {
        if (!MaskRegister.EmphasizeRed &&
            !MaskRegister.EmphasizeGreen &&
            !MaskRegister.EmphasizeBlue)
        {
            return c;
        }

        double r = c.R, g = c.G, b = c.B;

        if (MaskRegister.EmphasizeRed)   { g *= EmphasisAttenuation; b *= EmphasisAttenuation; }
        if (MaskRegister.EmphasizeGreen) { r *= EmphasisAttenuation; b *= EmphasisAttenuation; }
        if (MaskRegister.EmphasizeBlue)  { r *= EmphasisAttenuation; g *= EmphasisAttenuation; }

        return new Color(
            (byte)Math.Round(Math.Clamp(r, 0, 255)),
            (byte)Math.Round(Math.Clamp(g, 0, 255)),
            (byte)Math.Round(Math.Clamp(b, 0, 255)));
    }

    private const double EmphasisAttenuation = 0.746;
}
