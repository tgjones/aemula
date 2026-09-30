using System;
using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502;
using Hexa.NET.ImGui;

namespace Aemula.UI.Chips.Mos6502;

internal sealed class CpuStateWindow : DebuggerWindow
{
    private readonly Mos6502Chip _mos6502;
    private readonly IcPin[] _leftPins;
    private readonly IcPin[] _rightPins;

    public override string DisplayName => "MOS6502 CPU State";

    public override Pane PreferredPane => Pane.Left;

    public CpuStateWindow(Mos6502Chip mos6502)
    {
        _mos6502 = mos6502;

        Func<bool?> Address(int bit) => () => (_mos6502.Address & (1 << bit)) != 0;
        Func<bool?> Data(int bit) => () => (_mos6502.Data & (1 << bit)) != 0;

        // 40-pin DIP, pins 1-20 down the left side and 40-21 down the right.
        _leftPins =
        [
            IcPin.Power(1, "VSS"),
            IcPin.Input(2, "RDY", () => _mos6502.Rdy),
            IcPin.Output(3, "PHI1", () => _mos6502.Phi1),
            IcPin.Input(4, "~IRQ~", () => _mos6502.Irq, activeLow: true),
            IcPin.NoConnect(5),
            IcPin.Input(6, "~NMI~", () => _mos6502.Nmi, activeLow: true),
            IcPin.Output(7, "SYNC", () => _mos6502.Sync),
            IcPin.Power(8, "VCC"),
            IcPin.Output(9, "A0", Address(0)),
            IcPin.Output(10, "A1", Address(1)),
            IcPin.Output(11, "A2", Address(2)),
            IcPin.Output(12, "A3", Address(3)),
            IcPin.Output(13, "A4", Address(4)),
            IcPin.Output(14, "A5", Address(5)),
            IcPin.Output(15, "A6", Address(6)),
            IcPin.Output(16, "A7", Address(7)),
            IcPin.Output(17, "A8", Address(8)),
            IcPin.Output(18, "A9", Address(9)),
            IcPin.Output(19, "A10", Address(10)),
            IcPin.Output(20, "A11", Address(11)),
        ];

        _rightPins =
        [
            IcPin.Input(40, "~RES~", () => _mos6502.Res, activeLow: true),
            IcPin.Output(39, "PHI2", () => _mos6502.Phi2),
            // The chip doesn't model the set-overflow pin, so it has no state to show.
            new IcPin(38, "~SO~", IcPinKind.Input, true, null),
            // PHI0 is the clock input; PHI2 is its buffered copy.
            IcPin.Input(37, "PHI0", () => _mos6502.Phi2),
            IcPin.NoConnect(36),
            IcPin.NoConnect(35),
            IcPin.Output(34, "R/~W~", () => _mos6502.RW),
            IcPin.Bidirectional(33, "D0", Data(0)),
            IcPin.Bidirectional(32, "D1", Data(1)),
            IcPin.Bidirectional(31, "D2", Data(2)),
            IcPin.Bidirectional(30, "D3", Data(3)),
            IcPin.Bidirectional(29, "D4", Data(4)),
            IcPin.Bidirectional(28, "D5", Data(5)),
            IcPin.Bidirectional(27, "D6", Data(6)),
            IcPin.Bidirectional(26, "D7", Data(7)),
            IcPin.Output(25, "A15", Address(15)),
            IcPin.Output(24, "A14", Address(14)),
            IcPin.Output(23, "A13", Address(13)),
            IcPin.Output(22, "A12", Address(12)),
            IcPin.Power(21, "VSS"),
        ];
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        var columnWidth = CpuWidgets.RegisterRowWidth(16);
        var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
        var sideBySide = ImGui.GetContentRegionAvail().X >= columnWidth + itemSpacing * 2 + IcDiagram.MeasureWidth(_leftPins, _rightPins);

        ImGui.BeginGroup();

        CpuWidgets.Section("Flags", columnWidth);

        var p = _mos6502.P;
        CpuWidgets.FlagRow(
            [
                CpuWidgets.Flag("N", "Negative", p.N),
                CpuWidgets.Flag("V", "Overflow", p.V),
                CpuWidgets.UnusedFlag("-", "Unused"),
                CpuWidgets.UnusedFlag("B", "Break (exists only on the stack)"),
                CpuWidgets.Flag("D", "Decimal (BCD)", p.D),
                CpuWidgets.Flag("I", "Interrupt disable", p.I),
                CpuWidgets.Flag("Z", "Zero", p.Z),
                CpuWidgets.Flag("C", "Carry", p.C),
            ],
            columnWidth);
        CpuWidgets.RegisterRow("P", p.AsByte(false), 8, columnWidth);

        CpuWidgets.Section("Registers", columnWidth);

        CpuWidgets.RegisterRow("PC", _mos6502.PC, 16, columnWidth);
        CpuWidgets.RegisterRow("SP", _mos6502.SP, 8, columnWidth);
        CpuWidgets.RegisterRow("A", _mos6502.A, 8, columnWidth);
        CpuWidgets.RegisterRow("X", _mos6502.X, 8, columnWidth);
        CpuWidgets.RegisterRow("Y", _mos6502.Y, 8, columnWidth);

        CpuWidgets.Section("Bus", columnWidth);

        CpuWidgets.KeyValue("Addr:", $"{_mos6502.Address:X4}");
        CpuWidgets.KeyValue("Data:", $"{_mos6502.Data:X2}");
        CpuWidgets.KeyValue("Cycle:", $"T{_mos6502.TR}");

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
        IcDiagram.Draw("MOS 6502", _leftPins, _rightPins);
        ImGui.EndGroup();
    }
}
