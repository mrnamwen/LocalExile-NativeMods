using System.Collections.Concurrent;
using System.Diagnostics;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Cross-<c>AssemblyLoadContext</c> Gateway packet and chat-fallback fan-out bus stored in
/// <see cref="AppDomain.CurrentDomain"/> using strictly BCL types so multiple LocalExile server mods
/// loaded in isolated <c>ModLoadContext</c>s share <c>0x02CA</c> (<see cref="IOpcodeRegistry.ClientRulesetOpcode"/>)
/// and <c>/nm</c> traffic without swallowing each other's handshakes or channel messages.
/// </summary>
public static class GatewayBridgeBus
{
    /// <summary>Default <see cref="AppDomain.CurrentDomain"/> slot key shared by all NativeMods server mods.</summary>
    public const string DefaultDomainKey = "LocalExile.NativeMods.GatewayBus.v1";

    private const string ParticipantsKey = "participants";
    private const string CounterKey = "counter";
    private const string HandshakesKey = "handshakes";
    private const int MaxCachedHandshakes = 4096;

    // Bitmask returned across ALCs by participant dispatch delegates (pure BCL int).
    internal const int ResultNone = 0;
    internal const int ResultHandled = 1;
    internal const int ResultSentServerHandshake = 2;
    internal const int ResultWasClientHandshake = 4;

    // Participant entry object[] indices (pure BCL types only — never custom types across ALCs).
    private const int FieldRegistrationId = 0;
    private const int FieldOwnerId = 1;
    private const int FieldOrder = 2;
    private const int FieldHostRulesetId = 3;
    private const int FieldRequiredMods = 4;
    private const int FieldServerCapabilities = 5;
    private const int FieldPacketHandler = 6;
    private const int FieldChatHandler = 7;
    private const int ParticipantFieldCount = 8;

    // Cached handshake entry object[] indices (pure BCL types only).
    private const int HandshakeRulesetId = 0;
    private const int HandshakeEnvelopeBytes = 1;
    private const int HandshakeIsChatFallback = 2;
    private const int HandshakeTimestampTicks = 3;
    private const int HandshakeFieldCount = 4;

