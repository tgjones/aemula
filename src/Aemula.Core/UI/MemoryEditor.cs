// Based on https://github.com/mellinoe/ImGui.NET/blob/0938c9882a9bffa089f72fb04091c4207e8578cb/src/ImGui.NET.SampleProgram/MemoryEditor.cs
//
// Licensed under the MIT license:
// 
// The MIT License (MIT)
// 
// Copyright(c) 2017 Eric Mellino and ImGui.NET contributors
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Globalization;
using System.Numerics;
using Aemula.Core;
using ImGuiNET;

namespace Aemula.Core.UI;

public sealed class MemoryEditor : DebuggerWindow
{
    private const int MemorySize = 0x10000;

    private readonly int base_display_addr = 0;

    private readonly Func<ushort, byte> _readMemoryCallback;
    private readonly Action<ushort, byte> _writeMemoryCallback;

    bool AllowEdits;
    int Rows;
    int DataEditingAddr;
    bool DataEditingTakeFocus;
    byte[] DataInput = new byte[32];
    byte[] AddrInput = new byte[32];

    public override string DisplayName { get; }

    public override Vector2 DefaultSize => new Vector2(500, 350);

    public MemoryEditor(
        int windowNumber,
        Func<ushort, byte> readMemoryCallback,
        Action<ushort, byte> writeMemoryCallback)
    {
        DisplayName = $"Memory Editor #{windowNumber}";

        _readMemoryCallback = readMemoryCallback;
        _writeMemoryCallback = writeMemoryCallback;

        Rows = 16;
        DataEditingAddr = -1;
        DataEditingTakeFocus = false;
        AllowEdits = true;
    }

    private static string FixedHex(int v, int count)
    {
        return v.ToString("X").PadLeft(count, '0');
    }

