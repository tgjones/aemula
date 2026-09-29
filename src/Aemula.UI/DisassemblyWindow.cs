using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Widest cycles text the annotation column shows ("current/total").
    private const string AnnotationWidestText = "00/00";

    // Same yellow/red accent hues as the PC triangle and breakpoint dot
    // drawn in the gutter, just translucent - these mark rows rather than
    // precise points, so they're a wash behind the text rather than an
    // opaque fill.
    private static readonly uint PcRowTintColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0f, 0.35f));
    private static readonly uint BreakpointRowTintColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0f, 0f, 0.18f));

    private const float ScrollbarMarkerWidth = 4f;

    private readonly List<DisassemblyLine> _disassembly = [];

    private int _previousPC;

    // Goto-address input box state and the scroll-lock toggle that keeps it
    // from immediately fighting the PC auto-scroll (see DrawOverride).
    private string _gotoAddressInput = string.Empty;
    private bool _scrollLocked;

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

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);
        var gotoSubmitted = ImGui.InputText(
            "##gotoAddress",
            ref _gotoAddressInput,
            5,
            ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var gotoClicked = ImGui.Button("Go to Address"u8);
        ImGui.SameLine();
        // Gates the PC auto-scroll below so it doesn't yank the view back to
        // PC while manually browsing after a goto-address jump - the two
        // would otherwise fight each other every frame PC keeps moving.
        ImGui.Checkbox("Scroll Lock"u8, ref _scrollLocked);

        int? gotoIndexToScrollTo = null;
        if ((gotoSubmitted || gotoClicked) &&
            ushort.TryParse(_gotoAddressInput, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gotoAddress))
        {
            var index = _disassembly.FindIndex(x => x.Instruction?.AddressNumeric == gotoAddress);
            if (index >= 0)
            {
                gotoIndexToScrollTo = index;
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

            var rowHeight = ImGui.GetTextLineHeight();

            // Table rows are spaced by cell padding, not item spacing. The clipper
            // and scroll math need the real stride: if it's off, the error
            // accumulates over the whole listing and the last rows end up clipped
            // when scrolled to the bottom.
            lineHeight = rowHeight + ImGui.GetStyle().CellPadding.Y * 2;
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
                // Sized to the cycles text itself, so it doesn't take space from the
                // stretchy Operand column when the pane is narrow.
                ImGui.TableSetupColumn("##annotation"u8, ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(AnnotationWidestText).X);

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
                                var isCurrentPC = instruction.AddressNumeric == lastPC;

                                // PC tint wins when a row is both the current PC and a
                                // breakpoint - the triangle marker is still the precise PC
                                // indicator, and the breakpoint dot itself renders in the
                                // gutter below regardless of which tint the row gets.
                                if (isCurrentPC)
                                {
                                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, PcRowTintColor);
                                }
                                else
                                {
                                    var rowBreakpointIndex = debugger.Breakpoints.FindIndex(BreakpointManager.ExecutionTypeLabel, instruction.AddressNumeric);
                                    if (rowBreakpointIndex >= 0 && debugger.Breakpoints.GetBreakpoint(rowBreakpointIndex).Enabled)
                                    {
                                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, BreakpointRowTintColor);
                                    }
                                }

                                ImGui.TableNextColumn();

                                var pos = ImGui.GetCursorScreenPos();
                                var drawList = ImGui.GetWindowDrawList();

                                ImGui.PushID(instruction.AddressNumeric);

                                // Row-wide hit target for the right-click context menu, under
                                // the breakpoint button below - AllowOverlap lets that button
                                // still receive its own left-click despite this spanning the
                                // same area, and resetting the cursor back to pos afterward is
                                // what makes the two occupy the same screen rect rather than
                                // the button landing below this row.
                                ImGui.Selectable("##row", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap, new Vector2(0, rowHeight));

                                if (ImGui.BeginPopupContextItem("##rowContext"))
                                {
                                    if (ImGui.MenuItem("Toggle Breakpoint"))
                                    {
                                        debugger.Breakpoints.ToggleExecutionBreakpoint(instruction.AddressNumeric);
                                    }
                                    if (ImGui.MenuItem("Add Byte Watchpoint Here"))
                                    {
                                        debugger.Breakpoints.AddValueByteBreakpoint(true, instruction.AddressNumeric);
                                    }
                                    if (ImGui.MenuItem("Add Word Watchpoint Here"))
                                    {
                                        debugger.Breakpoints.AddValueWordBreakpoint(true, instruction.AddressNumeric);
                                    }

                                    ImGui.Separator();

                                    if (ImGui.MenuItem("Run to Cursor"))
                                    {
                                        debugger.RunToAddress(instruction.AddressNumeric);
                                        debugger.ActiveStepModeIndex = -1;
                                        debugger.Stopped = false;
                                    }

                                    ImGui.Separator();

                                    if (ImGui.MenuItem("Copy Address"))
                                    {
                                        ImGui.SetClipboardText(instruction.Address);
                                    }
                                    if (ImGui.MenuItem("Copy Bytes"))
                                    {
                                        ImGui.SetClipboardText(instruction.RawBytes);
                                    }

                                    ImGui.EndPopup();
                                }

                                ImGui.SetCursorScreenPos(pos);

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

                                if (isCurrentPC)
                                {
                                    var a = new Vector2(pos.X + 2, pos.Y);
                                    var b = new Vector2(pos.X + 12, pos.Y + rowHeightDiv2);
                                    var c = new Vector2(pos.X + 2, pos.Y + rowHeight);
                                    drawList.AddTriangleFilled(a, b, c, 0xFF00FFFF);
                                }

                                ImGui.TableNextColumn();
                                ImGui.Text($"{instruction.Address}:");

                                ImGui.TableNextColumn();
                                ImGui.TextColored(disabledColorVector, instruction.RawBytes);

                                ImGui.TableNextColumn();
                                ImGui.Text(instruction.Mnemonic);

                                if (instruction.Operand.Length > 0)
                                {
                                    ImGui.TableNextColumn();
                                    ImGui.TextColored(GetOperandKindColor(instruction.OperandKind), instruction.Operand);

                                    // Only meaningful for the instruction actually at PC - X/Y
                                    // (or the pointed-to memory) could be anything the next time
                                    // any other row's instruction runs, so this is recomputed
                                    // live every frame rather than cached.
                                    if (isCurrentPC)
                                    {
                                        var effectiveAddress = debugger.Disassembler.TryGetEffectiveAddress(instruction);
                                        if (effectiveAddress != null)
                                        {
                                            ImGui.SameLine();
                                            ImGui.TextColored(disabledColorVector, $"; (${effectiveAddress.Value.Address:X4}) = ${effectiveAddress.Value.Value:X2}");
                                        }
                                    }
                                }

                                // Total is the measured length of the last completed
                                // execution, so zero means "hasn't completed one yet" and is
                                // left blank rather than shown as "0" (which would read as a
                                // measurement). The currently-executing row additionally
                                // shows progress through it ("1/5"), even when the total
                                // isn't known yet. Explicit TableSetColumnIndex rather than
                                // another TableNextColumn since the Operand column above is
                                // sometimes skipped.
                                var lastCycles = debugger.LastExecutionCycles[instruction.AddressNumeric];
                                var cyclesText = isCurrentPC && debugger.CurrentInstructionCycles > 0
                                    ? (lastCycles > 0 ? $"{debugger.CurrentInstructionCycles}/{lastCycles}" : $"{debugger.CurrentInstructionCycles}/?")
                                    : (lastCycles > 0 ? $"{lastCycles}" : null);
                                if (cyclesText != null)
                                {
                                    ImGui.TableSetColumnIndex((int)DisassemblyColumn.Annotation);
                                    var cyclesTextWidth = ImGui.CalcTextSize(cyclesText).X;
                                    var availableWidth = ImGui.GetContentRegionAvail().X;
                                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, availableWidth - cyclesTextWidth));
                                    ImGui.TextColored(disabledColorVector, cyclesText);
                                }

                                break;

                            case DisassemblyLineType.Text:
                            case DisassemblyLineType.LineSeparator:
                            case DisassemblyLineType.Ellipsis:
                                // Labels (Text) start in the gutter column, flush with the
                                // breakpoint markers; separators and ellipses stay in the
                                // address column.
                                ImGui.TableNextColumn();
                                if (line.Type != DisassemblyLineType.Text)
                                {
                                    ImGui.TableNextColumn();
                                }
                                // These strings (labels especially) are wider than their
                                // starting column, and NoClip on the later columns doesn't
                                // stop the cell clip rect cutting them off - widen the clip
                                // rect to the whole row instead.
                                var textPos = ImGui.GetCursorScreenPos();
                                ImGui.PushClipRect(
                                    new Vector2(textPos.X, textPos.Y),
                                    new Vector2(textPos.X + availableSize.X, textPos.Y + rowHeight),
                                    false);
                                ImGui.TextColored(disabledColorVector, line.Text);
                                ImGui.PopClipRect();
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

        if (gotoIndexToScrollTo != null)
        {
            ScrollToIndex(gotoIndexToScrollTo.Value, lineHeight, availableSize);
        }

        if (lastPC != _previousPC)
        {
            // Scroll-lock only gates this auto-follow, not a goto-address jump
            // (above) - a user asking to go somewhere specific should always
            // get there, even with the lock on.
            if (!_scrollLocked)
            {
                // TODONT: Don't search whole array.
                var indexToScrollTo = _disassembly.FindIndex(x => x.Instruction?.AddressNumeric == lastPC);

                if (indexToScrollTo >= 0)
                {
                    ScrollToIndex(indexToScrollTo, lineHeight, availableSize);
                }
            }

            _previousPC = lastPC;
        }
    }

    private void ScrollToIndex(int index, float lineHeight, Vector2 availableSize)
    {
        ImGui.BeginChild("##disassembly_listing"u8);

        var lineTop = index * lineHeight;
        var lineBottom = lineTop + lineHeight;
        var scrollY = ImGui.GetScrollY();

        if (lineTop < scrollY || lineBottom > scrollY + availableSize.Y)
        {
            ImGui.SetScrollY(lineTop - availableSize.Y * 0.5f);
        }

        ImGui.EndChild();
    }

    // Fixed accent colors, one per OperandKind.
    private static Vector4 GetOperandKindColor(OperandKind kind) => kind switch
    {
        OperandKind.Immediate => new Vector4(0.60f, 0.90f, 0.60f, 1f),
        OperandKind.Address => new Vector4(0.55f, 0.75f, 1.00f, 1f),
        OperandKind.Indirect => new Vector4(0.90f, 0.75f, 0.40f, 1f),
        OperandKind.Register => new Vector4(0.80f, 0.80f, 1.00f, 1f),
        _ => ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Text)),
    };

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
