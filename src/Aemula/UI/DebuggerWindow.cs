using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

public abstract class DebuggerWindow : IDisposable
{
    private bool _isVisible;

    public virtual Vector2 DefaultSize { get; } = new Vector2(400, 300);

    public bool IsVisible
    {
        get => _isVisible;
        set => _isVisible = value;
    }

    public string Name => DisplayName;

    public abstract string DisplayName { get; }

    public virtual Pane PreferredPane => Pane.None;

    public virtual void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice) { }

    public void Prepare(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        if (!IsVisible)
        {
            return;
        }

        PrepareOverride(time, commandBuffer);
    }

    protected virtual void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer) { }

    public void Draw(EmulatorTime time)
    {
        if (!IsVisible)
        {
            return;
        }

        ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.FirstUseEver);

        if (ImGui.Begin($"{DisplayName}##{Name}", ref _isVisible))
        {
            DrawOverride(time);
        }
        ImGui.End();
    }

    protected abstract void DrawOverride(EmulatorTime time);

    public virtual void Dispose() { }
}
