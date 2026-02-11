using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

public abstract class DebuggerWindow : IDisposable
{
    private readonly GCHandle _gcHandle;
    private bool _isOpen;

    public GCHandle GCHandle => _gcHandle;

    public virtual Vector2 DefaultSize { get; } = new Vector2(400, 300);

    public bool IsOpen
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    public string Name => DisplayName;

    public abstract string DisplayName { get; }

    public virtual Pane PreferredPane => Pane.None;

    protected DebuggerWindow()
    {
        _gcHandle = GCHandle.Alloc(this);
    }

    public virtual void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice) { }

    public void Prepare(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        if (!IsOpen)
        {
            return;
        }

        PrepareOverride(time, commandBuffer);
    }

    protected virtual void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer) { }

    public void Draw(EmulatorTime time)
    {
        if (!IsOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.FirstUseEver);

        if (ImGui.Begin($"{DisplayName}##{Name}", ref _isOpen))
        {
            DrawOverride(time);
        }
        ImGui.End();
    }

    protected abstract void DrawOverride(EmulatorTime time);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
