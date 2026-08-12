namespace Versio.Core.Model;

/// <summary>
/// CODESYS object types, identified by the type GUID CODESYS stamps on every object.
/// </summary>
/// <remarks>
/// The GUID table is adopted from greenforge-labs/codescribe (Apache-2.0). There is no
/// published list and deriving it independently costs days. Entries marked [VERSIO] were
/// discovered by probing a real CODESYS 3.5.22.20 project and are not in codescribe's table.
/// <para>
/// This must stay in lockstep with <c>src/Versio.Scripts/versio_types.py</c>. The
/// <c>TypeTableParityTests</c> assert that they have not drifted.
/// </para>
/// </remarks>
public enum CodesysObjectType
{
    Unknown = 0,
    Pou,
    Dut,
    Gvl,
    GvlPersistent,
    Evc,
    Interface,
    Task,
    Method,
    MethodNoReturn,
    Property,
    PropertyMethod,
    Action,
    Transition,
    ImagePool,
    TextList,
    GlobalTextList,
    LibraryManager,
    TaskConfiguration,
    ProjectInformation,
    ProjectSettings,
    Device,
    Folder,
    CallToPou,
    Visualisation,
    VisualisationManager,
    TargetVisualisation,
    WebVisualisation,
    VisualisationStyle,

    /// <summary>[VERSIO] The "Plc Logic" node beneath a Device.</summary>
    PlcLogic,

    /// <summary>[VERSIO] The "Application" node. codescribe locates this by name, which
    /// breaks if a project renames it; the type GUID does not.</summary>
    Application,
}

public static class CodesysObjectTypes
{
    private static readonly Dictionary<string, CodesysObjectType> ByGuid =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["6f9dac99-8de1-4efc-8465-68ac443b7d08"] = CodesysObjectType.Pou,
            ["2db5746d-d284-4425-9f7f-2663a34b0ebc"] = CodesysObjectType.Dut,
            ["ffbfa93a-b94d-45fc-a329-229860183b1d"] = CodesysObjectType.Gvl,
            ["261bd6e6-249c-4232-bb6f-84c2fbeef430"] = CodesysObjectType.GvlPersistent,
            ["327b6465-4e7f-4116-846a-8369c730fd66"] = CodesysObjectType.Evc,
            ["6654496c-404d-479a-aad2-8551054e5f1e"] = CodesysObjectType.Interface,
            ["98a2708a-9b18-4f31-82ed-a1465b24fa2d"] = CodesysObjectType.Task,
            ["f8a58466-d7f6-439f-bbb8-d4600e41d099"] = CodesysObjectType.Method,
            ["f89f7675-27f1-46b3-8abb-b7da8e774ffd"] = CodesysObjectType.MethodNoReturn,
            ["5a3b8626-d3e9-4f37-98b5-66420063d91e"] = CodesysObjectType.Property,
            ["792f2eb6-721e-4e64-ba20-bc98351056db"] = CodesysObjectType.PropertyMethod,
            ["8ac092e5-3128-4e26-9e7e-11016c6684f2"] = CodesysObjectType.Action,
            ["a10c6218-cb94-436f-91c6-e1652575253d"] = CodesysObjectType.Transition,
            ["bb0b9044-714e-4614-ad3e-33cbdf34d16b"] = CodesysObjectType.ImagePool,
            ["2bef0454-1bd3-412a-ac2c-af0f31dbc40f"] = CodesysObjectType.TextList,
            ["63784cbb-9ba0-45e6-9d69-babf3f040511"] = CodesysObjectType.GlobalTextList,
            ["adb5cb65-8e1d-4a00-b70a-375ea27582f3"] = CodesysObjectType.LibraryManager,
            ["ae1de277-a207-4a28-9efb-456c06bd52f3"] = CodesysObjectType.TaskConfiguration,
            ["085afe48-c5d8-4ea5-ab0d-b35701fa6009"] = CodesysObjectType.ProjectInformation,
            ["8753fe6f-4a22-4320-8103-e553c4fc8e04"] = CodesysObjectType.ProjectSettings,
            ["225bfe47-7336-4dbc-9419-4105a7c831fa"] = CodesysObjectType.Device,
            ["738bea1e-99bb-4f04-90bb-a7a567e74e3a"] = CodesysObjectType.Folder,
            ["413e2a7d-adb1-4d2c-be29-6ae6e4fab820"] = CodesysObjectType.CallToPou,
            ["f18bec89-9fef-401d-9953-2f11739a6808"] = CodesysObjectType.Visualisation,
            ["4d3fdb8f-ab50-4c35-9d3a-d4bb9bb9a628"] = CodesysObjectType.VisualisationManager,
            ["bc63f5fa-d286-4786-994e-7b27e4f97bd5"] = CodesysObjectType.TargetVisualisation,
            ["0fdbf158-1ae0-47d9-9269-cd84be308e9d"] = CodesysObjectType.WebVisualisation,
            ["8e687a04-7ca7-42d3-be06-fcbda676c5ef"] = CodesysObjectType.VisualisationStyle,
            ["40b404f9-e5dc-42c6-907f-c89f4a517386"] = CodesysObjectType.PlcLogic,
            ["639b491f-5557-464c-af91-1471bac9f549"] = CodesysObjectType.Application,
        };

    private static readonly Dictionary<CodesysObjectType, string> ByType =
        ByGuid.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static int KnownGuidCount => ByGuid.Count;

    public static IReadOnlyDictionary<string, CodesysObjectType> All => ByGuid;

    /// <summary>Never throws. An unmapped GUID is <see cref="CodesysObjectType.Unknown"/>,
    /// which the exporter reports loudly and falls back to native export for — a new
    /// CODESYS version adding a type must not silently drop objects.</summary>
    public static CodesysObjectType FromGuid(string? typeGuid) =>
        typeGuid is not null && ByGuid.TryGetValue(typeGuid.Trim(), out var t)
            ? t
            : CodesysObjectType.Unknown;

    public static string? ToGuid(CodesysObjectType type) =>
        ByType.TryGetValue(type, out var g) ? g : null;
}
