using System;
using System.Collections.Generic;
using System.Numerics;
using Hexa.NET.ImGui;

namespace Aemula.UI.Chips;

internal enum IcPinKind
{
    Input,
    Output,
    Bidirectional,
    Power,
    NoConnect,
}

/// <summary>
/// One pin of an <see cref="IcDiagram"/>.
/// </summary>
/// <param name="Number">Package pin number, drawn dimly inside the body.</param>
/// <param name="Label">
/// Pin name. Text between '~' characters is drawn with an overbar, as on a
/// datasheet: "~M1~" is /M1, "R/~W~" is R/W with the bar over the W.
/// </param>
/// <param name="Kind">Direction; drives the arrow beside the pin.</param>
/// <param name="ActiveLow">Whether the pin lights up when it is electrically low.</param>
/// <param name="Level">Electrical level: true is high, null is unknown/high-impedance.</param>
internal readonly record struct IcPin(
    int Number,
    string Label,
    IcPinKind Kind,
    bool ActiveLow,
    Func<bool?>? Level)
{
    public static IcPin Input(int number, string label, Func<bool?> level, bool activeLow = false) =>
        new(number, label, IcPinKind.Input, activeLow, level);

    public static IcPin Output(int number, string label, Func<bool?> level, bool activeLow = false) =>
        new(number, label, IcPinKind.Output, activeLow, level);

    public static IcPin Bidirectional(int number, string label, Func<bool?> level, bool activeLow = false) =>
        new(number, label, IcPinKind.Bidirectional, activeLow, level);

    public static IcPin Power(int number, string label) => new(number, label, IcPinKind.Power, false, null);

    public static IcPin NoConnect(int number) => new(number, "NC", IcPinKind.NoConnect, false, null);
}

/// <summary>
/// Draws a dual-in-line IC package - a rectangular body with a pin-1 notch and
/// one live LED per pin - in the style of a schematic symbol. Left pins are
/// listed top to bottom, right pins top to bottom (so a DIP's right side is
/// listed in descending pin-number order).
/// </summary>
internal static class IcDiagram
{
    private static readonly uint BodyFill = CpuWidgets.Rgb(0x16, 0x17, 0x1C);
    private static readonly uint BodyOutline = CpuWidgets.Rgb(0xD0, 0xD0, 0xD8);
    private static readonly uint LedLit = CpuWidgets.Rgb(0x6B, 0xFF, 0x58);
    private static readonly uint LedUnlit = CpuWidgets.Rgb(0x1B, 0x40, 0x1E);
    private static readonly uint LedUnknown = CpuWidgets.Rgb(0x55, 0x55, 0x5C);
    private static readonly uint LedBorder = CpuWidgets.Rgb(0x0C, 0x0C, 0x0E);
    private static readonly uint ArrowColor = CpuWidgets.Rgb(0x8A, 0x8D, 0x99);

    private const int BodyColumns = 14;

    public static float MeasureWidth(IReadOnlyList<IcPin> left, IReadOnlyList<IcPin> right)
    {
        var charWidth = CpuWidgets.CharWidth;
        return LabelColumns(left) * charWidth
            + LabelColumns(right) * charWidth
            + (ArrowColumns + LedColumns) * 2 * charWidth
            + BodyColumns * charWidth;
    }

    private const float ArrowColumns = 1.5f;
    private const float LedColumns = 1.5f;

    private static int LabelColumns(IReadOnlyList<IcPin> pins)
    {
        var columns = 0;
        foreach (var pin in pins)
        {
            columns = Math.Max(columns, VisibleLength(pin.Label));
        }

        // A character of breathing room between the label and the arrow.
        return columns + 1;
    }

    private static int VisibleLength(string label) => label.Replace("~", string.Empty).Length;

    public static Vector2 Measure(IReadOnlyList<IcPin> left, IReadOnlyList<IcPin> right)
    {
        var rows = Math.Max(left.Count, right.Count);
        return new Vector2(MeasureWidth(left, right), rows * PinPitch + PinPitch);
    }

