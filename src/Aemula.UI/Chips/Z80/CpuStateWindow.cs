using System;
using Aemula.Emulation.Chips.Z80;
using Hexa.NET.ImGui;

namespace Aemula.UI.Chips.Z80;

internal sealed class CpuStateWindow : DebuggerWindow
{
    private readonly Z80Chip _z80;
    private readonly IcPin[] _leftPins;
    private readonly IcPin[] _rightPins;

    public override string DisplayName => "Z80 CPU State";

    public override Pane PreferredPane => Pane.Left;

    public CpuStateWindow(Z80Chip z80)
    {
        _z80 = z80;

        Func<bool?> Address(int bit) => () => (_z80.Address & (1 << bit)) != 0;
        Func<bool?> Data(int bit) => () => (_z80.Data & (1 << bit)) != 0;

        // 40-pin DIP, pins 1-20 down the left side and 40-21 down the right.
        _leftPins =
        [
            IcPin.Output(1, "A11", Address(11)),
            IcPin.Output(2, "A12", Address(12)),
            IcPin.Output(3, "A13", Address(13)),
            IcPin.Output(4, "A14", Address(14)),
            IcPin.Output(5, "A15", Address(15)),
            IcPin.Input(6, "CLK", () => _z80.Clk),
            IcPin.Bidirectional(7, "D4", Data(4)),
            IcPin.Bidirectional(8, "D3", Data(3)),
            IcPin.Bidirectional(9, "D5", Data(5)),
            IcPin.Bidirectional(10, "D6", Data(6)),
            IcPin.Power(11, "VCC"),
            IcPin.Bidirectional(12, "D2", Data(2)),
            IcPin.Bidirectional(13, "D7", Data(7)),
            IcPin.Bidirectional(14, "D0", Data(0)),
            IcPin.Bidirectional(15, "D1", Data(1)),
            IcPin.Input(16, "~INT~", () => _z80.Int, activeLow: true),
            IcPin.Input(17, "~NMI~", () => _z80.Nmi, activeLow: true),
            IcPin.Output(18, "~HALT~", () => _z80.Halt, activeLow: true),
            IcPin.Output(19, "~MREQ~", () => _z80.MReq, activeLow: true),
            IcPin.Output(20, "~IORQ~", () => _z80.IoRq, activeLow: true),
        ];

        _rightPins =
        [
            IcPin.Output(40, "A10", Address(10)),
            IcPin.Output(39, "A9", Address(9)),
            IcPin.Output(38, "A8", Address(8)),
            IcPin.Output(37, "A7", Address(7)),
            IcPin.Output(36, "A6", Address(6)),
            IcPin.Output(35, "A5", Address(5)),
            IcPin.Output(34, "A4", Address(4)),
            IcPin.Output(33, "A3", Address(3)),
            IcPin.Output(32, "A2", Address(2)),
            IcPin.Output(31, "A1", Address(1)),
            IcPin.Output(30, "A0", Address(0)),
            IcPin.Power(29, "GND"),
            IcPin.Output(28, "~RFSH~", () => _z80.Rfsh, activeLow: true),
            IcPin.Output(27, "~M1~", () => _z80.M1, activeLow: true),
            IcPin.Input(26, "~RESET~", () => _z80.Reset, activeLow: true),
            IcPin.Input(25, "~BUSRQ~", () => _z80.BusRq, activeLow: true),
            IcPin.Input(24, "~WAIT~", () => _z80.Wait, activeLow: true),
            IcPin.Output(23, "~BUSAK~", () => _z80.BusAk, activeLow: true),
            IcPin.Output(22, "~WR~", () => _z80.Wr, activeLow: true),
            IcPin.Output(21, "~RD~", () => _z80.Rd, activeLow: true),
        ];
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        var columnWidth = CpuWidgets.RegisterRowWidth(16);
        var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
        var sideBySide = ImGui.GetContentRegionAvail().X >= columnWidth + itemSpacing * 2 + IcDiagram.MeasureWidth(_leftPins, _rightPins);

        ImGui.BeginGroup();

        CpuWidgets.Section("Flags", columnWidth);

        var flags = _z80.Flags;
        CpuWidgets.FlagRow(
            [
                CpuWidgets.Flag("S", "Sign", flags.Sign),
                CpuWidgets.Flag("Z", "Zero", flags.Zero),
                CpuWidgets.Flag("Y", "Y (undocumented, bit 5)", flags.Y),
                CpuWidgets.Flag("H", "Half carry", flags.HalfCarry),
                CpuWidgets.Flag("X", "X (undocumented, bit 3)", flags.X),
                CpuWidgets.Flag("P", "Parity / overflow", flags.ParityOverflow),
                CpuWidgets.Flag("N", "Subtract", flags.Subtract),
                CpuWidgets.Flag("C", "Carry", flags.Carry),
            ],
            columnWidth);
        CpuWidgets.RegisterRow("F", flags.AsByte(), 8, columnWidth);

        CpuWidgets.Section("Registers", columnWidth);

        CpuWidgets.RegisterRow("AF", _z80.AF.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("BC", _z80.BC.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("DE", _z80.DE.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("HL", _z80.HL.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("IX", _z80.IX.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("IY", _z80.IY.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("SP", _z80.SP.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("PC", _z80.PC.Value, 16, columnWidth);
        CpuWidgets.RegisterRow("WZ", _z80.WZ.Value, 16, columnWidth);

        CpuWidgets.Section("Alternate registers", columnWidth);

        CpuWidgets.RegisterRow("AF'", _z80.AFalt, 16, columnWidth);
        CpuWidgets.RegisterRow("BC'", _z80.BCalt, 16, columnWidth);
        CpuWidgets.RegisterRow("DE'", _z80.DEalt, 16, columnWidth);
        CpuWidgets.RegisterRow("HL'", _z80.HLalt, 16, columnWidth);

        CpuWidgets.Section("Internal", columnWidth);

        CpuWidgets.RegisterRow("I", _z80.I, 8, columnWidth);
        CpuWidgets.RegisterRow("R", _z80.R, 8, columnWidth);
        CpuWidgets.FlagRow(
            [
                new CpuWidgets.FlagCell("IM", "Interrupt mode", _z80.IM.ToString(), false),
                CpuWidgets.Flag("IFF1", "Interrupt flip-flop 1", _z80.IFF1),
                CpuWidgets.Flag("IFF2", "Interrupt flip-flop 2", _z80.IFF2),
                CpuWidgets.Flag("HALT", "Halted", _z80.Halt),
            ],
            columnWidth);

        CpuWidgets.Section("Bus", columnWidth);

        CpuWidgets.KeyValue("Addr:", $"{_z80.Address:X4}");
        CpuWidgets.KeyValue("Data:", $"{_z80.Data:X2}");
        CpuWidgets.KeyValue("Cycle:", $"{_z80.CurrentMachineCycle}");
        CpuWidgets.KeyValue("State:", $"{_z80.CurrentState}");

        ImGui.EndGroup();

        if (sideBySide)
        {
            ImGui.SameLine(0, itemSpacing * 2);
        }
        else
        {
            CpuWidgets.Section("Pins", columnWidth);
        }

        ImGui.BeginGroup();
        IcDiagram.Draw("Z80 CPU", _leftPins, _rightPins);
        ImGui.EndGroup();
    }
}
