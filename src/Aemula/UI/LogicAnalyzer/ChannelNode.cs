using System.Collections.Generic;

namespace Aemula.UI.LogicAnalyzer;

/// <summary>
/// Base for the logic analyzer's channel tree - either a leaf <see cref="Channel"/>
/// or a <see cref="ChannelGroup"/> composing other nodes.
/// </summary>
public abstract class ChannelNode(string name)
{
    public string Name { get; } = name;

    public static IEnumerable<Channel> Flatten(ChannelNode node)
    {
        switch (node)
        {
            case Channel channel:
                yield return channel;
                break;

            case ChannelGroup group:
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

    public static IEnumerable<Channel> Flatten(IEnumerable<ChannelNode> nodes)
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
