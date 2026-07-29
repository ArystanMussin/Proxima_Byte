using Opc.Ua;
using Opc.Ua.Client;

namespace PGW.Drivers.OpcUa;

public sealed record OpcUaBrowseNode(string NodeId, string BrowseName, string NodeClass, bool HasChildren);

/// <summary>
/// Ad-hoc address-space browsing (§5.2, §14.2 `POST /config/opcua/browse`): connects, browses one level
/// below <paramref name="startNodeId"/> (ObjectsFolder by default), disconnects. Not tied to any
/// configured source — this is the tool used to discover NodeIds *before* writing the tag list.
/// </summary>
public static class OpcUaBrowser
{
    private const int MaxNodes = 5000;

    public static async Task<List<OpcUaBrowseNode>> BrowseAsync(
        string endpointUrl, bool useSecurity, string? startNodeId, string certsPath, bool autoAccept, CancellationToken ct)
    {
        var appConfig = await OpcUaAppConfig.BuildAsync("browse", certsPath, autoAccept, ct);
        var endpoint = await Task.Run(() => Opc.Ua.Client.CoreClientUtils.SelectEndpoint(appConfig, endpointUrl, useSecurity), ct);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(appConfig));

        using var session = await Session.CreateAsync(appConfig, null, configuredEndpoint, false, false,
            "PGW-browse", 30000, new UserIdentity(), null, ct);

        var startNode = string.IsNullOrWhiteSpace(startNodeId) ? new NodeId(Objects.ObjectsFolder, 0) : NodeId.Parse(startNodeId);
        var description = new BrowseDescription
        {
            NodeId = startNode,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = 0,
            ResultMask = (uint)BrowseResultMask.All,
        };

        var nodes = new List<OpcUaBrowseNode>();
        var response = await session.BrowseAsync(null, null, (uint)MaxNodes, new BrowseDescriptionCollection { description }, ct);
        var result = response.Results[0];
        AddReferences(nodes, result.References);

        var continuationPoint = result.ContinuationPoint;
        while (continuationPoint is { Length: > 0 } && nodes.Count < MaxNodes)
        {
            var next = await session.BrowseNextAsync(null, false, new ByteStringCollection { continuationPoint }, ct);
            var nextResult = next.Results[0];
            AddReferences(nodes, nextResult.References);
            continuationPoint = nextResult.ContinuationPoint;
        }

        await session.CloseAsync(ct);
        return nodes;
    }

    private static void AddReferences(List<OpcUaBrowseNode> nodes, ReferenceDescriptionCollection refs)
    {
        foreach (var r in refs)
            // Object/View nodes usually have further children; Variable/Method nodes are leaves for our purposes — a heuristic, not a guarantee.
            nodes.Add(new OpcUaBrowseNode(r.NodeId.ToString(), r.BrowseName.Name, r.NodeClass.ToString(), r.NodeClass is NodeClass.Object or NodeClass.View));
    }
}