    /// <summary>
    /// Attaches <paramref name="host"/> to the shared <see cref="AppDomain"/> Gateway bus identified by
    /// <paramref name="domainKey"/> (defaults to <see cref="DefaultDomainKey"/>).
    /// Disposing the returned <see cref="GatewayBridgeRegistration"/> unregisters this mod cleanly on unload/hot-reload.
    /// </summary>
    /// <param name="host">Server bridge host owned by the calling mod.</param>
    /// <param name="ownerId">Unique LocalExile mod identifier (for example <c>mod.Manifest.Id</c>).</param>
    /// <param name="onGatewayContext">
    /// Optional callback invoked with <c>(connectionId, characterName, gatewayContext)</c> right before this host
    /// processes a fanned-out NativeMods packet or <c>/nm</c> command, allowing the mod to bind the live Gateway
    /// <c>Player</c> object and character name even when another mod's hook received the packet first.
    /// </param>
    /// <param name="domainKey">Optional isolated <see cref="AppDomain"/> key (defaults to <see cref="DefaultDomainKey"/>).</param>
    /// <param name="onDispatchError">Optional callback invoked when a participant throws during fan-out.</param>
    public static GatewayBridgeRegistration Attach(
        ClientBridgeHost host,
        string ownerId,
        Action<string, string?, object?>? onGatewayContext = null,
        string? domainKey = null,
        Action<string, Exception>? onDispatchError = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        string resolvedKey = string.IsNullOrWhiteSpace(domainKey) ? DefaultDomainKey : domainKey;
        ConcurrentDictionary<string, object> state = GetOrCreateState(resolvedKey);
        ConcurrentDictionary<string, object[]> participants = GetParticipants(state);
        long[] counter = GetCounter(state);

        string registrationId = $"{ownerId}#{Guid.NewGuid():N}";
        long order = Interlocked.Increment(ref counter[0]);

        Func<string, string?, object?, ushort, byte[], bool, string[]?, string[]?, CancellationToken, Task<int>> packetHandler =
            async (connectionId, characterName, gatewayContext, opcode, payload, suppressHandshakeReply, mergedRequiredMods, mergedCapabilities, ct) =>
            {
                if (opcode != IOpcodeRegistry.ClientRulesetOpcode ||
                    !MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out _) ||
                    rulesetId != host.Options.HostRulesetId)
                {
                    return ResultNone;
                }

                onGatewayContext?.Invoke(connectionId, characterName, gatewayContext);

                bool sentReply = false;
                try
                {
                    return await host.HandleIncomingPacketLocalAsync(
                        connectionId,
                        opcode,
                        payload,
                        suppressHandshakeReply,
                        mergedRequiredMods,
                        mergedCapabilities,
                        onServerHandshakeSent: () => sentReply = true,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ReportParticipantError(onDispatchError, ownerId, ex);
                    return ResultHandled | (sentReply ? ResultSentServerHandshake : ResultNone);
                }
            };

        Func<string, string?, object?, string, bool, string[]?, string[]?, CancellationToken, Task<int>> chatHandler =
            async (connectionId, characterName, gatewayContext, commandLine, suppressHandshakeReply, mergedRequiredMods, mergedCapabilities, ct) =>
            {
                if (!MessageCodec.TryDecodeClientChatEnvelope(commandLine, out _))
                {
                    return ResultNone;
                }

                onGatewayContext?.Invoke(connectionId, characterName, gatewayContext);

                bool sentReply = false;
                try
                {
                    return await host.HandleIncomingChatCommandLocalAsync(
                        connectionId,
                        commandLine,
                        suppressHandshakeReply,
                        mergedRequiredMods,
                        mergedCapabilities,
                        onServerHandshakeSent: () => sentReply = true,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ReportParticipantError(onDispatchError, ownerId, ex);
                    return ResultHandled | (sentReply ? ResultSentServerHandshake : ResultNone);
                }
            };

        object[] entry = new object[ParticipantFieldCount];
        entry[FieldRegistrationId] = registrationId;
        entry[FieldOwnerId] = ownerId;
        entry[FieldOrder] = order;
        entry[FieldHostRulesetId] = host.Options.HostRulesetId;
        entry[FieldRequiredMods] = host.Options.RequiredMods.Count == 0 ? Array.Empty<string>() : [.. host.Options.RequiredMods];
        entry[FieldServerCapabilities] = host.Options.ServerCapabilities.Count == 0 ? Array.Empty<string>() : [.. host.Options.ServerCapabilities];
        entry[FieldPacketHandler] = packetHandler;
        entry[FieldChatHandler] = chatHandler;

        participants[registrationId] = entry;

        return new GatewayBridgeRegistration(host, resolvedKey, registrationId, ownerId, onDispatchError);
    }

    /// <summary>Returns the number of currently registered mod bridges on <paramref name="domainKey"/>.</summary>
    public static int GetParticipantCount(string? domainKey = null)
    {
        string resolvedKey = string.IsNullOrWhiteSpace(domainKey) ? DefaultDomainKey : domainKey;
        if (AppDomain.CurrentDomain.GetData(resolvedKey) is not ConcurrentDictionary<string, object> state)
        {
            return 0;
        }

        return GetParticipants(state).Count;
    }

    /// <summary>Returns the ordered owner identifiers of all currently registered mod bridges on <paramref name="domainKey"/>.</summary>
    public static IReadOnlyList<string> GetParticipantOwners(string? domainKey = null)
    {
        string resolvedKey = string.IsNullOrWhiteSpace(domainKey) ? DefaultDomainKey : domainKey;
        if (AppDomain.CurrentDomain.GetData(resolvedKey) is not ConcurrentDictionary<string, object> state)
        {
            return [];
        }

        object[][] snapshot = SnapshotParticipants(GetParticipants(state));
        var owners = new string[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            owners[i] = (string)snapshot[i][FieldOwnerId];
        }

        return owners;
    }

    /// <summary>Removes all shared bus state stored under <paramref name="domainKey"/> (primarily for test isolation).</summary>
    public static void Clear(string? domainKey = null)
    {
        string resolvedKey = string.IsNullOrWhiteSpace(domainKey) ? DefaultDomainKey : domainKey;
        lock (AppDomain.CurrentDomain)
        {
            AppDomain.CurrentDomain.SetData(resolvedKey, null);
        }
    }

    internal static void Unregister(string domainKey, string registrationId)
    {
        if (AppDomain.CurrentDomain.GetData(domainKey) is not ConcurrentDictionary<string, object> state)
        {
            return;
        }

        GetParticipants(state).TryRemove(registrationId, out _);
    }

    internal static void ClearCachedHandshake(string domainKey, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) ||
            AppDomain.CurrentDomain.GetData(domainKey) is not ConcurrentDictionary<string, object> state)
        {
            return;
        }

