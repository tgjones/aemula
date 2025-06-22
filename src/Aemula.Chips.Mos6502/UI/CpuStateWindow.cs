using Aemula.Core;
using Aemula.Core.UI;
using ImGuiNET;

namespace Aemula.Chips.Mos6502.UI;

internal sealed class CpuStateWindow : DebuggerWindow
{
    private readonly Mos6502 _mos6502;

    public override string DisplayName => "MOS6502 CPU State";

    public override Pane PreferredPane => Pane.Left;

    public CpuStateWindow(Mos6502 mos6502)
    {
        _mos6502 = mos6502;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        ImGui.Text("Registers");

        ImGui.Text($"A:  {_mos6502.A:X2}");
        ImGui.Text($"X:  {_mos6502.X:X2}");
        ImGui.Text($"Y:  {_mos6502.Y:X2}");
        ImGui.Text($"PC: {_mos6502.PC:X4}");
        ImGui.Text($"SP: {_mos6502.SP:X2}");

        ImGui.Spacing();

        ImGui.Text("Flags");

        ImGui.Text($"P: {_mos6502.P.AsByte(false):X}");

        ImGui.Checkbox("Carry", ref _mos6502.P.C);
        ImGui.Checkbox("Zero", ref _mos6502.P.Z);
        ImGui.Checkbox("Interrupt", ref _mos6502.P.I);
        ImGui.Checkbox("Overflow", ref _mos6502.P.V);
        ImGui.Checkbox("Negative", ref _mos6502.P.N);
        ImGui.Checkbox("BCD", ref _mos6502.P.D);
    }
}
