namespace DualMod.Phase0E2E.Shared;

/// <summary>
/// Shared constants, virtual overlay paths, reserved opcodes, and typed wire messages
/// compiled into both <c>phase0-e2e</c> (Server) and <c>phase0-e2e.Client</c> (Client).
/// </summary>
public static class Phase0Protocol
{
    public const string ModId = "phase0-e2e";
    public const string ChannelName = "phase0-e2e";

    /// <summary>Disjoint host-channel ruleset slot (0xA0=Host, 0xA1=HarvestGarden, 0xA2=Maxwell, 0xA3=Siphon, 0xA4=Phase0E2E).</summary>
    public const byte RulesetId = 0xA4;

    /// <summary>Reserved handler-group opcode floor (stock 3.29.3d ends at 0x02FB).</summary>
    public const ushort FirstReservedOpcode = 0x02FC;

    public const ushort RawProbeOpcodeA = 0x02FC;
    public const ushort RawProbeOpcodeB = 0x02FD;
    public const ushort RawProbeOpcodeLoopback = 0x02FE;
    public const ushort RawProbeOpcodeExtra = 0x02FF;

    public const ushort CustomOpcodePing = 1301;
    public const ushort CustomOpcodePong = 1302;
    public const ushort CustomOpcodeRunProbes = 1303;
    public const ushort CustomOpcodeProbeReport = 1304;
    public const ushort CustomOpcodeItemTestRequest = 1305;
    public const ushort CustomOpcodeItemTestResult = 1306;
    public const ushort CustomOpcodeChatProbe = 1307;
    public const ushort CustomOpcodeRawEchoRequest = 1308;
    public const ushort CustomOpcodeRawEchoResponse = 1309;
    public const ushort CustomOpcodeEventNotify = 1310;

    public const string ProbeWindowUiPath = "Metadata/UI/Mods/Phase0E2E/Phase0Probe.ui";
    public const string ChildCardUiPath = "Metadata/UI/Mods/Phase0E2E/Phase0ChildCard.ui";
    public const string BomFreeSmPath = "Metadata/UI/Mods/Phase0E2E/test_bom_free.sm";
    public const string BomFreeMatPath = "Metadata/UI/Mods/Phase0E2E/test_bom_free.mat";
    public const string BomFreeAmdPath = "Metadata/UI/Mods/Phase0E2E/test_bom_free.amd";
    public const string StockPetDatPath = "Data/Pet.datc64";

    public const string ClipboardProbeText = "Phase0E2E_Clip_\u2605";
    public const string ItemCustomNameProbe = "Phase0_Siphoned";
    public const string SuppressChatPrefix = "/e2e_suppress";
    public const string AllowChatPrefix = "/e2e_allow";

    public static string FormatAssertMarker(string assertionId, bool passed, string detail)
    {
        string status = passed ? "PASS" : "FAIL";
        return $"[PHASE0-E2E] ASSERT {assertionId} {status} {detail}";
    }
}

/// <summary>Client -&gt; Server: correlation ping for ServerBridge 0.2.0 round-trip verification (E2E-12).</summary>
public sealed record Phase0Ping(
    string CorrelationToken,
    string ClientSdkVersion,
    uint NativeAbiVersion,
    bool IsOnGameThread);

/// <summary>Server -&gt; Client: correlation pong confirming IsClientReady + HasMod + Client.For (E2E-12, E2E-13).</summary>
public sealed record Phase0Pong(
    string CorrelationToken,
    bool ClientReadyObserved,
    bool HasPhase0Mod,
    bool HasMaxwellMod,
    bool HasSiphonMod,
    string ServerNote);

/// <summary>Server -&gt; Client: instructs the client probe mod to execute a named probe group (/phase0 run|raw|item|chat).</summary>
public sealed record Phase0RunProbesRequest(
    string Suite,
    string CorrelationToken);

/// <summary>Client -&gt; Server: reports individual assertion outcomes back to Gateway log.</summary>
public sealed record Phase0ProbeAssertionResult(
    string AssertionId,
    bool Passed,
    string Detail);

/// <summary>Client -&gt; Server: requests verification of Item.CustomName 0x80 wire serialization (E2E-15).</summary>
public sealed record Phase0ItemTestRequest(
    string RequestedCustomName,
    bool TestClearAfterVerify = true);

/// <summary>Server -&gt; Client: confirms Item.CustomName set/round-trip/clear results (E2E-15).</summary>
public sealed record Phase0ItemTestResult(
    bool Passed,
    string ObservedName,
    bool ClearedCleanly,
    string Detail);

/// <summary>Server -&gt; Client: notifies client when GameEvents.LevelUp / CosmeticChanged / ClientMessage fire (E2E-13, E2E-14).</summary>
public sealed record Phase0ServerEventNotice(
    string EventName,
    string Detail);
