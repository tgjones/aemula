using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula.Debugging;
using Hexa.NET.ImGui;

namespace Aemula.UI;

public sealed class DisassemblyWindow : DebuggerWindow
{
    private readonly Debugger _debugger;
    private readonly List<DisassemblyLine> _disassembly;

    private int _previousPC;

    public override string DisplayName => "Disassembly";

    public override Pane PreferredPane => Pane.Right;

    public DisassemblyWindow(Debugger debugger)
    {
        _disassembly = new List<DisassemblyLine>();

        _debugger = debugger;
    }

    protected override unsafe void DrawOverride(EmulatorTime time)
    {
        if (_debugger.Disassembler.Changed)
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

            for (var i = 0; i < _debugger.Disassembler.Cache.Length; i++)
            {
                ref readonly var entry = ref _debugger.Disassembler.Cache[i];

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

                    _disassembly.Add(new DisassemblyLine(DisassemblyLineType.Instruction, entry.Instruction, null));

                    unknownStart = i + entry.Instruction.Value.InstructionSizeInBytes;
                }
            }

            if (unknownStart < 0xFFFF)
            {
                AddUnknownLines(0x10000);
            }

            _debugger.Disassembler.Changed = false;
        }

        if (!_debugger.Stopped)
        {
            if (ImGui.Button("Break"u8))
            {
                _debugger.ActiveStepModeIndex = -1;
                _debugger.Stopped = true;
            }
        }
        else
        {
            if (ImGui.Button("Continue"u8))
            {
                _debugger.ActiveStepModeIndex = -1;
                _debugger.Stopped = false;
            }

            for (var i = 0; i < _debugger.StepModes.Count; i++)
            {
                var stepMode = _debugger.StepModes[i];

                ImGui.SameLine();

                if (ImGui.Button(stepMode.Label))
                {
                    stepMode.Setup?.Invoke();
                    _debugger.ActiveStepModeIndex = i;
                    _debugger.Stopped = false;
                }
            }
        }

        ImGui.Separator();

        var lastPC = _debugger.LastPC;

        Vector2 availableSize = default;
        if (ImGui.BeginChild("##disassembly_listing"u8, Vector2.Zero, ImGuiChildFlags.None))
        {
            availableSize = ImGui.GetContentRegionAvail();

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 3));

            var lineHeight = ImGui.GetTextLineHeightWithSpacing();
            var lineHeightDiv2 = (int)(lineHeight / 2.0f);

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
                            var instruction = line.Instruction.Value;
                            ImGui.PushID(instruction.AddressNumeric);
                            if (ImGui.InvisibleButton("##breakpoint", new Vector2(16, lineHeight)))
                            {
                                _debugger.Breakpoints.ToggleExecutionBreakpoint(instruction.AddressNumeric);
                            }
                            ImGui.PopID();

                            var breakpointCircleMiddle = new Vector2(pos.X + 7, pos.Y + lineHeightDiv2);
                            var breakpointIndex = _debugger.Breakpoints.FindIndex(BreakpointManager.ExecutionTypeLabel, instruction.AddressNumeric);
                            if (breakpointIndex >= 0)
                            {
                                var breakpoint = _debugger.Breakpoints.GetBreakpoint(breakpointIndex);
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
                                var b = new Vector2(pos.X + 12, pos.Y + lineHeightDiv2);
                                var c = new Vector2(pos.X + 2, pos.Y + lineHeight);
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

            ImGui.BeginChild("##disassembly_listing"u8);
            ImGui.SetScrollY(indexToScrollTo * ImGui.GetTextLineHeight() - availableSize.Y * 0.5f);
            ImGui.EndChild();

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
