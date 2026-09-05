namespace Concierge.Shared.Tools;

/// <summary>
/// Tools that arrive after start-up.
///
/// The registry collects <see cref="IAgentTool"/> from DI at construction, which
/// works for tools compiled in and cannot work for tools that are not known until
/// something has been started and asked — an MCP server's catalogue, or the answer
/// to "can this device send a text message". A source is consulted each time the
/// catalogue is read instead.
///
/// This lived in the MCP namespace until there was a second kind of source. It was
/// never an MCP idea; it was the first thing that needed it. A device capability
/// source importing Concierge.Shared.Rpc.Mcp to implement an interface with nothing
/// to do with MCP would have been a lie about how the product is put together, and
/// the next person would have had to read three files to find that out.
/// </summary>
public interface IAgentToolSource
{
    IReadOnlyList<IAgentTool> Tools { get; }
}
