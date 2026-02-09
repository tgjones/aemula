// Based on https://github.com/ocornut/imgui_club/blob/1e7facddfd50ba9ce4b75477b111d611c439076e/imgui_memory_editor/imgui_memory_editor.h
//
// Mini memory editor for Dear ImGui (to embed in your game/tools)
// Get latest version at http://www.github.com/ocornut/imgui_club
// Licensed under The MIT License (MIT)

// Right-click anywhere to access the Options menu!
// You can adjust the keyboard repeat delay/rate in ImGuiIO.
// The code assume a mono-space font for simplicity!
// If you don't use the default font, use ImGui::PushFont()/PopFont() to switch to a mono-space font before calling this.

using System;
using System.Buffers;
using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Unicode;
using Hexa.NET.ImGui;

using DataType = nint;

namespace Aemula.UI;

public sealed class MemoryEditor : DebuggerWindow
{
    private const int MemorySize = 0x10000;

    /// <summary>
    /// Disable any editing.
    /// </summary>
    private const bool ReadOnly = false;

    /// <summary>
    /// Number of columns to display.
    /// </summary>
    private int Cols = 16;

    /// <summary>
    /// Display options button/context menu. When disabled, options will be locked 
    /// unless you provide your own UI for them.
    /// </summary>
    private const bool OptShowOptions = true;

    /// <summary>
    /// Display a footer previewing the decimal/binary/hex/float representation of the 
    /// currently selected bytes.
    /// </summary>
    private bool OptShowDataPreview = false;

    /// <summary>
    /// Display values in HexII representation instead of regular hexadecimal: 
    /// hide null/zero bytes, ascii values as ".X".
    /// </summary>
    private bool OptShowHexII = false;

    /// <summary>
    /// Display ASCII representation on the right side.
    /// </summary>
    private bool OptShowAscii = true;

    /// <summary>
    /// Display null/zero bytes using the TextDisabled color.
    /// </summary>
    private bool OptGreyOutZeroes = true;

    /// <summary>
    /// Display hexadecimal values as "FF" instead of "ff".
    /// </summary>
    private bool OptUpperCaseHex = true;

    /// <summary>
    /// Set to 0 to disable extra spacing between every mid-cols.
    /// </summary>
    private const int OptMidColsCount = 8;

    /// <summary>
    /// Number of addr digits to display (default calculated based on maximum displayed addr).
    /// </summary>
    private const int OptAddrDigitsCount = 0;

    /// <summary>
    /// Space to reserve at the bottom of the widget to add custom widgets.
    /// </summary>
    private const float OptFooterExtraHeight = 0;

    /// <summary>
    /// Background color of highlighted bytes.
    /// </summary>
    private const uint HighlightColor = 0x32FFFFFF;

    private readonly int base_display_addr = 0;

    private readonly Func<DataType, byte> _readMemoryCallback;
    private readonly Action<DataType, byte> _writeMemoryCallback;
    private readonly Func<DataType, bool>? _highlightCallback = null;
    private readonly Func<DataType, uint>? _bgColorCallback = null;

    /// <summary>
    /// Set when mouse is hovering a value.
    /// </summary>
    private bool MouseHovered;

    /// <summary>
    /// The address currently being hovered if <see cref="_mouseHovered"/> is set.
    /// </summary>
    private DataType MouseHoveredAddr;

    private bool ContentsWidthChanged;
    private DataType DataPreviewAddr;
    private DataType DataEditingAddr;
    private bool DataEditingTakeFocus;

    private readonly byte[] DataInputBuf = new byte[32];
    private readonly byte[] AddrInputBuf = new byte[32];

    private DataType GotoAddr;
    private DataType HighlightMin, HighlightMax;
    private int PreviewEndianness;
    private ImGuiDataType PreviewDataType;

    public override string DisplayName { get; }

    public override Vector2 DefaultSize => new Vector2(500, 350);

