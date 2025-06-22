using Aemula.Core;
using Aemula.Core.UI;
using ImGuiNET;
using static Aemula.Chips.Ricoh2C02.Ricoh2C02;

namespace Aemula.Chips.Ricoh2C02.UI;

public sealed class PpuStateWindow : DebuggerWindow
{
    private static readonly string[] NameTableXOptions =
    {
        "Add 0 to X scroll",
        "Add 256 to X scroll",
    };

    private static readonly string[] NameTableYOptions =
    {
        "Add 0 to Y scroll",
        "Add 240 to Y scroll",
    };

    private static readonly string[] VramIncrementOptions =
    {
        "Add 1, going across",
        "Add 32, going down",
    };

    private static readonly string[] SpritePatternTableAddressOptions =
    {
        "$0000",
        "$1000",
    };

    private static readonly string[] SpriteSizeOptions =
    {
        "8x8",
        "8x16",
    };

    private readonly Ricoh2C02 _ppu;

    public override string DisplayName => "PPU State";

    public PpuStateWindow(Ricoh2C02 ppu)
    {
        _ppu = ppu;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        ImGui.Text($"Cycles:   {_ppu.Cycles}");
        ImGui.Text($"Frames:   {_ppu.Frames}");
        ImGui.Text($"Scanline: {_ppu.CurrentScanline}");
        ImGui.Text($"Dot:      {_ppu.CurrentDot}");

        ImGui.Separator();

        ImGui.Text("Control Register");
        DrawCtrlRegister();

        ImGui.Separator();

        ImGui.Text("Mask Register");
        DrawMaskRegister();

        ImGui.Separator();

        ImGui.Text("Status Register");
        DrawStatusRegister();
    }

    private void DrawCtrlRegister()
    {
        const float comboWidth = 180;

        var nameTableX = (int)_ppu.CtrlRegister.NameTableX;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo("Nametable X", ref nameTableX, NameTableXOptions, NameTableXOptions.Length))
        {
            _ppu.CtrlRegister.NameTableX = (byte)nameTableX;
        }

        var nameTableY = (int)_ppu.CtrlRegister.NameTableY;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo("Nametable Y", ref nameTableY, NameTableYOptions, NameTableYOptions.Length))
        {
            _ppu.CtrlRegister.NameTableY = (byte)nameTableY;
        }

        var vramIncrementMode = _ppu.CtrlRegister.VRamAddressIncrementMode;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGuiUtility.ComboEnum("VRAM Increment", ref vramIncrementMode, VramIncrementOptions))
        {
            _ppu.CtrlRegister.VRamAddressIncrementMode = vramIncrementMode;
        }

        int spritePatternTableAddress = _ppu.CtrlRegister.SpritePatternTableAddress;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo("Sprite Pattern Table Address", ref spritePatternTableAddress, SpritePatternTableAddressOptions, SpritePatternTableAddressOptions.Length))
        {
            _ppu.CtrlRegister.SpritePatternTableAddress = (byte)spritePatternTableAddress;
        }

        int backgroundPatternTableAddress = _ppu.CtrlRegister.BackgroundPatternTableAddress;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo("Background Pattern Table Address", ref backgroundPatternTableAddress, SpritePatternTableAddressOptions, SpritePatternTableAddressOptions.Length))
        {
            _ppu.CtrlRegister.BackgroundPatternTableAddress = (byte)backgroundPatternTableAddress;
        }

        int spriteSize = (int)_ppu.CtrlRegister.SpriteSize;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo("Sprite Size", ref spriteSize, SpriteSizeOptions, SpriteSizeOptions.Length))
        {
            _ppu.CtrlRegister.SpriteSize = (SpriteSize)spriteSize;
        }

        var slaveMode = _ppu.CtrlRegister.SlaveMode;
        if (ImGui.Checkbox("Slave Mode", ref slaveMode))
        {
            _ppu.CtrlRegister.SlaveMode = slaveMode;
        }

        var enableNmi = _ppu.CtrlRegister.EnableNmi;
        if (ImGui.Checkbox("Enable NMI", ref enableNmi))
        {
            _ppu.CtrlRegister.EnableNmi = enableNmi;
        }
    }

    private void DrawMaskRegister()
    {
        var grayscale = _ppu.MaskRegister.Grayscale;
        if (ImGui.Checkbox("Grayscale", ref grayscale))
        {
            _ppu.MaskRegister.Grayscale = grayscale;
        }

        var renderBackgroundLeft = _ppu.MaskRegister.RenderBackgroundLeft;
        if (ImGui.Checkbox("Render Background Left", ref renderBackgroundLeft))
        {
            _ppu.MaskRegister.RenderBackgroundLeft = renderBackgroundLeft;
        }

        var renderSpritesLeft = _ppu.MaskRegister.RenderSpritesLeft;
        if (ImGui.Checkbox("Render Sprites Left", ref renderSpritesLeft))
        {
            _ppu.MaskRegister.RenderSpritesLeft = renderSpritesLeft;
        }

        var renderBackground = _ppu.MaskRegister.RenderBackground;
        if (ImGui.Checkbox("Render Background", ref renderBackground))
        {
            _ppu.MaskRegister.RenderBackground = renderBackground;
        }

        var renderSprites = _ppu.MaskRegister.RenderSprites;
        if (ImGui.Checkbox("Render Sprites", ref renderSprites))
        {
            _ppu.MaskRegister.RenderSprites = renderSprites;
        }

        var emphasizeRed = _ppu.MaskRegister.EmphasizeRed;
        if (ImGui.Checkbox("Emphasize Red", ref emphasizeRed))
        {
            _ppu.MaskRegister.EmphasizeRed = emphasizeRed;
        }

        var emphasizeGreen = _ppu.MaskRegister.EmphasizeGreen;
        if (ImGui.Checkbox("Emphasize Green", ref emphasizeGreen))
        {
            _ppu.MaskRegister.EmphasizeGreen = emphasizeGreen;
        }

        var emphasizeBlue = _ppu.MaskRegister.EmphasizeBlue;
        if (ImGui.Checkbox("Emphasize Blue", ref emphasizeBlue))
        {
            _ppu.MaskRegister.EmphasizeBlue = emphasizeBlue;
        }
    }

    private void DrawStatusRegister()
    {
        var spriteOverflow = _ppu.StatusRegister.SpriteOverflow;
        if (ImGui.Checkbox("Sprite Overflow", ref spriteOverflow))
        {
            _ppu.StatusRegister.SpriteOverflow = spriteOverflow;
        }

        var sprite0Hit = _ppu.StatusRegister.Sprite0Hit;
        if (ImGui.Checkbox("Sprite 0 Hit", ref sprite0Hit))
        {
            _ppu.StatusRegister.Sprite0Hit = sprite0Hit;
        }

        var vBlankStarted = _ppu.StatusRegister.VBlankStarted;
        if (ImGui.Checkbox("VBlank Started", ref vBlankStarted))
        {
            _ppu.StatusRegister.VBlankStarted = vBlankStarted;
        }
    }
}