    private static bool TryHexParse(byte[] bytes, out int result)
    {
        var input = System.Text.Encoding.UTF8.GetString(bytes).ToString();
        return int.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.CurrentCulture, out result);
    }

    private static void ReplaceChars(byte[] bytes, string input)
    {
        var address = System.Text.Encoding.ASCII.GetBytes(input);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = i < address.Length ? address[i] : (byte)0;
        }
    }

    protected unsafe override void DrawOverride(EmulatorTime time)
    {
        var line_height = ImGuiNative.igGetTextLineHeight();
        var line_total_count = (MemorySize + Rows - 1) / Rows;

        ImGuiNative.igSetNextWindowContentSize(new Vector2(0.0f, line_total_count * line_height));
        ImGui.BeginChild("##scrolling", new Vector2(0, -ImGuiNative.igGetFrameHeightWithSpacing()), false, 0);

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        var addr_digits_count = 0;
        for (var n = base_display_addr + MemorySize - 1; n > 0; n >>= 4)
            addr_digits_count++;

        var glyph_width = ImGui.CalcTextSize("F").X;
        var cell_width = glyph_width * 3; // "FF " we include trailing space in the width to easily catch clicks everywhere

        var clipper = new ImGuiListClipper2(line_total_count, line_height);
        var visible_start_addr = clipper.DisplayStart * Rows;
        var visible_end_addr = clipper.DisplayEnd * Rows;

        var data_next = false;

        if (!AllowEdits || DataEditingAddr >= MemorySize)
            DataEditingAddr = -1;

        var data_editing_addr_backup = DataEditingAddr;

        if (DataEditingAddr != -1)
        {
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.UpArrow)) && DataEditingAddr >= Rows) { DataEditingAddr -= Rows; DataEditingTakeFocus = true; }
            else if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.DownArrow)) && DataEditingAddr < MemorySize - Rows) { DataEditingAddr += Rows; DataEditingTakeFocus = true; }
            else if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.LeftArrow)) && DataEditingAddr > 0) { DataEditingAddr -= 1; DataEditingTakeFocus = true; }
            else if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.RightArrow)) && DataEditingAddr < MemorySize - 1) { DataEditingAddr += 1; DataEditingTakeFocus = true; }
        }
        if (DataEditingAddr / Rows != data_editing_addr_backup / Rows)
        {
            // Track cursor movements
            var scroll_offset = (DataEditingAddr / Rows - data_editing_addr_backup / Rows) * line_height;
            var scroll_desired = scroll_offset < 0.0f && DataEditingAddr < visible_start_addr + Rows * 2 || scroll_offset > 0.0f && DataEditingAddr > visible_end_addr - Rows * 2;
            if (scroll_desired)
                ImGuiNative.igSetScrollYFloat(ImGuiNative.igGetScrollY() + scroll_offset);
        }

        for (var line_i = clipper.DisplayStart; line_i < clipper.DisplayEnd; line_i++) // display only visible items
        {
            var addr = line_i * Rows;
            ImGui.Text(FixedHex(base_display_addr + addr, addr_digits_count) + ": ");
            ImGui.SameLine();

            // Draw Hexadecimal
            var line_start_x = ImGuiNative.igGetCursorPosX();
            for (var n = 0; n < Rows && addr < MemorySize; n++, addr++)
            {
                ImGui.SameLine(line_start_x + cell_width * n);

                if (DataEditingAddr == addr)
                {
                    // Display text input on current byte
                    ImGui.PushID(addr);

                    // FIXME: We should have a way to retrieve the text edit cursor position more easily in the API, this is rather tedious.
                    ImGuiInputTextCallback callback = (data) =>
                    {
                        var p_cursor_pos = (int*)data->UserData;

                        if (ImGuiNative.ImGuiInputTextCallbackData_HasSelection(data) == 0)
                            *p_cursor_pos = data->CursorPos;
                        return 0;
                    };
                    var cursor_pos = -1;
                    var data_write = false;
                    if (DataEditingTakeFocus)
                    {
                        ImGui.SetKeyboardFocusHere();
                        ReplaceChars(DataInput, FixedHex(_readMemoryCallback((ushort)addr), 2));
                        ReplaceChars(AddrInput, FixedHex(base_display_addr + addr, addr_digits_count));
                    }
                    ImGui.PushItemWidth(ImGui.CalcTextSize("FF").X);

                    var flags = ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.NoHorizontalScroll | ImGuiInputTextFlags.CallbackAlways;

                    if (ImGui.InputText("##data", DataInput, 32, flags, callback, (nint)(&cursor_pos)))
                        data_write = data_next = true;
                    else if (!DataEditingTakeFocus && !ImGui.IsItemActive())
                        DataEditingAddr = -1;

                    DataEditingTakeFocus = false;
                    ImGui.PopItemWidth();
                    if (cursor_pos >= 2)
                        data_write = data_next = true;
                    if (data_write)
                    {
                        int data;
                        if (TryHexParse(DataInput, out data))
                            _writeMemoryCallback((ushort)addr, (byte)data);
                    }
                    ImGui.PopID();
                }
                else
                {
                    ImGui.Text(FixedHex(_readMemoryCallback((ushort)addr), 2));
                    if (AllowEdits && ImGui.IsItemHovered() && ImGui.IsMouseClicked(0))
                    {
                        DataEditingTakeFocus = true;
                        DataEditingAddr = addr;
                    }
                }
            }

            ImGui.SameLine(line_start_x + cell_width * Rows + glyph_width * 2);
            //separator line drawing replaced by printing a pipe char

            // Draw ASCII values
            addr = line_i * Rows;
            var asciiVal = new System.Text.StringBuilder(2 + Rows);
            asciiVal.Append("| ");
            for (var n = 0; n < Rows && addr < MemorySize; n++, addr++)
            {
                int c = _readMemoryCallback((ushort)addr);
                asciiVal.Append(c >= 32 && c < 128 ? Convert.ToChar(c) : '.');
            }
            ImGui.TextUnformatted(asciiVal.ToString());  //use unformatted, so string can contain the '%' character
        }
        //clipper.End();  //not implemented
        ImGui.PopStyleVar(2);

        ImGui.EndChild();

        if (data_next && DataEditingAddr < MemorySize)
        {
            DataEditingAddr = DataEditingAddr + 1;
            DataEditingTakeFocus = true;
        }

        ImGui.Separator();

        ImGuiNative.igAlignTextToFramePadding();
        ImGui.PushItemWidth(50);
        ImGui.PushAllowKeyboardFocus(true);
        var rows_backup = Rows;
        if (ImGui.DragInt("##rows", ref Rows, 0.2f, 4, 32, "%.0f rows"))
        {
            if (Rows <= 0) Rows = 4;
            var new_window_size = ImGui.GetWindowSize();
            new_window_size.X += (Rows - rows_backup) * (cell_width + glyph_width);
            ImGui.SetWindowSize(new_window_size);
        }
        ImGui.PopAllowKeyboardFocus();
        ImGui.PopItemWidth();
        ImGui.SameLine();
        ImGui.Text(string.Format(" Range {0}..{1} ", FixedHex(base_display_addr, addr_digits_count),
            FixedHex(base_display_addr + MemorySize - 1, addr_digits_count)));
        ImGui.SameLine();
        ImGui.PushItemWidth(70);
        if (ImGui.InputText("##addr", AddrInput, 32, ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue, null))
        {
            int goto_addr;
            if (TryHexParse(AddrInput, out goto_addr))
            {
                goto_addr -= base_display_addr;
                if (goto_addr >= 0 && goto_addr < MemorySize)
                {
                    ImGui.BeginChild("##scrolling");
                    ImGui.SetScrollFromPosY(ImGui.GetCursorStartPos().Y + goto_addr / Rows * ImGuiNative.igGetTextLineHeight());
                    ImGui.EndChild();
                    DataEditingAddr = goto_addr;
                    DataEditingTakeFocus = true;
                }
            }
        }
        ImGui.PopItemWidth();
    }
}

//Not a proper translation, because ImGuiListClipper uses imgui's internal api.
//Thus SetCursorPosYAndSetupDummyPrevLine isn't reimplemented, but SetCursorPosY + SetNextWindowContentSize seems to be working well instead.
//TODO expose clipper through newer cimgui version
internal class ImGuiListClipper2
{
    public float StartPosY;
    public float ItemsHeight;
    public int ItemsCount, StepNo, DisplayStart, DisplayEnd;

    public ImGuiListClipper2(int items_count = -1, float items_height = -1.0f)
    {
        Begin(items_count, items_height);
    }

    public unsafe void Begin(int count, float items_height = -1.0f)
    {
        StartPosY = ImGuiNative.igGetCursorPosY();
        ItemsHeight = items_height;
        ItemsCount = count;
        StepNo = 0;
        DisplayEnd = DisplayStart = -1;
        if (ItemsHeight > 0.0f)
        {
            int dispStart, dispEnd;
            ImGuiNative.igCalcListClipping(ItemsCount, ItemsHeight, &dispStart, &dispEnd);
            DisplayStart = dispStart;
            DisplayEnd = dispEnd;
            if (DisplayStart > 0)
                //SetCursorPosYAndSetupDummyPrevLine(StartPosY + DisplayStart * ItemsHeight, ItemsHeight); // advance cursor
                ImGuiNative.igSetCursorPosY(StartPosY + DisplayStart * ItemsHeight);
            StepNo = 2;
        }
    }
}
