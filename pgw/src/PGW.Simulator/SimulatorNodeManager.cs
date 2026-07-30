using Opc.Ua;
using Opc.Ua.Server;

namespace PGW.Simulator;

/// <summary>Address space for the simulated OPC UA server: one folder, four variables, one writable.</summary>
internal sealed class SimulatorNodeManager : CustomNodeManager2
{
    private readonly Dictionary<string, BaseDataVariableState> _variables = new();

    public SimulatorNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, "urn:pgw:simulator")
    {
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

            var root = new FolderState(null)
            {
                NodeId = new NodeId("Simulated", NamespaceIndex),
                BrowseName = new QualifiedName("Simulated", NamespaceIndex),
                DisplayName = "Simulated",
                TypeDefinitionId = ObjectTypeIds.FolderType,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
            };
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));

            AddVariable(root, "T1_supply", DataTypeIds.Double, 25.0, writable: false);
            AddVariable(root, "P1_supply", DataTypeIds.Double, 5.0, writable: false);
            AddVariable(root, "setpoint", DataTypeIds.Double, 70.0, writable: true);
            AddVariable(root, "pump1_run", DataTypeIds.Boolean, true, writable: false);

            AddPredefinedNode(SystemContext, root);
        }
    }

    private void AddVariable(FolderState parent, string name, NodeId dataType, object initial, bool writable)
    {
        var access = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = access,
            UserAccessLevel = access,
            Value = initial,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(variable);
        _variables[name] = variable;
    }

    /// <summary>Ticks the read-only variables so subscribers see live data, same idea as the Modbus simulator.</summary>
    public void Wiggle(Random rnd)
    {
        lock (Lock)
        {
            SetValue("T1_supply", 24.0 + rnd.NextDouble() * 2);
            SetValue("P1_supply", 4.8 + rnd.NextDouble() * 0.4);
        }
    }

    private void SetValue(string name, object value)
    {
        if (!_variables.TryGetValue(name, out var v)) return;
        v.Value = value;
        v.Timestamp = DateTime.UtcNow;
        v.ClearChangeMasks(SystemContext, false);
    }
}
