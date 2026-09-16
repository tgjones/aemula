using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula.Debugging;
using Hexa.NET.ImGui;

namespace Aemula.UI;

public sealed class DisassemblyWindow(Debugger debugger) : DebuggerWindow
{
    private const float GutterColumnWidth = 24f;
    private const float AddressColumnWidth = 50f;
    private const float BytesColumnWidth = 70f;
    private const float MnemonicColumnWidth = 50f;
    private const float AnnotationColumnWidth = 90f;

    private readonly List<DisassemblyLine> _disassembly = [];

    private int _previousPC;

    public override string DisplayName => "Disassembly";

    public override Pane PreferredPane => Pane.Right;

    protected override unsafe void DrawOverride(EmulatorTime time)
    {
        if (debugger.Disassembler.Changed)
        {
            _disassembly.Clear();

            var unknownStart = 0;

            void AddUnknownLines(int end)
            {
                _disassembly.Add(new DisassemblyLine(DisassemblyLineType.LineSeparator, null, unknownStart.ToString("X4")));
                if (unknownStart < end - 1)
                {
                    _disassembly.Add(new DisassemblyLine(DisassemblyLineType.Ellipsis, null, ".."));
                    _disassembly.Add(new DisassemblyLine(DisassemblyLineType.LineSeparator, null, (end - 1).ToString("X4")));
                }
            }

            for (var i = 0; i < debugger.Disassembler.Cache.Length; i++)
            {
                ref readonly var entry = ref debugger.Disassembler.Cache[i];

                if (entry.Instruction != null)
                {
                    if (unknownStart < i)
                    {
                        AddUnknownLines(i);
                    }

                    if (entry.Label != null)
                    {
                        _disassembly.Add(new DisassemblyLine(DisassemblyLineType.Text, null, $"{entry.Label}:"));
                    }

                    _disassembly.Add(new DisassemblyLine(DisassemblyLineType.Instruction, entry.Instruction, ""));

                    unknownStart = i + entry.Instruction.Value.InstructionSizeInBytes;
                }
            }

            if (unknownStart < 0xFFFF)
            {
                AddUnknownLines(0x10000);
            }

            debugger.Disassembler.Changed = false;
        }

        if (!debugger.Stopped)
        {
            if (ImGui.Button("Break"u8))
            {
                debugger.ActiveStepModeIndex = -1;
                debugger.Stopped = true;
            }
        }
        else
        {
            if (ImGui.Button("Continue"u8))
            {
                debugger.ActiveStepModeIndex = -1;
                debugger.Stopped = false;
            }

            for (var i = 0; i < debugger.StepModes.Count; i++)
            {
                var stepMode = debugger.StepModes[i];

                ImGui.SameLine();

                if (ImGui.Button(stepMode.Label))
                {
                    stepMode.Setup?.Invoke();
                    debugger.ActiveStepModeIndex = i;
                    debugger.Stopped = false;
                }
            }
        }

        ImGui.Separator();

        var lastPC = debugger.LastPC;

        Vector2 availableSize = default;
        float lineHeight = 0;
        if (ImGui.BeginChild("##disassembly_listing"u8, Vector2.Zero, ImGuiChildFlags.None))
        {
            availableSize = ImGui.GetContentRegionAvail();

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 3));

            lineHeight = ImGui.GetTextLineHeightWithSpacing();

            var rowHeight = ImGui.GetTextLineHeight();
            var rowHeightDiv2 = (int)(rowHeight / 2.0f);

            var disabledColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var disabledColorVector = ImGui.ColorConvertU32ToFloat4(disabledColor);

