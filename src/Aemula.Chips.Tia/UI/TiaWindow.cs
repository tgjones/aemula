﻿using System;
using System.Numerics;
using System.Reflection;
using Aemula.UI;
using ImGuiNET;

namespace Aemula.Chips.Tia.UI;

internal sealed class TiaWindow : DebuggerWindow
{
    private readonly Tia _tia;

    public override string DisplayName => "TIA";

    public TiaWindow(Tia tia)
    {
        _tia = tia;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        ImGui.Checkbox("VSync", ref _tia.VerticalSync);
        ImGui.SameLine();

        ImGui.Checkbox("VBlank", ref _tia.VerticalBlank);
        ImGui.SameLine();

        ImGui.Checkbox("HSync", ref _tia.HorizontalSync);
        ImGui.SameLine();

        ImGui.Checkbox("HBlank", ref _tia.HorizontalBlank);

        ImGui.Spacing();

        //ImGui.Text($"Horizontal counter: {Convert.ToString(_tia.HorizontalCounter, 2).PadLeft(6, '0')}");

        if (ImGui.CollapsingHeader("Playfield", ImGuiTreeNodeFlags.DefaultOpen))
        {

        }

        ImGui.Spacing();

        DrawPlayer(_tia.PlayerAndMissile0, 0);

        ImGui.Spacing();

        DrawPlayer(_tia.PlayerAndMissile1, 1);

        ImGui.Spacing();
    }

