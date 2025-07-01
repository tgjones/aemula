using System;
using System.Numerics;
using ImGuiNET;
using Veldrid;

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

    public virtual void CreateGraphicsResources(GraphicsDevice graphicsDevice, ImGuiRenderer renderer) { }

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
