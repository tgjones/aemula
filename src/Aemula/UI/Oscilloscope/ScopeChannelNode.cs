using System.Collections.Generic;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Base for the oscilloscope's channel tree - either a leaf <see cref="ScopeChannel"/>
/// or a <see cref="ScopeChannelGroup"/> composing other nodes.
/// </summary>
public abstract class ScopeChannelNode(string name)
{
    public string Name { get; } = name;

    public static IEnumerable<ScopeChannel> Flatten(ScopeChannelNode node)
    {
        switch (node)
        {
            case ScopeChannel channel:
                yield return channel;
                break;

            case ScopeChannelGroup group:
                foreach (var child in group.Children)
                {
                    foreach (var channel in Flatten(child))
                    {
                        yield return channel;
                    }
                }
                break;
        }
    }

    public static IEnumerable<ScopeChannel> Flatten(IEnumerable<ScopeChannelNode> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var channel in Flatten(node))
            {
                yield return channel;
            }
        }
    }
}