    private static float PinPitch => ImGui.GetFontSize() * 1.45f;

    public static void Draw(string name, IReadOnlyList<IcPin> left, IReadOnlyList<IcPin> right)
    {
        var drawList = ImGui.GetWindowDrawList();
        var charWidth = CpuWidgets.CharWidth;
        var fontSize = ImGui.GetFontSize();
        var pitch = PinPitch;
        var size = Measure(left, right);

        var origin = ImGui.GetCursorScreenPos();

        var leftLabelWidth = LabelColumns(left) * charWidth;
        var rightLabelWidth = LabelColumns(right) * charWidth;
        var arrowWidth = ArrowColumns * charWidth;
        var ledWidth = LedColumns * charWidth;
        var bodyWidth = BodyColumns * charWidth;

        var bodyMin = new Vector2(origin.X + leftLabelWidth + arrowWidth + ledWidth, origin.Y + pitch * 0.5f);
        var bodyMax = new Vector2(bodyMin.X + bodyWidth, origin.Y + size.Y - pitch * 0.5f);

        drawList.AddRectFilled(bodyMin, bodyMax, BodyFill);
        drawList.AddRect(bodyMin, bodyMax, BodyOutline, 0f, ImDrawFlags.None, 1.5f);

        // Pin-1 notch: a small semicircle cut into the top edge.
        var notchCentre = new Vector2((bodyMin.X + bodyMax.X) * 0.5f, bodyMin.Y);
        drawList.PathArcTo(notchCentre, charWidth * 1.2f, 0f, MathF.PI);
        drawList.PathStroke(BodyOutline, ImDrawFlags.None, 1.5f);

        var nameSize = ImGui.CalcTextSize(name);
        drawList.AddText(
            new Vector2((bodyMin.X + bodyMax.X - nameSize.X) * 0.5f, (bodyMin.Y + bodyMax.Y - nameSize.Y) * 0.5f),
            CpuWidgets.Theme.Value,
            name);

        for (var i = 0; i < left.Count; i++)
        {
            var centreY = bodyMin.Y + pitch * (i + 0.5f);
            DrawPin(drawList, left[i], isLeft: true, centreY, origin.X, leftLabelWidth, arrowWidth, ledWidth, bodyMin, bodyMax, charWidth, fontSize);
        }

        for (var i = 0; i < right.Count; i++)
        {
            var centreY = bodyMin.Y + pitch * (i + 0.5f);
            DrawPin(drawList, right[i], isLeft: false, centreY, bodyMax.X, rightLabelWidth, arrowWidth, ledWidth, bodyMin, bodyMax, charWidth, fontSize);
        }

        ImGui.Dummy(size);
    }

    // For a left pin, edgeX is the left edge of the whole diagram and the pin
    // runs rightwards to the body: label, arrow, LED. For a right pin, edgeX is
    // the body's right edge and the pin runs rightwards away from it: LED,
    // arrow, label.
    private static void DrawPin(
        ImDrawListPtr drawList,
        IcPin pin,
        bool isLeft,
        float centreY,
        float edgeX,
        float labelWidth,
        float arrowWidth,
        float ledWidth,
        Vector2 bodyMin,
        Vector2 bodyMax,
        float charWidth,
        float fontSize)
    {
        var ledHeight = fontSize * 0.8f;
        var ledMin = isLeft
            ? new Vector2(bodyMin.X - ledWidth, centreY - ledHeight * 0.5f)
            : new Vector2(bodyMax.X, centreY - ledHeight * 0.5f);
        var ledMax = ledMin + new Vector2(ledWidth, ledHeight);

        drawList.AddRectFilled(ledMin, ledMax, LedColor(pin));
        drawList.AddRect(ledMin, ledMax, LedBorder);

        var arrowMin = isLeft ? ledMin.X - arrowWidth : ledMax.X;
        DrawArrow(drawList, pin.Kind, arrowMin, arrowWidth, centreY, isLeft, fontSize);

        var textY = centreY - fontSize * 0.5f;
        var labelColor = pin.Kind is IcPinKind.Power or IcPinKind.NoConnect ? CpuWidgets.Theme.Dim : CpuWidgets.Theme.Label;
        var labelX = isLeft
            ? edgeX + labelWidth - charWidth - VisibleLength(pin.Label) * charWidth
            : ledMax.X + arrowWidth + charWidth;
        DrawLabel(drawList, pin.Label, labelX, textY, charWidth, fontSize, labelColor);

        // The pin number sits just inside the body, next to its pin.
        var number = pin.Number.ToString();
        var numberX = isLeft
            ? bodyMin.X + charWidth * 0.5f
            : bodyMax.X - charWidth * 0.5f - number.Length * charWidth;
        drawList.AddText(new Vector2(numberX, textY), CpuWidgets.Theme.Dim, number);
    }

