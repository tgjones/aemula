using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;

namespace Aemula.UI;

public static unsafe class ImGuiUtility
{
    private const int StackAllocationSizeLimit = 2048;

    public static void SetupDocking()
    {
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        // Need to reload .ini file as described in https://github.com/mellinoe/veldrid/issues/410
        ImGui.LoadIniSettingsFromDisk(ImGui.GetIO().IniFilename);
    }

    public static bool InputHex<T>(string label, uint length, ref T value, T? maximum = null)
        where T : struct, INumber<T>
    {
        var input = value.ToString($"X{length}", null);

        ImGui.PushItemWidth(CalcTextWidth(length));

        var flags = ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.EnterReturnsTrue;

        var result = false;
        if (ImGui.InputText(label, ref input, length, flags))
        {
            var tempValue = T.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (maximum == null || tempValue <= maximum.Value)
            {
                value = tempValue;
                result = true;
            }
            else
            {
                result = false;
            }
        }

        ImGui.PopItemWidth();

        return result;
    }

    public static Vector2 CalculateFrameDimensions(ReadOnlySpan<char> text, string[] otherTexts)
    {
        var size = CalcTextSize(text);

        for (var i = 0; i < otherTexts.Length; i++)
        {
            var size2 = ImGui.CalcTextSize(otherTexts[i]);
            if (size2.X > size.X)
            {
                size = size2;
            }
        }

        size.Y = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2.5f;

        size.X += ImGui.GetStyle().FramePadding.X * 2.5f;

        return size;
    }

    private static float CalcTextWidth(uint length)
    {
        Span<char> chars = stackalloc char[(int)length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = 'X';
        }

        return CalculateFrameDimensions(chars, Array.Empty<string>()).X;
    }

    private static unsafe Vector2 CalcTextSize(ReadOnlySpan<char> chars)
    {
        Vector2 result = default;

        var byteCount = Encoding.UTF8.GetByteCount(chars);

        Span<byte> bytes = stackalloc byte[byteCount + 1]; // Last byte is \0

        Encoding.UTF8.GetBytes(chars, bytes);

        fixed (byte* bytesPtr = bytes)
        {
            ImGuiNative.igCalcTextSize(&result, bytesPtr, null, 0, -1);
        }

        return result;
    }

    public static bool InputUInt16(string label, ref ushort value) => InputHex(label, 4, ref value);

    public static bool InputByte(string label, ref byte value, byte? maximum = null) => InputHex(label, 2, ref value, maximum);

    public static bool Combo(string label, ref int current_item, IReadOnlyList<string> items)
    {
        byte* native_label;
        var label_byteCount = 0;
        if (label != null)
        {
            label_byteCount = Encoding.UTF8.GetByteCount(label);
            if (label_byteCount > StackAllocationSizeLimit)
            {
                native_label = Allocate(label_byteCount + 1);
            }
            else
            {
                var native_label_stackBytes = stackalloc byte[label_byteCount + 1];
                native_label = native_label_stackBytes;
            }
            var native_label_offset = GetUtf8(label, native_label, label_byteCount);
            native_label[native_label_offset] = 0;
        }
        else { native_label = null; }
        var items_byteCounts = stackalloc int[items.Count];
        var items_byteCount = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var s = items[i];
            items_byteCounts[i] = Encoding.UTF8.GetByteCount(s);
            items_byteCount += items_byteCounts[i] + 1;
        }
        var native_items_data = stackalloc byte[items_byteCount];
        var offset = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var s = items[i];
            fixed (char* sPtr = s)
            {
                offset += Encoding.UTF8.GetBytes(sPtr, s.Length, native_items_data + offset, items_byteCounts[i]);
                native_items_data[offset] = 0;
                offset += 1;
            }
        }
        var native_items = stackalloc byte*[items.Count];
        offset = 0;
        for (var i = 0; i < items.Count; i++)
        {
            native_items[i] = &native_items_data[offset];
            offset += items_byteCounts[i] + 1;
        }
        var popup_max_height_in_items = -1;
        fixed (int* native_current_item = &current_item)
        {
            var ret = ImGuiNative.igComboStr_arr(native_label, native_current_item, native_items, items.Count, popup_max_height_in_items);
            if (label_byteCount > StackAllocationSizeLimit)
            {
                Free(native_label);
            }
            return ret != 0;
        }
    }

    private static byte* Allocate(int numBytes) => (byte*)Marshal.AllocHGlobal(numBytes);

    private static void Free(byte* ptr) => Marshal.FreeHGlobal((nint)ptr);

    private static int GetUtf8(string s, byte* utf8Bytes, int utf8ByteCount)
    {
        fixed (char* utf16Ptr = s)
        {
            return Encoding.UTF8.GetBytes(utf16Ptr, s.Length, utf8Bytes, utf8ByteCount);
        }
    }

    public static bool ComboEnum<T>(string label, ref T value, string[] options)
        where T : Enum
    {
        var currentItem = Unsafe.As<T, int>(ref value);
        var result = ImGui.Combo(label, ref currentItem, options, options.Length);
        if (result)
        {
            value = Unsafe.As<int, T>(ref currentItem);
        }
        return result;
    }

    public static void Label(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(text);
        ImGui.SameLine();
    }

    private const uint BoxBorderColor = 0xFFCCCCCC;

    public static void HorizontalBoxes(Vector2 size, ReadOnlySpan<uint> colors, Span<bool> clicked)
    {
        var x = ImGui.GetCursorScreenPos().X;
        var y = ImGui.GetCursorScreenPos().Y;

        ImGui.BeginGroup();

        for (var i = 0; i < colors.Length; i++)
        {
            var a = new Vector2(x, y);
            var b = a + size;

            if (ImGui.IsWindowHovered())
            {
                var pos = ImGui.GetMousePos();
                var hovered = pos.X >= a.X && pos.X <= b.X && pos.Y >= a.Y && pos.Y <= b.Y;
                clicked[i] = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            }

            var drawList = ImGui.GetWindowDrawList();

            drawList.AddRectFilled(a, b, colors[i]);
            drawList.AddRect(a, b, BoxBorderColor);

            x += size.X - 1; // Subtract border thickness

            ImGui.SetCursorScreenPos(new Vector2(x + size.X, y));
        }

        ImGui.EndGroup();
    }

    public static bool PaletteColorButton(Vector2 size, uint color)
    {
        var x = ImGui.GetCursorScreenPos().X;
        var y = ImGui.GetCursorScreenPos().Y;

        ImGui.BeginGroup();

        var a = new Vector2(x, y);
        var b = a + size;

        var clicked = false;

        if (ImGui.IsWindowHovered())
        {
            var pos = ImGui.GetMousePos();
            var hovered = pos.X >= a.X && pos.X <= b.X && pos.Y >= a.Y && pos.Y <= b.Y;
            clicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        }

        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(a, b, color);
        drawList.AddRect(a, b, BoxBorderColor);

        ImGui.SetCursorScreenPos(new Vector2(x + size.X, y));

        ImGui.EndGroup();

        return clicked;
    }
}
