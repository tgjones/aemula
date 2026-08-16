using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Extra key/value settings to persist alongside <c>IsOpen</c> in the
    /// shared <c>[Aemula][&lt;window name&gt;]</c> ini section (see the
    /// <c>ImGuiSettingsHandler</c> wiring in <c>Program.cs</c>). Override to add
    /// window-specific state (e.g. tree collapse state) that should survive a
    /// restart - subclasses only deal with keys and values, never the
    /// "key=value" ini line format itself; see <see cref="GetPersistedSettingsLines"/>.
    /// </summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> GetPersistedSettings() => [];

    /// <summary>
    /// Receives one key/value pair previously returned from
    /// <see cref="GetPersistedSettings"/> - the read-side counterpart, called
    /// once per persisted key found in this window's ini section (excluding the
    /// built-in <c>IsOpen</c>, which <see cref="ApplyPersistedSettingsLine"/>
    /// handles directly rather than routing through here).
    /// </summary>
    protected virtual void ApplyPersistedSetting(string key, string value) { }

    /// <summary>
    /// Formats <c>IsOpen</c> plus <see cref="GetPersistedSettings"/> as
    /// "key=value" lines - the generic ini-line handling every window shares
    /// (via <c>Program.cs</c>'s <c>ImGuiSettingsHandler</c> wiring), so no
    /// subclass, nor <c>Program.cs</c> itself, needs to format/parse lines or
    /// hardcode the <c>IsOpen</c> key.
    /// </summary>
    public IEnumerable<string> GetPersistedSettingsLines()
    {
        if (IsOpen)
        {
            yield return "IsOpen=1";
        }

        foreach (var setting in GetPersistedSettings())
        {
            yield return $"{setting.Key}={setting.Value}";
        }
    }

    /// <summary>
    /// Splits an ini line from this window's section into a key/value pair,
    /// handles the built-in <c>IsOpen</c> key directly, and forwards anything
    /// else to <see cref="ApplyPersistedSetting"/>.
    /// </summary>
    public void ApplyPersistedSettingsLine(string line)
    {
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex < 0)
        {
            return;
        }

        var key = line[..separatorIndex];
        var value = line[(separatorIndex + 1)..];

        if (key == "IsOpen")
        {
            IsOpen = value == "1";
            return;
        }

        ApplyPersistedSetting(key, value);
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