    private static unsafe void DrawPlayer(PlayerAndMissile player, int playerIndex)
    {
        if (!ImGui.CollapsingHeader($"Player {playerIndex}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ImGui.PushID(playerIndex);

        ImGui.Spacing();

        //ImGui.Text($"Luminance: {Convert.ToString(player.Luminance, 2)}");

        ImGuiUtility.Label("HMOVE");

        var hmove = player.HorizontalMotionPlayer;
        if (ImGuiUtility.InputHex("##hmove", 1, ref hmove))
        {
            player.HorizontalMotionPlayer = hmove;
        }

        ImGui.SameLine();

        var hmoveSliderWidth = ImGui.GetFontSize() * 16;

        ImGui.PushItemWidth(hmoveSliderWidth);
        var hmoveSlider = player.HorizontalMotionPlayer - 8;
        if (ImGui.SliderInt("##hmoveSlider", ref hmoveSlider, -8, 7))
        {
            player.HorizontalMotionPlayer = (byte)(hmoveSlider + 8);
        }
        ImGui.PopItemWidth();

        ImGui.Checkbox("Reflect", ref player.Reflect);

        DrawPlayerGraphics(player);

        ImGui.Spacing();

        ImGuiUtility.Label("Color");

        var color = player.Color;
        if (ImGuiUtility.InputByte("##color", ref color))
        {
            player.Color = color;
        }

        ImGui.SameLine();

        ImGuiUtility.PaletteColorButton(
            new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()),
            NtscPalette[color] | 0xFF000000);

        ImGui.Spacing();

        ImGuiUtility.Label("NUSIZ");

        var nusiz = player.NumberSizePlayer;
        if (ImGuiUtility.InputByte("##nusiz", ref nusiz, (byte)(NuSizNames.Length - 1)))
        {
            player.NumberSizePlayer = nusiz;
        }

        ImGui.SameLine();

        ImGui.BeginGroup();

        ImGui.PushItemWidth(NuSizeComboSize.X);

        if (ImGui.BeginCombo("##nusizcombo", NuSizNames[nusiz]))
        {
            for (byte i = 0; i < NuSizNames.Length; i++)
            {
                if (ImGui.Selectable(NuSizNames[i]))
                {
                    player.NumberSizePlayer = i;
                }
            }

            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();

        ImGui.EndGroup();

        ImGui.PopID();
    }

    private static readonly string[] NuSizNames = new[]
    {
        "One copy",
        "Two copies - close",
        "Two copies - medium",
        "Three copies - close",
        "Two copies - wide",
        "Double sized player",
        "Three copies - medium",
        "Quad sized player",
    };

    private static readonly Vector2 NuSizeComboSize = ImGuiUtility.CalculateFrameDimensions(Array.Empty<char>(), NuSizNames) + new Vector2(ImGui.GetTextLineHeightWithSpacing(), 0);

    private static unsafe void DrawPlayerGraphics(PlayerAndMissile player)
    {
        ImGuiUtility.Label("Graphics");

        var graphics = player.Graphics;

        Span<uint> graphicsColors = stackalloc uint[8];
        Span<bool> graphicsClicked = stackalloc bool[8];

        for (var i = 0; i < graphicsColors.Length; i++)
        {
            var color = (((graphics << i) & 0x080) == 0x80)
                ? player.Color
                : 0;

            graphicsColors[i] = NtscPalette[color] | 0xFF000000;
        }

        ImGuiUtility.HorizontalBoxes(
            new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()),
            graphicsColors, graphicsClicked);

        for (var i = 0; i < graphicsClicked.Length; i++)
        {
            if (graphicsClicked[i])
            {
                graphics ^= (byte)(0x80 >> i);
                player.Graphics = graphics;
            }
        }
    }

    // TODO: Don't duplicate this here.
    private static readonly uint[] NtscPalette =
    {
        0x000000, 0x404040, 0x6c6c6c, 0x909090, 0xb0b0b0, 0xc8c8c8, 0xdcdcdc, 0xececec,
        0x444400, 0x646410, 0x848424, 0xa0a034, 0xb8b840, 0xd0d050, 0xe8e85c, 0xfcfc68,
        0x702800, 0x844414, 0x985c28, 0xac783c, 0xbc8c4c, 0xcca05c, 0xdcb468, 0xecc878,
        0x841800, 0x983418, 0xac5030, 0xc06848, 0xd0805c, 0xe09470, 0xeca880, 0xfcbc94,
        0x880000, 0x9c2020, 0xb03c3c, 0xc05858, 0xd07070, 0xe08888, 0xeca0a0, 0xfcb4b4,
        0x78005c, 0x8c2074, 0xa03c88, 0xb0589c, 0xc070b0, 0xd084c0, 0xdc9cd0, 0xecb0e0,
        0x480078, 0x602090, 0x783ca4, 0x8c58b8, 0xa070cc, 0xb484dc, 0xc49cec, 0xd4b0fc,
        0x140084, 0x302098, 0x4c3cac, 0x6858c0, 0x7c70d0, 0x9488e0, 0xa8a0ec, 0xbcb4fc,
        0x000088, 0x1c209c, 0x3840b0, 0x505cc0, 0x6874d0, 0x7c8ce0, 0x90a4ec, 0xa4b8fc,
        0x00187c, 0x1c3890, 0x3854a8, 0x5070bc, 0x6888cc, 0x7c9cdc, 0x90b4ec, 0xa4c8fc,
        0x002c5c, 0x1c4c78, 0x386890, 0x5084ac, 0x689cc0, 0x7cb4d4, 0x90cce8, 0xa4e0fc,
        0x003c2c, 0x1c5c48, 0x387c64, 0x509c80, 0x68b494, 0x7cd0ac, 0x90e4c0, 0xa4fcd4,
        0x003c00, 0x205c20, 0x407c40, 0x5c9c5c, 0x74b474, 0x8cd08c, 0xa4e4a4, 0xb8fcb8,
        0x143800, 0x345c1c, 0x507c38, 0x6c9850, 0x84b468, 0x9ccc7c, 0xb4e490, 0xc8fca4,
        0x2c3000, 0x4c501c, 0x687034, 0x848c4c, 0x9ca864, 0xb4c078, 0xccd488, 0xe0ec9c,
        0x442800, 0x644818, 0x846830, 0xa08444, 0xb89c58, 0xd0b46c, 0xe8cc7c, 0xfce08c
    };
}