        GetHandshakes(state).TryRemove(connectionId, out _);
    }

    internal static bool TryGetCachedHandshake(
        string domainKey,
        string connectionId,
        byte expectedRulesetId,
        out byte[] envelopeBytes,
        out bool isChatFallback)
    {
        envelopeBytes = [];
        isChatFallback = false;

        if (string.IsNullOrWhiteSpace(connectionId) ||
            AppDomain.CurrentDomain.GetData(domainKey) is not ConcurrentDictionary<string, object> state)
        {
            return false;
        }

        if (!GetHandshakes(state).TryGetValue(connectionId, out object[]? entry) ||
            entry.Length < HandshakeFieldCount)
        {
            return false;
        }

        byte cachedRuleset = (byte)entry[HandshakeRulesetId];
        bool cachedChat = (bool)entry[HandshakeIsChatFallback];
        if (!cachedChat && cachedRuleset != expectedRulesetId)
        {
            return false;
        }

        if (entry[HandshakeEnvelopeBytes] is not byte[] bytes || bytes.Length == 0)
        {
            return false;
        }

        envelopeBytes = bytes;
        isChatFallback = cachedChat;
        return true;
    }

    internal static async Task<bool> DispatchPacketAsync(
        string domainKey,
        string connectionId,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        string? characterName,
        object? gatewayContext,
        Action<string, Exception>? onDispatchError,
        CancellationToken cancellationToken)
    {
        if (opcode != IOpcodeRegistry.ClientRulesetOpcode || payload.Length < 2)
        {
            return false;
        }

        if (!MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner))
        {
            return false;
        }

        ModEnvelope envelope;
        try
        {
            envelope = MessageCodec.DecodeEnvelope(inner.Span);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (AppDomain.CurrentDomain.GetData(domainKey) is not ConcurrentDictionary<string, object> state)
        {
            return false;
        }

        object[][] participants = SnapshotParticipants(GetParticipants(state));
        if (participants.Length == 0)
        {
            return false;
        }

        bool isClientHandshake = IsClientHandshakeEnvelope(envelope);
        string[]? mergedRequiredMods = null;
        string[]? mergedCapabilities = null;

        if (isClientHandshake)
        {
            // Cache raw handshake envelope BEFORE fanning out so even synchronous loopback replies or
            // hot-reloaded mods immediately see HasNativeHost == true via GetOrCreateSession.
            CacheHandshake(state, connectionId, rulesetId, inner.ToArray(), isChatFallback: false);
            (mergedRequiredMods, mergedCapabilities) = MergeHandshakeMetadata(participants, rulesetId, filterByRuleset: true);
        }

        byte[] payloadBytes = payload.ToArray();
        bool anyHandled = false;
        bool serverHandshakeSent = false;

        foreach (object[] entry in participants)
        {
            if ((byte)entry[FieldHostRulesetId] != rulesetId)
            {
                continue;
            }

            if (entry[FieldPacketHandler] is not Func<string, string?, object?, ushort, byte[], bool, string[]?, string[]?, CancellationToken, Task<int>> handler)
            {
                continue;
            }

            string participantOwner = (string)entry[FieldOwnerId];
            try
            {
                int result = await handler(
                    connectionId,
                    characterName,
                    gatewayContext,
                    opcode,
                    payloadBytes,
                    serverHandshakeSent,
                    mergedRequiredMods,
                    mergedCapabilities,
                    cancellationToken).ConfigureAwait(false);

                if ((result & ResultHandled) != 0)
                {
                    anyHandled = true;
                }

                if ((result & ResultSentServerHandshake) != 0)
                {
                    serverHandshakeSent = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportParticipantError(onDispatchError, participantOwner, ex);
                anyHandled = true;
            }
        }

        return anyHandled;
    }

    internal static async Task<bool> DispatchChatCommandAsync(
        string domainKey,
        string connectionId,
        string commandLine,
        string? characterName,
        object? gatewayContext,
        Action<string, Exception>? onDispatchError,
        CancellationToken cancellationToken)
    {
        if (!MessageCodec.TryDecodeClientChatEnvelope(commandLine, out ModEnvelope? envelope) || envelope is null)
        {
            return false;
        }

        if (AppDomain.CurrentDomain.GetData(domainKey) is not ConcurrentDictionary<string, object> state)
        {
            return false;
        }

        object[][] participants = SnapshotParticipants(GetParticipants(state));
        if (participants.Length == 0)
        {
            return false;
        }

        bool isClientHandshake = IsClientHandshakeEnvelope(envelope);
        string[]? mergedRequiredMods = null;
        string[]? mergedCapabilities = null;

        if (isClientHandshake)
        {
            CacheHandshake(state, connectionId, rulesetId: 0, MessageCodec.EncodeEnvelope(envelope), isChatFallback: true);
            (mergedRequiredMods, mergedCapabilities) = MergeHandshakeMetadata(participants, rulesetId: 0, filterByRuleset: false);
        }

        bool anyHandled = false;
        bool serverHandshakeSent = false;

        foreach (object[] entry in participants)
        {
            if (entry[FieldChatHandler] is not Func<string, string?, object?, string, bool, string[]?, string[]?, CancellationToken, Task<int>> handler)
            {
                continue;
            }

            string participantOwner = (string)entry[FieldOwnerId];
            try
            {
                int result = await handler(
                    connectionId,
                    characterName,
                    gatewayContext,
                    commandLine,
                    serverHandshakeSent,
                    mergedRequiredMods,
                    mergedCapabilities,
                    cancellationToken).ConfigureAwait(false);

                if ((result & ResultHandled) != 0)
                {
                    anyHandled = true;
                }

                if ((result & ResultSentServerHandshake) != 0)
                {
                    serverHandshakeSent = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportParticipantError(onDispatchError, participantOwner, ex);
                anyHandled = true;
            }
        }

        return anyHandled;
    }

    private static bool IsClientHandshakeEnvelope(ModEnvelope envelope) =>
        string.Equals(envelope.Channel, MessageCodec.SystemChannel, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(envelope.MessageType, nameof(ClientHandshake), StringComparison.Ordinal);

    private static (string[] RequiredMods, string[] ServerCapabilities) MergeHandshakeMetadata(
        object[][] participants,
        byte rulesetId,
        bool filterByRuleset)
    {
        var reqSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reqList = new List<string>();
        var capSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capList = new List<string>();

        foreach (object[] entry in participants)
        {
            if (filterByRuleset && (byte)entry[FieldHostRulesetId] != rulesetId)
            {
                continue;
            }

            if (entry[FieldRequiredMods] is string[] reqs)
            {
                foreach (string mod in reqs)
                {
                    if (!string.IsNullOrWhiteSpace(mod) && reqSet.Add(mod))
                    {
                        reqList.Add(mod);
                    }
                }
            }

            if (entry[FieldServerCapabilities] is string[] caps)
            {
                foreach (string cap in caps)
                {
                    if (!string.IsNullOrWhiteSpace(cap) && capSet.Add(cap))
                    {
                        capList.Add(cap);
                    }
                }
            }
        }

        return (reqList.Count == 0 ? Array.Empty<string>() : reqList.ToArray(),
                capList.Count == 0 ? Array.Empty<string>() : capList.ToArray());
    }

    private static void CacheHandshake(
        ConcurrentDictionary<string, object> state,
        string connectionId,
        byte rulesetId,
        byte[] rawEnvelopeBytes,
        bool isChatFallback)
    {
        ConcurrentDictionary<string, object[]> handshakes = GetHandshakes(state);

        if (handshakes.Count >= MaxCachedHandshakes)
        {
            long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
            foreach (KeyValuePair<string, object[]> kv in handshakes)
            {
                if (kv.Value.Length >= HandshakeFieldCount && (long)kv.Value[HandshakeTimestampTicks] < cutoff)
                {
                    handshakes.TryRemove(kv.Key, out _);
                }
            }
        }

        object[] entry = new object[HandshakeFieldCount];
        entry[HandshakeRulesetId] = rulesetId;
        entry[HandshakeEnvelopeBytes] = rawEnvelopeBytes;
        entry[HandshakeIsChatFallback] = isChatFallback;
        entry[HandshakeTimestampTicks] = DateTime.UtcNow.Ticks;
        handshakes[connectionId] = entry;
    }

    private static object[][] SnapshotParticipants(ConcurrentDictionary<string, object[]> participants)
    {
        object[][] list = participants.Values.Where(e => e.Length >= ParticipantFieldCount).ToArray();
        if (list.Length > 1)
        {
            Array.Sort(list, static (a, b) => ((long)a[FieldOrder]).CompareTo((long)b[FieldOrder]));
        }

        return list;
    }

    private static ConcurrentDictionary<string, object> GetOrCreateState(string domainKey)
    {
        if (AppDomain.CurrentDomain.GetData(domainKey) is ConcurrentDictionary<string, object> existing)
        {
            return existing;
        }

        lock (AppDomain.CurrentDomain)
        {
            if (AppDomain.CurrentDomain.GetData(domainKey) is ConcurrentDictionary<string, object> lockedExisting)
            {
                return lockedExisting;
            }

            var created = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
            created[ParticipantsKey] = new ConcurrentDictionary<string, object[]>(StringComparer.Ordinal);
            created[CounterKey] = new long[1];
            created[HandshakesKey] = new ConcurrentDictionary<string, object[]>(StringComparer.Ordinal);
            AppDomain.CurrentDomain.SetData(domainKey, created);
            return created;
        }
    }

    private static ConcurrentDictionary<string, object[]> GetParticipants(ConcurrentDictionary<string, object> state) =>
        (ConcurrentDictionary<string, object[]>)state.GetOrAdd(
            ParticipantsKey,
            static _ => new ConcurrentDictionary<string, object[]>(StringComparer.Ordinal));

    private static long[] GetCounter(ConcurrentDictionary<string, object> state) =>
        (long[])state.GetOrAdd(CounterKey, static _ => new long[1]);

    private static ConcurrentDictionary<string, object[]> GetHandshakes(ConcurrentDictionary<string, object> state) =>
        (ConcurrentDictionary<string, object[]>)state.GetOrAdd(
            HandshakesKey,
            static _ => new ConcurrentDictionary<string, object[]>(StringComparer.Ordinal));

    private static void ReportParticipantError(Action<string, Exception>? callback, string ownerId, Exception ex)
    {
        if (callback is not null)
        {
            try
            {
                callback(ownerId, ex);
                return;
            }
            catch
            {
                // Never allow error logger failure to break other mods' dispatch.
            }
        }

        Trace.TraceWarning($"[GatewayBridgeBus] Mod '{ownerId}' threw during packet fan-out: {ex}");
    }
}

/// <summary>
/// Active registration binding a <see cref="ClientBridgeHost"/> to the cross-ALC <see cref="GatewayBridgeBus"/>.
/// Dispose when the owning mod unloads or reloads.
/// </summary>
public sealed class GatewayBridgeRegistration : IDisposable
{
    private readonly ClientBridgeHost _host;
    private readonly string _domainKey;
    private readonly string _registrationId;
    private readonly Action<string, Exception>? _onDispatchError;
    private int _disposed;

    /// <summary>Mod owner identifier registered on the bus.</summary>
    public string OwnerId { get; }

    /// <summary><see cref="AppDomain.CurrentDomain"/> slot key this registration belongs to.</summary>
    public string DomainKey => _domainKey;

    internal GatewayBridgeRegistration(
        ClientBridgeHost host,
        string domainKey,
        string registrationId,
        string ownerId,
        Action<string, Exception>? onDispatchError)
    {
        _host = host;
        _domainKey = domainKey;
        _registrationId = registrationId;
        OwnerId = ownerId;
        _onDispatchError = onDispatchError;
    }

    /// <summary>
    /// Fans out an incoming C→S ruleset packet (<c>0x02CA</c>) to all attached <see cref="ClientBridgeHost"/>
    /// instances across all load contexts, deduplicating <see cref="ServerHandshake"/> replies.
    /// </summary>
    public Task<bool> DispatchPacketAsync(
        string connectionId,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        string? characterName = null,
        object? gatewayContext = null,
        CancellationToken cancellationToken = default) =>
        GatewayBridgeBus.DispatchPacketAsync(
            _domainKey,
            connectionId,
            opcode,
            payload,
            characterName,
            gatewayContext,
            _onDispatchError,
            cancellationToken);

    /// <summary>
    /// Fans out an incoming C→S chat fallback command (<c>/nm env ...</c>) to all attached
    /// <see cref="ClientBridgeHost"/> instances across all load contexts, deduplicating <see cref="ServerHandshake"/> replies.
    /// </summary>
    public Task<bool> DispatchChatCommandAsync(
        string connectionId,
        string commandLine,
        string? characterName = null,
        object? gatewayContext = null,
        CancellationToken cancellationToken = default) =>
        GatewayBridgeBus.DispatchChatCommandAsync(
            _domainKey,
            connectionId,
            commandLine,
            characterName,
            gatewayContext,
            _onDispatchError,
            cancellationToken);

    internal bool TryGetCachedHandshake(string connectionId, byte expectedRulesetId, out byte[] envelopeBytes, out bool isChatFallback) =>
        GatewayBridgeBus.TryGetCachedHandshake(_domainKey, connectionId, expectedRulesetId, out envelopeBytes, out isChatFallback);

    internal void ClearCachedHandshake(string connectionId) =>
        GatewayBridgeBus.ClearCachedHandshake(_domainKey, connectionId);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GatewayBridgeBus.Unregister(_domainKey, _registrationId);
        _host.DetachBusRegistration(this);
    }
}
