using Aemula.Emulation.Chips.Z80;
using Hexa.NET.ImGui;

namespace Aemula.UI.Chips.Z80;

internal sealed class CpuStateWindow : DebuggerWindow
{
    private readonly Z80Chip _z80;

    public override string DisplayName => "Z80 CPU State";

    public override Pane PreferredPane => Pane.Left;

    public CpuStateWindow(Z80Chip z80)
    {
        _z80 = z80;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        ImGui.Text("Registers");

        ImGui.Text($"AF: {_z80.AF.Value:X4}");
        ImGui.Text($"BC: {_z80.BC.Value:X4}");
        ImGui.Text($"DE: {_z80.DE.Value:X4}");
        ImGui.Text($"HL: {_z80.HL.Value:X4}");
        ImGui.Text($"IX: {_z80.IX.Value:X4}");
        ImGui.Text($"IY: {_z80.IY.Value:X4}");
        ImGui.Text($"SP: {_z80.SP.Value:X4}");
        ImGui.Text($"PC: {_z80.PC.Value:X4}");
        ImGui.Text($"WZ: {_z80.WZ.Value:X4}");

        ImGui.Spacing();

        ImGui.Text("Alternate Registers");

        ImGui.Text($"AF': {_z80.AFalt:X4}");
        ImGui.Text($"BC': {_z80.BCalt:X4}");
        ImGui.Text($"DE': {_z80.DEalt:X4}");
        ImGui.Text($"HL': {_z80.HLalt:X4}");

        ImGui.Spacing();

        ImGui.Text("Flags");

        ImGui.Text($"F: {_z80.Flags.AsByte():X2}");

        ImGui.Checkbox("Sign", ref _z80.Flags.Sign);
        ImGui.Checkbox("Zero", ref _z80.Flags.Zero);
        ImGui.Checkbox("Y", ref _z80.Flags.Y);
        ImGui.Checkbox("Half Carry", ref _z80.Flags.HalfCarry);
        ImGui.Checkbox("X", ref _z80.Flags.X);
        ImGui.Checkbox("Parity/Overflow", ref _z80.Flags.ParityOverflow);
        ImGui.Checkbox("Subtract", ref _z80.Flags.Subtract);
        ImGui.Checkbox("Carry", ref _z80.Flags.Carry);

        ImGui.Spacing();

        ImGui.Text("Internal");

        ImGui.Text($"I:   {_z80.I:X2}");
        ImGui.Text($"R:   {_z80.R:X2}");
        ImGui.Text($"IM:  {_z80.IM}");
        ImGui.Text($"IFF1: {_z80.IFF1}");
        ImGui.Text($"IFF2: {_z80.IFF2}");
        ImGui.Text($"Halt: {_z80.Halt}");
        ImGui.Text($"MachineCycle: {_z80.CurrentMachineCycle}");
        ImGui.Text($"State:        {_z80.CurrentState}");
    }
}