    public MemoryEditor(
        int windowNumber,
        Func<DataType, byte> readMemoryCallback,
        Action<DataType, byte> writeMemoryCallback)
    {
        DisplayName = $"Memory Editor #{windowNumber}";

        _readMemoryCallback = readMemoryCallback;
        _writeMemoryCallback = writeMemoryCallback;

        DataPreviewAddr = DataEditingAddr = DataType.MaxValue;
        GotoAddr = DataType.MaxValue;
        HighlightMin = HighlightMax = DataType.MaxValue;
        PreviewDataType = ImGuiDataType.S32;
    }

    private void GotoAddrAndHighlight(DataType addrMin, DataType addrMax)
    {
        GotoAddr = addrMin;
        HighlightMin = addrMin;
        HighlightMax = addrMax;
    }

    private struct Sizes
    {
        /// <summary>
        /// Number of digits required to represent maximum address.
        /// </summary>
        public int AddrDigitsCount;

        /// <summary>
        /// Height of each line (no spacing).
        /// </summary>
        public float LineHeight;

        /// <summary>
        /// Glyph width (assume mono-space).
        /// </summary>
        public float GlyphWidth;

        /// <summary>
        /// Width of a hex edit cell ~2.5f * GlyphWidth.
        /// </summary>
        public float HexCellWidth;

        /// <summary>
        /// Spacing between each columns section (OptMidColsCount).
        /// </summary>
        public float SpacingBetweenMidCols;

        public float OffsetHexMinX;
        public float OffsetHexMaxX;
        public float OffsetAsciiMinX;
        public float OffsetAsciiMaxX;

        /// <summary>
        /// Ideal window width.
        /// </summary>
        public float WindowWidth;
    }

    private Sizes CalcSizes(DataType memSize, DataType baseDisplayAddr)
    {
        var style = ImGui.GetStyle();
        var s = new Sizes();
        s.AddrDigitsCount = OptAddrDigitsCount;
        if (s.AddrDigitsCount == 0)
        {
            for (var n = baseDisplayAddr + memSize - 1; n > 0; n >>= 4)
            {
                s.AddrDigitsCount++;
            }
        }
        s.LineHeight = ImGui.GetTextLineHeight();
        s.GlyphWidth = ImGui.CalcTextSize("F").X - 1;                // We assume the font is mono-space
        s.HexCellWidth = (int)(s.GlyphWidth * 2.5f);            // "FF " we include trailing space in the width to easily catch clicks everywhere
        s.SpacingBetweenMidCols = (int)(s.HexCellWidth * 2.5f); // Every OptMidColsCount columns we add a bit of extra spacing
        s.OffsetHexMinX = (s.AddrDigitsCount + 2) * s.GlyphWidth;
        s.OffsetHexMaxX = s.OffsetHexMinX + (s.HexCellWidth * Cols);
        s.OffsetAsciiMinX = s.OffsetAsciiMaxX = s.OffsetHexMaxX;
        if (OptShowAscii)
        {
            s.OffsetAsciiMinX = s.OffsetHexMaxX + s.GlyphWidth * 1;
            if (OptMidColsCount > 0)
            {
                s.OffsetAsciiMinX += (float)((Cols + OptMidColsCount - 1) / OptMidColsCount) * s.SpacingBetweenMidCols;
            }
            s.OffsetAsciiMaxX = s.OffsetAsciiMinX + (Cols * s.GlyphWidth);
        }
        s.WindowWidth = s.OffsetAsciiMaxX + style.ScrollbarSize + style.WindowPadding.X * 2 + s.GlyphWidth;
        return s;
    }

    private string FixedHex(DataType v, int count)
    {
        return v.ToString(OptUpperCaseHex ? "X" : "x").PadLeft(count, '0');
    }

