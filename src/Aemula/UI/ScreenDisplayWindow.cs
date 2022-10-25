﻿using System;
using System.Numerics;
using ImGuiNET;
using Veldrid;

namespace Aemula.UI;

public sealed class ScreenDisplayWindow : DebuggerWindow
{
    private readonly DisplayBuffer _displayBuffer;
    private readonly int _angle;

    private GraphicsDevice _graphicsDevice;
    private Texture _texture;
    private IntPtr _textureBinding;

    public override string DisplayName => "Display";

    public ScreenDisplayWindow(DisplayBuffer displayBuffer, int angle = 0)
    {
        _displayBuffer = displayBuffer;
        _angle = angle;
    }

    public override void CreateGraphicsResources(GraphicsDevice graphicsDevice, ImGuiRenderer renderer)
    {
        base.CreateGraphicsResources(graphicsDevice, renderer);

        _graphicsDevice = graphicsDevice;

        _texture = graphicsDevice.ResourceFactory.CreateTexture(
            TextureDescription.Texture2D(
                _displayBuffer.Width,
                _displayBuffer.Height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));

        _textureBinding = renderer.GetOrCreateImGuiBinding(
            graphicsDevice.ResourceFactory,
            _texture);
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        _graphicsDevice.UpdateTexture(
            _texture,
            _displayBuffer.Data,
            0, 0, 0,
            _displayBuffer.Width,
            _displayBuffer.Height,
            1, 0, 0);

        if (_angle == 0)
        {
            ImGui.Image(
                _textureBinding,
                new Vector2(_texture.Width, _texture.Height));

            return;
        }

        var size = new Vector2(_texture.Height, _texture.Width);

        var cursorPos = ImGui.GetCursorPos();
        cursorPos.X += ImGui.GetWindowContentRegionWidth() / 2;
        cursorPos.Y += ImGui.GetWindowHeight() / 2;

        var uv0 = new Vector2(1, 0);
        var uv1 = new Vector2(1, 1);
        var uv2 = new Vector2(0, 1);
        var uv3 = new Vector2(0, 0);

        var p1 = cursorPos;
        var p2 = new Vector2(cursorPos.X + size.X, cursorPos.Y);
        var p3 = new Vector2(cursorPos.X + size.X, cursorPos.Y + size.Y);
        var p4 = new Vector2(cursorPos.X, cursorPos.Y + size.Y);

        ImGui.GetWindowDrawList().AddImageQuad(
            _textureBinding, 
            p1, p2, p3, p4, 
            uv0, uv1, uv2, uv3);
    }

    public override void Dispose()
    {
        base.Dispose();

        _texture.Dispose();
    }
}