    private static uint LedColor(IcPin pin)
    {
        if (pin.Level == null)
        {
            return LedUnknown;
        }

        var level = pin.Level();
        if (level == null)
        {
            return LedUnknown;
        }

        var lit = pin.ActiveLow ? !level.Value : level.Value;
        return lit ? LedLit : LedUnlit;
    }

    private static void DrawLabel(ImDrawListPtr drawList, string label, float x, float y, float charWidth, float fontSize, uint color)
    {
        var overbar = false;
        var barStart = 0f;

        foreach (var c in label)
        {
            if (c == '~')
            {
                if (overbar)
                {
                    DrawOverbar(drawList, barStart, x, y, color);
                }
                else
                {
                    barStart = x;
                }

                overbar = !overbar;
                continue;
            }

            drawList.AddText(new Vector2(x, y), color, c.ToString());
            x += charWidth;
        }

        if (overbar)
        {
            DrawOverbar(drawList, barStart, x, y, color);
        }
    }

    private static void DrawOverbar(ImDrawListPtr drawList, float x0, float x1, float y, uint color)
    {
        drawList.AddLine(new Vector2(x0, y - 1), new Vector2(x1, y - 1), color);
    }

    // Arrows point in the direction the signal travels: into the body for an
    // input, out of it for an output, both ways for a bidirectional pin.
    private static void DrawArrow(ImDrawListPtr drawList, IcPinKind kind, float x, float width, float centreY, bool isLeft, float fontSize)
    {
        if (kind is IcPinKind.Power or IcPinKind.NoConnect)
        {
            return;
        }

        var half = fontSize * 0.28f;
        var centreX = x + width * 0.5f;

        // direction is +1 for a head pointing rightwards, -1 for leftwards.
        void Head(float tipX, float direction)
        {
            var back = tipX - direction * half;

            // ImGui's anti-aliased fill grows the edge outwards or inwards
            // depending on the winding order, so both directions must wind the
            // same way or one draws larger than the other.
            var first = direction > 0 ? half : -half;
            drawList.AddTriangleFilled(
                new Vector2(tipX, centreY),
                new Vector2(back, centreY + first),
                new Vector2(back, centreY - first),
                ArrowColor);
        }

        // A head pointing a given way always sits at the same spot, whichever
        // kind of pin it belongs to, so single arrows line up with the matching
        // head of a bidirectional pin's double arrow.
        float TipX(float direction) => centreX + direction * half * 1.5f;

        switch (kind)
        {
            case IcPinKind.Input:
                Head(TipX(isLeft ? 1 : -1), isLeft ? 1 : -1);
                break;

            case IcPinKind.Output:
                Head(TipX(isLeft ? -1 : 1), isLeft ? -1 : 1);
                break;

            case IcPinKind.Bidirectional:
                drawList.AddLine(new Vector2(centreX - half * 0.5f, centreY), new Vector2(centreX + half * 0.5f, centreY), ArrowColor);
                Head(TipX(-1), -1);
                Head(TipX(1), 1);
                break;
        }
    }
}
