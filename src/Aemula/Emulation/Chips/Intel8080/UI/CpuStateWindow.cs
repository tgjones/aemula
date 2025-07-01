using Aemula.UI;
using ImGuiNET;

namespace Aemula.Emulation.Chips.Intel8080.UI;

internal sealed class CpuStateWindow : DebuggerWindow
{
    private readonly Intel8080Chip _intel8080;

    public override string DisplayName => "Intel 8080 CPU State";

    public override Pane PreferredPane => Pane.Left;

    public CpuStateWindow(Intel8080Chip intel8080)
    {
        _intel8080 = intel8080;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        ImGui.Text("Registers");

        ImGui.Text($"PC: {_intel8080.PC.Value:X4}");
        ImGui.Text($"SP: {_intel8080.SP.Value:X2}");
        ImGui.Text($"B:  {_intel8080.BC.B:X2}");
        ImGui.Text($"C:  {_intel8080.BC.C:X2}");
        ImGui.Text($"D:  {_intel8080.DE.D:X2}");
        ImGui.Text($"E:  {_intel8080.DE.E:X2}");
        ImGui.Text($"H:  {_intel8080.HL.H:X2}");
        ImGui.Text($"L:  {_intel8080.HL.L:X2}");
        ImGui.Text($"A:  {_intel8080.A:X2}");

        ImGui.Spacing();

        ImGui.Text("Flags");

        ImGui.Text($"F: {_intel8080.Flags.AsByte():X2}");

        ImGui.Checkbox("Sign", ref _intel8080.Flags.Sign);
        ImGui.Checkbox("Zero", ref _intel8080.Flags.Zero);
        ImGui.Checkbox("Auxiliary Carry", ref _intel8080.Flags.AuxiliaryCarry);
        ImGui.Checkbox("Sign", ref _intel8080.Flags.Sign);
        ImGui.Checkbox("Parity", ref _intel8080.Flags.Parity);

        ImGui.Spacing();

        ImGui.Text("Internal");

        ImGui.Text($"MachineCycle: {_intel8080.CurrentMachineCycle}");
        ImGui.Text($"State:        {_intel8080.CurrentState}");
    }
}
