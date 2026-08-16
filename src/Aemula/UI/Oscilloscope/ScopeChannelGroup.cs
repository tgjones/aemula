using System.Collections.Generic;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// A named, collapsible grouping of channels/sub-groups in the oscilloscope's
/// channel sidebar. Collapsing a group only hides its member rows - it doesn't
/// stop them being recorded.
/// </summary>
public sealed class ScopeChannelGroup(string name, IReadOnlyList<ScopeChannelNode> children)
    : ScopeChannelNode(name)
{
    public IReadOnlyList<ScopeChannelNode> Children { get; } = children;
}
