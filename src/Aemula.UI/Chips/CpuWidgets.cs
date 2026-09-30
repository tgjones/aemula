using System;
using System.Numerics;
using Hexa.NET.ImGui;

namespace Aemula.UI.Chips;

/// <summary>
/// Drawing helpers shared by the CPU state windows, so every CPU debugger has the
/// same look: coloured register rows with a binary readout, read-only flag
/// badges and section headers.
/// </summary>
internal static class CpuWidgets
{
    public static class Theme
    {
        public static readonly uint Label = Rgb(0x5F, 0xD3, 0xE6);
        public static readonly uint Value = Rgb(0xF2, 0xF2, 0xF2);
        public static readonly uint Dim = Rgb(0x70, 0x72, 0x7C);
        public static readonly uint Header = Rgb(0xF2, 0x9C, 0x38);
        public static readonly uint On = Rgb(0x5F, 0xE0, 0x50);
        public static readonly uint CardBackground = Rgb(0x1E, 0x20, 0x27);
        public static readonly uint CardBorder = Rgb(0x3A, 0x3D, 0x48);
    }

    public static uint Rgb(byte r, byte g, byte b) => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;

    /// <summary>Width of one character; the debugger font is monospaced.</summary>
    public static float CharWidth => ImGui.CalcTextSize("0").X;

    public static float RowHeight => ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2;

    /// <summary>Width of a <see cref="RegisterRow"/> for a register of the given bit count.</summary>
    public static float RegisterRowWidth(int bits)
    {
        // Label column, hex column, binary column, and padding.
        return CharWidth * (LabelColumns + bits / 4 + 2 + BinaryColumns(bits) + 1);
    }

    private const int LabelColumns = 4;

    // Width of the binary readout in characters: a half-character gap between
    // nibbles and a character and a half between bytes (see DrawBinary).
    private static float BinaryColumns(int bits)
    {
        var byteGaps = bits / 8 - 1;
        var nibbleGaps = bits / 4 - 1 - byteGaps;
        return bits + nibbleGaps * 0.5f + byteGaps * 1.5f;
    }

    /// <summary>
    /// A section title in the header colour, with a line under it.
    /// </summary>
    public static void Section(string title, float width)
    {
        ImGui.Dummy(new Vector2(0, ImGui.GetStyle().ItemSpacing.Y * 0.5f));

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var textHeight = ImGui.GetFontSize();

        drawList.AddText(origin, Theme.Header, title);

        var lineY = origin.Y + textHeight + 2;
        drawList.AddLine(new Vector2(origin.X, lineY), new Vector2(origin.X + width, lineY), Theme.CardBorder);

        ImGui.Dummy(new Vector2(width, textHeight + 6));
    }

    /// <summary>
    /// One register as a card: label, hex value, then the value in binary, with
    /// set bits bright and clear bits dim.
    /// </summary>
    public static void RegisterRow(string label, uint value, int bits, float width)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var height = RowHeight;
        var charWidth = CharWidth;
        var pad = ImGui.GetStyle().FramePadding.X;

        drawList.AddRectFilled(origin, origin + new Vector2(width, height), Theme.CardBackground);
        drawList.AddRect(origin, origin + new Vector2(width, height), Theme.CardBorder);

        var textY = origin.Y + (height - ImGui.GetFontSize()) * 0.5f;
        var x = origin.X + pad;

        drawList.AddText(new Vector2(x, textY), Theme.Label, label);
        x += charWidth * LabelColumns;

        var hexDigits = bits / 4;
        drawList.AddText(new Vector2(x, textY), Theme.Value, value.ToString($"X{hexDigits}"));
        x += charWidth * (hexDigits + 2);

        DrawBinary(drawList, new Vector2(x, textY), value, bits, charWidth);

        ImGui.Dummy(new Vector2(width, height));
    }

    private static void DrawBinary(ImDrawListPtr drawList, Vector2 position, uint value, int bits, float charWidth)
    {
        var x = position.X;

        for (var bit = bits - 1; bit >= 0; bit--)
        {
            var set = ((value >> bit) & 1) != 0;
            drawList.AddText(new Vector2(x, position.Y), set ? Theme.Value : Theme.Dim, set ? "1" : "0");
            x += charWidth;

            if (bit == 0)
            {
                break;
            }

            if (bit % 8 == 0)
            {
                x += charWidth * 1.5f;
            }
            else if (bit % 4 == 0)
            {
                x += charWidth * 0.5f;
            }
        }
    }

    /// <summary>A dim key followed by a bright value, on one line.</summary>
    public static void KeyValue(string key, string value)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Dim);
        ImGui.Text(key);
        ImGui.PopStyleColor();

        ImGui.SameLine(0, CharWidth);

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Value);
        ImGui.Text(value);
        ImGui.PopStyleColor();
    }

    /// <summary>One cell in a <see cref="FlagRow"/>.</summary>
    public readonly record struct FlagCell(string Label, string Name, string ValueText, bool IsSet);

    public static FlagCell Flag(string label, string name, bool isSet) => new(label, name, isSet ? "1" : "0", isSet);

    /// <summary>A placeholder cell for a P/F bit that has no state (e.g. the unused 6502 bit).</summary>
    public static FlagCell UnusedFlag(string label, string name) => new(label, name, "-", false);

    /// <summary>
    /// A read-only row of flag badges: the flag's letter above its value, with
    /// the value green while the flag is set. Hovering shows the full name.
    /// </summary>
    public static void FlagRow(ReadOnlySpan<FlagCell> cells, float width)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var cellWidth = width / cells.Length;
        var fontSize = ImGui.GetFontSize();
        var height = fontSize * 2 + ImGui.GetStyle().FramePadding.Y * 3;

        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var min = origin + new Vector2(cellWidth * i, 0);
            var max = min + new Vector2(cellWidth, height);

            drawList.AddRectFilled(min, max, Theme.CardBackground);
            drawList.AddRect(min, max, Theme.CardBorder);

            CenterText(drawList, min.X, cellWidth, min.Y + ImGui.GetStyle().FramePadding.Y, cell.Label, Theme.Header);
            CenterText(
                drawList,
                min.X,
                cellWidth,
                min.Y + ImGui.GetStyle().FramePadding.Y * 2 + fontSize,
                cell.ValueText,
                cell.IsSet ? Theme.On : Theme.Dim);

            if (ImGui.IsMouseHoveringRect(min, max))
            {
                ImGui.SetTooltip(cell.Name);
            }
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    private static void CenterText(ImDrawListPtr drawList, float x, float width, float y, string text, uint color)
    {
        var size = ImGui.CalcTextSize(text);
        drawList.AddText(new Vector2(x + (width - size.X) * 0.5f, y), color, text);
    }
}
