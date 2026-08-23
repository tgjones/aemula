using System.Collections.Generic;

namespace Aemula.UI.LogicAnalyzer;

/// <summary>
/// A named, collapsible grouping of channels/sub-groups in the logic analyzer's
/// channel sidebar. Collapsing a group only hides its member rows - it doesn't
/// stop them being recorded.
/// </summary>
public sealed class ChannelGroup(string name, IReadOnlyList<ChannelNode> children)
    : ChannelNode(name)
{
    public IReadOnlyList<ChannelNode> Children { get; } = children;
}
