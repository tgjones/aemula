using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula.Debugging;
using Hexa.NET.ImGui;

namespace Aemula.UI;

public sealed class DisassemblyWindow(Debugger debugger) : DebuggerWindow
{
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

            var clipper = new ImGuiListClipper();
            clipper.Begin(_disassembly.Count, lineHeight);

            const byte grayColor = 0x99;
            var grayColorVector = new Vector4(new Vector3(grayColor / (float)0xFF), 1.0f);

            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var line = _disassembly[i];

                    var pos = ImGui.GetCursorScreenPos();
                    var drawList = ImGui.GetWindowDrawList();

                    switch (line.Type)
                    {
                        case DisassemblyLineType.Instruction:
                            var instruction = line.Instruction!.Value;
                            ImGui.PushID(instruction.AddressNumeric);
                            if (ImGui.InvisibleButton("##breakpoint", new Vector2(16, rowHeight)))
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

                            ImGui.SameLine();
                            ImGui.Text($"{instruction.Address}:   ");

                            ImGui.SameLine();
                            ImGui.Text($"{instruction.RawBytes}");

                            ImGui.SameLine(250);
                            ImGui.Text(instruction.Disassembly);

                            // TODO: Show CPU ticks.
                            break;

                        case DisassemblyLineType.Text:
                            ImGui.TextColored(grayColorVector, line.Text);
                            break;

                        case DisassemblyLineType.LineSeparator:
                            ImGui.SetCursorPosX(24);
                            ImGui.TextColored(grayColorVector, line.Text);
                            break;

                        case DisassemblyLineType.Ellipsis:
                            ImGui.SetCursorPosX(24);
                            ImGui.TextColored(grayColorVector, line.Text);
                            break;

                        default:
                            throw new InvalidOperationException();
                    }
                }
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
}
