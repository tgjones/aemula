using System.Numerics;
using Aemula.Core;
using Aemula.Core.UI;
using ImGuiNET;

namespace Aemula.Chips.Ricoh2C02.UI;

internal sealed class PaletteWindow : DebuggerWindow
{
    private const int PaletteEntries = 16;

    private readonly Ricoh2C02 _ricoh2C02;

    public override string DisplayName => "NES PPU Palette";

    public override Pane PreferredPane => Pane.Bottom;

    public PaletteWindow(Ricoh2C02 ricoh2C02)
    {
        _ricoh2C02 = ricoh2C02;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        DrawPalette((ushort)0x3F00u);

        DrawPalette((ushort)0x3F10u);
    }

    private void DrawPalette(ushort startAddress)
    {
        for (var i = 0; i < PaletteEntries; i++)
        {
            var address = (ushort)(startAddress + i);
            var color = _ricoh2C02.GetColor(address);

            if (i > 0)
            {
                ImGui.SameLine();
            }

            ImGui.ColorButton(
                $"Palette_{startAddress}_{i}",
                new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, 1),
                ImGuiColorEditFlags.None);
        }
    }
}