            if (ImGui.BeginTable(
                "##disassembly_table"u8,
                (int)DisassemblyColumn.Count,
                ImGuiTableFlags.None,
                availableSize))
            {
                // Gutter holds the breakpoint hit-test button and the PC/
                // breakpoint markers, drawn straight into the draw list rather
                // than as cell text - it never needs to hold anything wider
                // than that, so it's the one column not marked NoClip below.
                ImGui.TableSetupColumn("##gutter"u8, ImGuiTableColumnFlags.WidthFixed, GutterColumnWidth);
                // Address/Bytes/Mnemonic are NoClip so a long combined string
                // (still the case for Mnemonic until instructions carry a
                // separately-formatted Operand) overflows into the next empty
                // column instead of being cut off, rather than each column
                // needing to be sized for a worst-case string.
                ImGui.TableSetupColumn("##address"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoClip, AddressColumnWidth);
                ImGui.TableSetupColumn("##bytes"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoClip, BytesColumnWidth);
                ImGui.TableSetupColumn("##mnemonic"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoClip, MnemonicColumnWidth);
                ImGui.TableSetupColumn("##operand"u8, ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##annotation"u8, ImGuiTableColumnFlags.WidthFixed, AnnotationColumnWidth);

                var clipper = new ImGuiListClipper();
                clipper.Begin(_disassembly.Count, lineHeight);

                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var line = _disassembly[i];

                        ImGui.TableNextRow();

                        switch (line.Type)
                        {
                            case DisassemblyLineType.Instruction:
                                var instruction = line.Instruction!.Value;

                                ImGui.TableNextColumn();

                                var pos = ImGui.GetCursorScreenPos();
                                var drawList = ImGui.GetWindowDrawList();

                                ImGui.PushID(instruction.AddressNumeric);
                                if (ImGui.InvisibleButton("##breakpoint", new Vector2(GutterColumnWidth, rowHeight)))
                                {
                                    debugger.Breakpoints.ToggleExecutionBreakpoint(instruction.AddressNumeric);
                                }
                                ImGui.PopID();

                                var breakpointCircleMiddle = new Vector2(pos.X + 7, pos.Y + rowHeightDiv2);
                                var breakpointIndex = debugger.Breakpoints.FindIndex(BreakpointManager.ExecutionTypeLabel, instruction.AddressNumeric);
                                if (breakpointIndex >= 0)
                                {
                                    var breakpoint = debugger.Breakpoints.GetBreakpoint(breakpointIndex);
                                    var breakpointColor = breakpoint.Enabled
                                        ? 0xFF0000FF
                                        : 0xFF000088;
                                    drawList.AddCircleFilled(breakpointCircleMiddle, 7, breakpointColor);
                                }
                                else if (ImGui.IsItemHovered())
                                {
                                    drawList.AddCircle(breakpointCircleMiddle, 7, 0xFF0000FF);
                                }

                                if (instruction.AddressNumeric == lastPC)
                                {
                                    var a = new Vector2(pos.X + 2, pos.Y);
                                    var b = new Vector2(pos.X + 12, pos.Y + rowHeightDiv2);
                                    var c = new Vector2(pos.X + 2, pos.Y + rowHeight);
                                    drawList.AddTriangleFilled(a, b, c, 0xFF00FFFF);
                                }

                                ImGui.TableNextColumn();
                                ImGui.Text($"{instruction.Address}:");

                                ImGui.TableNextColumn();
                                ImGui.Text(instruction.RawBytes);

                                ImGui.TableNextColumn();
                                ImGui.Text(instruction.Disassembly);

                                // TODO: Show CPU ticks.
                                break;

                            case DisassemblyLineType.Text:
                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();
                                ImGui.TextColored(disabledColorVector, line.Text);
                                break;

                            case DisassemblyLineType.LineSeparator:
                            case DisassemblyLineType.Ellipsis:
                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();
                                ImGui.TextColored(disabledColorVector, line.Text);
                                break;

                            default:
                                throw new InvalidOperationException();
                        }
                    }
                }

                ImGui.EndTable();
            }

            ImGui.PopStyleVar();
        }
        ImGui.EndChild();

        if (lastPC != _previousPC)
        {
            // TODONT: Don't search whole array.
            var indexToScrollTo = _disassembly.FindIndex(x => x.Instruction?.AddressNumeric == lastPC);

            if (indexToScrollTo >= 0)
            {
                ImGui.BeginChild("##disassembly_listing"u8);

                var lineTop = indexToScrollTo * lineHeight;
                var lineBottom = lineTop + lineHeight;
                var scrollY = ImGui.GetScrollY();

                if (lineTop < scrollY || lineBottom > scrollY + availableSize.Y)
                {
                    ImGui.SetScrollY(lineTop - availableSize.Y * 0.5f);
                }

                ImGui.EndChild();
            }

            _previousPC = lastPC;
        }
    }

    private readonly record struct DisassemblyLine(DisassemblyLineType Type, DisassembledInstruction? Instruction, string Text);

    private enum DisassemblyLineType
    {
        Instruction,
        Text,
        LineSeparator,
        Ellipsis,
    }

    private enum DisassemblyColumn
    {
        Gutter,
        Address,
        Bytes,
        Mnemonic,
        Operand,
        Annotation,
        Count,
    }
}