    private static bool TryHexParse(byte[] bytes, out DataType result)
    {
        var input = System.Text.Encoding.UTF8.GetString(bytes).ToString();
        return DataType.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.CurrentCulture, out result);
    }

    private static void ReplaceChars(byte[] bytes, string input)
    {
        var address = System.Text.Encoding.ASCII.GetBytes(input);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = i < address.Length ? address[i] : (byte)0;
        }
    }

    private static readonly string[] descs = { "Int8", "Uint8", "Int16", "Uint16", "Int32", "Uint32", "Int64", "Uint64", "Float", "Double" };

    private static string DataTypeGetDesc(ImGuiDataType data_type)
    {
        Debug.Assert(data_type >= 0 && (int)data_type < descs.Length);
        return descs[(int)data_type];
    }

    private static readonly DataType[] DataTypeSizes = { 1, 1, 2, 2, 4, 4, 8, 8, sizeof(float), sizeof(double) };

    private static DataType DataTypeGetSize(ImGuiDataType data_type)
    {
        Debug.Assert(data_type >= 0 && (int)data_type < DataTypeSizes.Length);
        return DataTypeSizes[(int)data_type];
    }

    protected unsafe override void DrawOverride(EmulatorTime time)
    {
        var s = CalcSizes(MemorySize, base_display_addr);
        var style = ImGui.GetStyle();

        var contents_pos_start = ImGui.GetCursorScreenPos();

        // We begin into our scrolling region with the 'ImGuiWindowFlags_NoMove' in order to prevent click from moving the window.
        // This is used as a facility since our main click detection code doesn't assign an ActiveId so the click would normally be caught as a window-move.
        var heightSeparator = style.ItemSpacing.Y;
        var footerHeight = OptFooterExtraHeight;
        if (OptShowOptions)
        {
            footerHeight += heightSeparator + ImGui.GetFrameHeightWithSpacing() * 1;
        }
        if (OptShowDataPreview)
        {
            footerHeight += heightSeparator + ImGui.GetFrameHeightWithSpacing() * 1 + ImGui.GetTextLineHeightWithSpacing() * 3;
        }
        ImGui.BeginChild("##scrolling"u8, new Vector2(-float.Epsilon, -footerHeight), ImGuiChildFlags.None, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav);
        var drawList = ImGui.GetWindowDrawList();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        // We are not really using the clipper API correctly here, because we rely on visible_start_addr/visible_end_addr for our scrolling function.
        var avail_size = ImGui.GetContentRegionAvail();
        var line_total_count = (int)((MemorySize + Cols - 1) / Cols);
        var clipper = new ImGuiListClipper();
        clipper.Begin(line_total_count, s.LineHeight);

        bool data_next = false;

        if (DataEditingAddr >= MemorySize)
            DataEditingAddr = DataType.MaxValue;
        if (DataPreviewAddr >= MemorySize)
            DataPreviewAddr = DataType.MaxValue;

        var preview_data_type_size = OptShowDataPreview ? DataTypeGetSize(PreviewDataType) : 0;

        var data_editing_addr_next = DataType.MaxValue;
        if (DataEditingAddr != DataType.MaxValue)
        {
            // Move cursor but only apply on next frame so scrolling with be synchronized (because currently we can't change the scrolling while the window is being rendered)
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) && DataEditingAddr >= Cols) { data_editing_addr_next = DataEditingAddr - Cols; }
            else if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) && DataEditingAddr < MemorySize - Cols) { data_editing_addr_next = DataEditingAddr + Cols; }
            else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) && DataEditingAddr > 0) { data_editing_addr_next = DataEditingAddr - 1; }
            else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) && DataEditingAddr < MemorySize - 1) { data_editing_addr_next = DataEditingAddr + 1; }
        }

        // Draw vertical separator
        var window_pos = ImGui.GetWindowPos();
        if (OptShowAscii)
            drawList.AddLine(new Vector2(window_pos.X + s.OffsetAsciiMinX - s.GlyphWidth, window_pos.Y), new Vector2(window_pos.X + s.OffsetAsciiMinX - s.GlyphWidth, window_pos.Y + 9999), ImGui.GetColorU32(ImGuiCol.Border));

        var color_text = ImGui.GetColorU32(ImGuiCol.Text);
        var color_disabled = OptGreyOutZeroes ? ImGui.GetColorU32(ImGuiCol.TextDisabled) : color_text;

        //var format_byte = OptUpperCaseHex ? "%02X" : "%02x";
        //var format_byte_space = OptUpperCaseHex ? "%02X " : "%02x ";

        MouseHovered = false;
        MouseHoveredAddr = 0;

        while (clipper.Step())
            for (int line_i = clipper.DisplayStart; line_i < clipper.DisplayEnd; line_i++) // display only visible lines
            {
                var addr = (DataType)line_i * Cols;
                ImGui.Text(FixedHex(base_display_addr + addr, s.AddrDigitsCount) + ": ");

                // Draw Hexadecimal
                for (int n = 0; n < Cols && addr < MemorySize; n++, addr++)
                {
                    float byte_pos_x = s.OffsetHexMinX + s.HexCellWidth * n;
                    if (OptMidColsCount > 0)
                        byte_pos_x += (float)(n / OptMidColsCount) * s.SpacingBetweenMidCols;
                    ImGui.SameLine(byte_pos_x);

                    // Draw highlight or custom background color
                    var is_highlight_from_user_range = (addr >= HighlightMin && addr < HighlightMax);
                    var is_highlight_from_user_func = _highlightCallback != null && _highlightCallback(addr);
                    var is_highlight_from_preview = (addr >= DataPreviewAddr && addr < DataPreviewAddr + preview_data_type_size);

                    uint bg_color = 0;
                    bool is_next_byte_highlighted = false;
                    if (is_highlight_from_user_range || is_highlight_from_user_func || is_highlight_from_preview)
                    {
                        is_next_byte_highlighted = (addr + 1 < MemorySize) && ((HighlightMax != DataType.MaxValue && addr + 1 < HighlightMax) || (_highlightCallback != null && _highlightCallback(addr + 1)) || (addr + 1 < DataPreviewAddr + preview_data_type_size));
                        bg_color = HighlightColor;
                    }
                    else if (_bgColorCallback != null)
                    {
                        const uint IM_COL32_A_MASK = 0xFF000000;
                        is_next_byte_highlighted = (addr + 1 < MemorySize) && ((_bgColorCallback(addr + 1) & IM_COL32_A_MASK) != 0);
                        bg_color = _bgColorCallback(addr);
                    }
                    if (bg_color != 0)
                    {
                        float bg_width = s.GlyphWidth * 2;
                        if (is_next_byte_highlighted || (n + 1 == Cols))
                        {
                            bg_width = s.HexCellWidth;
                            if (OptMidColsCount > 0 && n > 0 && (n + 1) < Cols && ((n + 1) % OptMidColsCount) == 0)
                                bg_width += s.SpacingBetweenMidCols;
                        }
                        var pos = ImGui.GetCursorScreenPos();
                        drawList.AddRectFilled(pos, new Vector2(pos.X + bg_width, pos.Y + s.LineHeight), bg_color);
                    }

                    if (DataEditingAddr == addr)
                    {
                        // Display text input on current byte
                        bool data_write = false;
                        ImGui.PushID((void*)addr);
                        if (DataEditingTakeFocus)
                        {
                            ImGui.SetKeyboardFocusHere(0);
                            ReplaceChars(AddrInputBuf, FixedHex(base_display_addr + addr, s.AddrDigitsCount));
                            ReplaceChars(DataInputBuf, FixedHex(_readMemoryCallback(addr), 2));
                        }

                        //var input_text_user_data = new InputTextUserData();
                        //input_text_user_data.CursorPos = -1;

                        var currentBufOverwrite = new byte[3];
                        ReplaceChars(currentBufOverwrite, FixedHex(_readMemoryCallback(addr), 2));

                        var cursorPos = -1;

                        unsafe int Callback(ImGuiInputTextCallbackData* data)
                        {
                            if (!data->HasSelection())
                                cursorPos = data->CursorPos;
                            if (data->SelectionStart == 0 && data->SelectionEnd == data->BufTextLen)
                            {
                                // When not editing a byte, always refresh its InputText content pulled from underlying memory data
                                // (this is a bit tricky, since InputText technically "owns" the master copy of the buffer we edit it in there)
                                data->DeleteChars(0, data->BufTextLen);
                                data->InsertChars(0, currentBufOverwrite);
                                data->SelectionStart = 0;
                                data->SelectionEnd = 2;
                                data->CursorPos = 0;
                            }
                            return 0;
                        }

                        ImGuiInputTextFlags flags = ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.NoHorizontalScroll | ImGuiInputTextFlags.CallbackAlways;
                        if (ReadOnly)
                            flags |= ImGuiInputTextFlags.ReadOnly;
                        flags |= ImGuiInputTextFlags.AlwaysOverwrite; // was ImGuiInputTextFlags.AlwaysInsertMode
                        ImGui.SetNextItemWidth(s.GlyphWidth * 2);
                        fixed (byte* dataInputBuf = &DataInputBuf[0])
                        {
                            if (ImGui.InputText("##data", dataInputBuf, (nuint)DataInputBuf.Length, flags, Callback))
                                data_write = data_next = true;
                            else if (!DataEditingTakeFocus && !ImGui.IsItemActive())
                                DataEditingAddr = data_editing_addr_next = DataType.MaxValue;
                        }
                        DataEditingTakeFocus = false;
                        if (cursorPos >= 2)
                            data_write = data_next = true;
                        if (data_editing_addr_next != DataType.MaxValue)
                            data_write = data_next = false;
                        if (!ReadOnly && data_write && TryHexParse(DataInputBuf, out var data_input_value))
                        {
                            _writeMemoryCallback(addr, (byte)data_input_value);
                        }
                        if (ImGui.IsItemHovered())
                        {
                            MouseHovered = true;
                            MouseHoveredAddr = addr;
                        }
                        ImGui.PopID();
                    }
                    else
                    {
                        // NB: The trailing space is not visible but ensure there's no gap that the mouse cannot click on.
                        var b = _readMemoryCallback(addr);

                        if (OptShowHexII)
                        {
                            if ((b >= 32 && b < 128))
                                ImGui.Text($".{(char)b} ");
                            else if (b == 0xFF && OptGreyOutZeroes)
                                ImGui.TextDisabled("## ");
                            else if (b == 0x00)
                                ImGui.Text("   ");
                            else
                                ImGui.Text(FixedHex(b, 2) + " ");
                        }
                        else
                        {
                            if (b == 0 && OptGreyOutZeroes)
                                ImGui.TextDisabled("00 ");
                            else
                                ImGui.Text(FixedHex(b, 2) + " ");
                        }
                        if (ImGui.IsItemHovered())
                        {
                            MouseHovered = true;
                            MouseHoveredAddr = addr;
                            if (ImGui.IsMouseClicked(0))
                            {
                                DataEditingTakeFocus = true;
                                data_editing_addr_next = addr;
                            }
                        }
                    }
                }

                if (OptShowAscii)
                {
                    // Draw ASCII values
                    ImGui.SameLine(s.OffsetAsciiMinX);
                    var pos = ImGui.GetCursorScreenPos();
                    addr = (DataType)line_i * Cols;

                    var mouse_off_x = ImGui.GetIO().MousePos.X - pos.X;
                    var mouse_addr = (mouse_off_x >= 0.0f && mouse_off_x < s.OffsetAsciiMaxX - s.OffsetAsciiMinX) ? addr + (DataType)(mouse_off_x / s.GlyphWidth) : DataType.MaxValue;

                    ImGui.PushID(line_i);
                    if (ImGui.InvisibleButton("ascii", new Vector2(s.OffsetAsciiMaxX - s.OffsetAsciiMinX, s.LineHeight)))
                    {
                        DataEditingAddr = DataPreviewAddr = mouse_addr;
                        DataEditingTakeFocus = true;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        MouseHovered = true;
                        MouseHoveredAddr = mouse_addr;
                    }
                    ImGui.PopID();
                    for (int n = 0; n < Cols && addr < MemorySize; n++, addr++)
                    {
                        if (addr == DataEditingAddr)
                        {
                            drawList.AddRectFilled(pos, new Vector2(pos.X + s.GlyphWidth, pos.Y + s.LineHeight), ImGui.GetColorU32(ImGuiCol.FrameBg));
                            drawList.AddRectFilled(pos, new Vector2(pos.X + s.GlyphWidth, pos.Y + s.LineHeight), ImGui.GetColorU32(ImGuiCol.TextSelectedBg));
                        }
                        else if (_bgColorCallback != null)
                        {
                            drawList.AddRectFilled(pos, new Vector2(pos.X + s.GlyphWidth, pos.Y + s.LineHeight), _bgColorCallback(addr));
                        }
                        byte c = _readMemoryCallback(addr);
                        byte display_c = (c < 32 || c >= 128) ? (byte)'.' : c;
                        drawList.AddText(pos, (display_c == c) ? color_text : color_disabled, &display_c, &display_c + 1);
                        pos.X += s.GlyphWidth;
                    }
                }
            }
        ImGui.PopStyleVar(2);
        var child_width = ImGui.GetWindowSize().X;
        ImGui.EndChild();

        // Notify the main window of our ideal child content size (FIXME: we are missing an API to get the contents size from the child)
        var backup_pos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorPosX(s.WindowWidth);
        ImGui.Dummy(new Vector2(0.0f, 0.0f));
        ImGui.SetCursorScreenPos(backup_pos);

        if (data_next && DataEditingAddr + 1 < MemorySize)
        {
            DataEditingAddr = DataPreviewAddr = DataEditingAddr + 1;
            DataEditingTakeFocus = true;
        }
        else if (data_editing_addr_next != DataType.MaxValue)
        {
            DataEditingAddr = DataPreviewAddr = data_editing_addr_next;
            DataEditingTakeFocus = true;
        }

        var lock_show_data_preview = OptShowDataPreview;
        if (OptShowOptions)
        {
            ImGui.Separator();
            DrawOptionsLine(s);
        }

        if (lock_show_data_preview)
        {
            ImGui.Separator();
            DrawPreviewLine(s);
        }

        if (GotoAddr != DataType.MaxValue)
        {
            if (GotoAddr < MemorySize)
            {
                ImGui.BeginChild("##scrolling");
                ImGui.SetScrollY((GotoAddr / Cols) * ImGui.GetTextLineHeight() - avail_size.Y * 0.5f);
                ImGui.EndChild();
                DataEditingAddr = DataPreviewAddr = GotoAddr;
                DataEditingTakeFocus = true;
            }
            GotoAddr = DataType.MaxValue;
        }

        var contents_pos_end = new Vector2(contents_pos_start.X + child_width, ImGui.GetCursorScreenPos().Y);
        //ImGui.GetForegroundDrawList()->AddRect(contents_pos_start, contents_pos_end, IM_COL32(255, 0, 0, 255));
        if (OptShowOptions)
            if (ImGui.IsMouseHoveringRect(contents_pos_start, contents_pos_end))
                if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                    ImGui.OpenPopup("OptionsPopup");

        if (ImGui.BeginPopup("OptionsPopup"))
        {
            ImGui.SetNextItemWidth(s.GlyphWidth * 7 + style.FramePadding.X * 2.0f);
            if (ImGui.DragInt("##cols", ref Cols, 0.2f, 4, 32, "%d cols")) { ContentsWidthChanged = true; if (Cols < 1) Cols = 1; }
            ImGui.Checkbox("Show Data Preview", ref OptShowDataPreview);
            ImGui.Checkbox("Show HexII", ref OptShowHexII);
            if (ImGui.Checkbox("Show Ascii", ref OptShowAscii)) { ContentsWidthChanged = true; }
            ImGui.Checkbox("Grey out zeroes", ref OptGreyOutZeroes);
            ImGui.Checkbox("Uppercase Hex", ref OptUpperCaseHex);

            ImGui.EndPopup();
        }
    }

    private unsafe void DrawOptionsLine(in Sizes s)
    {
        var style = ImGui.GetStyle();

        // Options menu
        if (ImGui.Button("Options"))
            ImGui.OpenPopup("OptionsPopup");

        ImGui.SameLine();
        ImGui.Text($"Range {FixedHex(base_display_addr, s.AddrDigitsCount)}..{FixedHex(base_display_addr + MemorySize - 1, s.AddrDigitsCount)}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth((s.AddrDigitsCount + 1) * s.GlyphWidth + style.FramePadding.X * 2.0f);
        fixed (byte* addrInputBufPtr = &AddrInputBuf[0])
        {
            if (ImGui.InputText("##addr", addrInputBufPtr, (nuint)AddrInputBuf.Length, ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (TryHexParse(AddrInputBuf, out var goto_addr))
                {
                    GotoAddr = goto_addr - base_display_addr;
                    HighlightMin = HighlightMax = DataType.MaxValue;
                }
            }
        }

        //if (MouseHovered)
        //{
        //    ImGui::SameLine();
        //    ImGui::Text("Hovered: %p", MouseHoveredAddr);
        //}
    }

    private static readonly ImGuiDataType[] supported_data_types = { ImGuiDataType.S8, ImGuiDataType.U8, ImGuiDataType.S16, ImGuiDataType.U16, ImGuiDataType.S32, ImGuiDataType.U32, ImGuiDataType.S64, ImGuiDataType.U64, ImGuiDataType.Float, ImGuiDataType.Double };

    private void DrawPreviewLine(in Sizes s)
    {
        var style = ImGui.GetStyle();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Preview as:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth((s.GlyphWidth * 10.0f) + style.FramePadding.X * 2.0f + style.ItemInnerSpacing.X);

        if (ImGui.BeginCombo("##combo_type", DataTypeGetDesc(PreviewDataType), ImGuiComboFlags.HeightLargest))
        {
            for (int n = 0; n < supported_data_types.Length; n++)
            {
                ImGuiDataType data_type = supported_data_types[n];
                if (ImGui.Selectable(DataTypeGetDesc(data_type), PreviewDataType == data_type))
                    PreviewDataType = data_type;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth((s.GlyphWidth * 6.0f) + style.FramePadding.X * 2.0f + style.ItemInnerSpacing.X);
        ImGui.Combo("##combo_endianness", ref PreviewEndianness, "LE\0BE\0\0");

        Span<byte> buf = stackalloc byte[128];
        float x = s.GlyphWidth * 6.0f;
        bool has_value = DataPreviewAddr != DataType.MaxValue;
        if (has_value)
            DrawPreviewData(DataPreviewAddr, PreviewDataType, DataFormat.Dec, buf);
        ReadOnlySpan<byte> naBuf = "N/A"u8;
        ImGui.Text("Dec"); ImGui.SameLine(x); ImGui.TextUnformatted(has_value ? buf : naBuf);
        if (has_value)
            DrawPreviewData(DataPreviewAddr, PreviewDataType, DataFormat.Hex, buf);
        ImGui.Text("Hex"); ImGui.SameLine(x); ImGui.TextUnformatted(has_value ? buf : naBuf);
        if (has_value)
            DrawPreviewData(DataPreviewAddr, PreviewDataType, DataFormat.Bin, buf);
        buf[buf.Length - 1] = 0;
        ImGui.Text("Bin"); ImGui.SameLine(x); ImGui.TextUnformatted(has_value ? buf : naBuf);
    }

    private unsafe void DrawPreviewData(DataType addr, ImGuiDataType data_type, DataFormat dataFormat, Span<byte> out_buf)
    {
        // TODO: Don't do this, it's slow.
        out_buf.Clear();

        var elem_size = DataTypeGetSize(data_type);
        var size = addr + elem_size > MemorySize ? MemorySize - addr : elem_size;
        if (size > 8)
        {
            throw new InvalidOperationException();
        }
        Span<byte> buf = stackalloc byte[(int)size];
        for (int i = 0, n = (int)size; i < n; ++i)
            buf[i] = _readMemoryCallback(addr + i);

        var wantsLittleEndian = PreviewEndianness == 0;
        if (BitConverter.IsLittleEndian != wantsLittleEndian)
        {
            buf.Reverse();
        }

        if (dataFormat == DataFormat.Bin)
        {
            Span<byte> binbuf = stackalloc byte[8];
            var pos = 0;
            buf.Reverse();
            foreach (var b in buf)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    out_buf[pos++] = (byte)(((b >> bit) & 1) + '0');
                }
                out_buf[pos++] = (byte)' ';
            }
            return;
        }

        if (dataFormat == DataFormat.Hex && (data_type == ImGuiDataType.Float || data_type == ImGuiDataType.Double))
        {
            out_buf[0] = (byte)'T';
            out_buf[1] = (byte)'O';
            out_buf[2] = (byte)'D';
            out_buf[3] = (byte)'O';
            return;
        }

        var format = new StandardFormat(
            dataFormat == DataFormat.Hex
                ? OptUpperCaseHex ? 'X' : 'x'
                : 'G',
            dataFormat == DataFormat.Hex
                ? (byte)(elem_size * 2)
                : (byte)255);

        if (dataFormat == DataFormat.Hex)
        {
            out_buf[0] = (byte)'0';
            out_buf[1] = (byte)'x';
            // We will write the value after "0x"
            out_buf = out_buf[2..];
        }

        out_buf[0] = 0;
        switch (data_type)
        {
           case ImGuiDataType.S8:
                Utf8Formatter.TryFormat((sbyte)buf[0], out_buf, out _, format);
                break;

            case ImGuiDataType.U8:
                Utf8Formatter.TryFormat(buf[0], out_buf, out _, format);
                break;

            case ImGuiDataType.S16:
                Utf8Formatter.TryFormat(BitConverter.ToInt16(buf), out_buf, out _, format);
                break;

            case ImGuiDataType.U16:
                Utf8Formatter.TryFormat(BitConverter.ToUInt16(buf), out_buf, out _, format);
                break;

            case ImGuiDataType.S32:
                Utf8Formatter.TryFormat(BitConverter.ToInt32(buf), out_buf, out _, format);
                break;

            case ImGuiDataType.U32:
                Utf8Formatter.TryFormat(BitConverter.ToUInt32(buf), out_buf, out _, format);
                break;
            
            case ImGuiDataType.S64:
                Utf8Formatter.TryFormat(BitConverter.ToInt64(buf), out_buf, out _, format);
                break;

            case ImGuiDataType.U64:
                Utf8Formatter.TryFormat(BitConverter.ToUInt64(buf), out_buf, out _, format);
                break;

            case ImGuiDataType.Float:
                Utf8Formatter.TryFormat(BitConverter.ToSingle(buf), out_buf, out _, format);
                break;
            
            case ImGuiDataType.Double:
                Utf8Formatter.TryFormat(BitConverter.ToDouble(buf), out_buf, out _, format);
                break;

            default:
                throw new InvalidOperationException();
        }
    }

    private enum DataFormat
    {
        Bin,
        Dec,
        Hex,
    }
}
