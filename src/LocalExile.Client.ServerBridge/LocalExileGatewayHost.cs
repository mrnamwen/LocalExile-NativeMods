using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Lightweight Gateway player context passed to <see cref="LocalExileGatewayHost"/> lifecycle callbacks
/// without introducing a compile-time dependency on <c>GameServer.Gateway</c>.
/// </summary>
/// <param name="ConnectionId">Unique connection key (<c>$"{Remote}#{Id}"</c>).</param>
/// <param name="CharacterName">Active character name.</param>
/// <param name="Player">Live Gateway <c>Player</c> instance.</param>
/// <param name="ClientChannel">Optional live <c>global::Client.For(player)</c> channel instance.</param>
public sealed record GatewayPlayerContext(
    string ConnectionId,
    string CharacterName,
    object Player,
    object? ClientChannel = null);

/// <summary>
/// Lightweight Gateway chat message context forwarded from <c>GameEvents.ClientMessage</c>.
/// </summary>
/// <param name="ConnectionId">Unique connection key.</param>
/// <param name="CharacterName">Active character name.</param>
/// <param name="Player">Live Gateway <c>Player</c> instance.</param>
/// <param name="Text">Raw chat or slash-command text.</param>
/// <param name="Links">Linked item ids attached to the chat message.</param>
/// <param name="IsCommand"><c>true</c> if <paramref name="Text"/> starts with <c>/</c>.</param>
public sealed record GatewayClientMessageContext(
    string ConnectionId,
    string CharacterName,
    object Player,
    string Text,
    IReadOnlyList<uint> Links,
    bool IsCommand);

/// <summary>
/// Lightweight Gateway cosmetic change context forwarded from <c>GameEvents.CosmeticChanged</c>.
/// </summary>
/// <param name="ConnectionId">Unique connection key.</param>
/// <param name="CharacterName">Active character name.</param>
/// <param name="Player">Live Gateway <c>Player</c> instance.</param>
/// <param name="Slot">Cosmetic equipment slot index.</param>
/// <param name="RecordId">Cosmetic MTX record id.</param>
/// <param name="MtxTypeHash">Cosmetic MTX type hash.</param>
/// <param name="Option">Optional variant byte.</param>
/// <param name="GemHash">Associated skill gem hash for skill cosmetics, or <c>0</c>.</param>
/// <param name="IsSkillCosmetic"><c>true</c> if <paramref name="GemHash"/> is non-zero.</param>
public sealed record GatewayCosmeticChangeContext(
    string ConnectionId,
    string CharacterName,
    object Player,
    byte Slot,
    uint RecordId,
    uint MtxTypeHash,
    byte Option = 0,
    uint GemHash = 0,
    bool IsSkillCosmetic = false);

/// <summary>
/// Abstraction over LocalExile's <c>global::Client</c>, <c>global::GameEvents</c>, and <c>global::Program</c>
/// allowing <see cref="LocalExileGatewayHost"/> and <see cref="LocalExileGatewaySink"/> to work both via
/// cached reflection against live Gateway assemblies and with fake adapters in unit tests.
/// </summary>
public interface ILocalExileGatewayAdapter
{
    /// <summary>Extracts the mod owner id from <paramref name="modContext"/>, or <c>null</c> if unavailable.</summary>
    string? TryGetOwnerId(object modContext);

    /// <summary>Extracts the mod logger callback from <paramref name="modContext"/>, or <c>null</c> if unavailable.</summary>
    Action<string>? TryGetLogger(object modContext);

    /// <summary>Returns the canonical Gateway connection identifier for <paramref name="player"/>.</summary>
    string GetConnectionId(object player);

    /// <summary>Returns the active character name for <paramref name="player"/>.</summary>
    string GetCharacterName(object player);

    /// <summary>Returns whether <paramref name="player"/>'s connection has closed.</summary>
    bool IsPlayerClosed(object player);

    /// <summary>Returns <c>true</c> when <c>global::Client.For(player)</c> / <c>OnRuleset</c> / <c>OnCommand</c> is available.</summary>
    bool SupportsCoreClientChannel { get; }

    /// <summary>Returns the optional <c>global::Client.For(player)</c> channel object for <paramref name="player"/>.</summary>
    object? TryGetClientChannel(object player);

    /// <summary>Sends a raw S→C packet to <paramref name="player"/>.</summary>
    Task SendPacketAsync(object player, ushort opcode, ReadOnlyMemory<byte> payload, string? log, CancellationToken cancellationToken);

    /// <summary>Sends a chat reply to <paramref name="player"/>.</summary>
    Task SendChatReplyAsync(object player, string text, CancellationToken cancellationToken);

    /// <summary>Subscribes to unframed C→S <c>0x02CA</c> ruleset payloads via <c>global::Client.OnRuleset</c>.</summary>
    IDisposable SubscribeRuleset(string ownerId, byte rulesetId, Func<object, string, ReadOnlyMemory<byte>, Task<bool>> onInnerPayload);

    /// <summary>Subscribes to <c>/nm</c> commands via <c>global::Client.OnCommand</c>.</summary>
    IDisposable SubscribeClientCommand(string ownerId, Func<object, string, string, Task<bool>> onCommand);

    /// <summary>Subscribes to raw C→S packets via <c>GameEvents.Packet(opcode)</c>.</summary>
    IDisposable SubscribePacket(string ownerId, ushort opcode, Func<object, byte[], Task<bool>> onPacket);

    /// <summary>Subscribes to a slash command via <c>GameEvents.Command</c>.</summary>
    IDisposable SubscribeCommand(string ownerId, string commandName, string usage, Func<object, string[], Task> onCommand);

    /// <summary>Subscribes to <c>GameEvents.PlayerEntered</c>.</summary>
    IDisposable SubscribePlayerEntered(string ownerId, Func<object, Task> onEntered);

    /// <summary>Subscribes to <c>GameEvents.PlayerLeaving</c>.</summary>
    IDisposable SubscribePlayerLeaving(string ownerId, Func<object, string, Task> onLeaving);

    /// <summary>Subscribes to <c>GameEvents.ClientReady</c> if available.</summary>
    IDisposable? SubscribeClientReady(string ownerId, Func<object, Task> onReady);

    /// <summary>Subscribes to <c>GameEvents.ClientMessage</c> if available.</summary>
    IDisposable? SubscribeClientMessage(string ownerId, Func<GatewayClientMessageContext, Task<bool>> onMessage);

    /// <summary>Subscribes to <c>GameEvents.CosmeticChanged</c> if available.</summary>
    IDisposable? SubscribeCosmeticChanged(string ownerId, Func<GatewayCosmeticChangeContext, Task> onCosmeticChanged);
}

/// <summary>
/// Configuration options for <see cref="LocalExileGatewayHost"/>.
/// </summary>
public sealed class LocalExileGatewayHostOptions
{
    /// <summary>Optional mod owner id; defaults to <c>modContext.Manifest.Id</c>.</summary>
    public string? OwnerId { get; set; }

    /// <summary>Optional log callback; defaults to <c>modContext.Log</c>.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Bridge options used when creating a new <see cref="ClientBridgeHost"/> via <see cref="LocalExileGatewayHost.Create"/>.</summary>
    public ClientBridgeOptions BridgeOptions { get; set; } = new()
    {
        ServerVersion = "LocalExile-3.29.3d",
        HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
    };

    /// <summary>Optional <see cref="GatewayBridgeBus"/> <see cref="AppDomain"/> slot key.</summary>
    public string? BusDomainKey { get; set; }

    /// <summary>Whether to attach the host bridge to <see cref="GatewayBridgeBus"/> (default <c>true</c>).</summary>
    public bool AttachToBus { get; set; } = true;

    /// <summary>Whether to prefer <c>global::Client.OnRuleset</c> / <c>OnCommand</c> when available (default <c>true</c>).</summary>
    public bool UseCoreClientChannel { get; set; } = true;

    /// <summary>Whether to remove closed player sessions on <c>PlayerLeaving</c> (default <c>true</c>).</summary>
    public bool RemoveSessionOnDisconnect { get; set; } = true;

    /// <summary>Optional callback invoked when any <see cref="GatewayBridgeBus"/> participant throws.</summary>
    public Action<string, Exception>? OnBusDispatchError { get; set; }

    /// <summary>Optional callback invoked with <c>(connectionId, characterName, player)</c> before bus dispatch.</summary>
    public Action<string, string?, object?>? OnGatewayContext { get; set; }

    /// <summary>Optional callback invoked when a player enters an area.</summary>
    public Func<GatewayPlayerContext, Task>? OnPlayerEntered { get; set; }

    /// <summary>Optional callback invoked when a player leaves an area or disconnects.</summary>
    public Func<GatewayPlayerContext, string, Task>? OnPlayerLeaving { get; set; }

    /// <summary>Optional callback invoked when <c>GameEvents.ClientReady</c> fires.</summary>
    public Func<GatewayPlayerContext, Task>? OnClientReady { get; set; }

    /// <summary>Optional callback invoked when <c>GameEvents.ClientMessage</c> fires.</summary>
    public Func<GatewayClientMessageContext, Task<bool>>? OnClientMessage { get; set; }

    /// <summary>Optional callback invoked when <c>GameEvents.CosmeticChanged</c> fires.</summary>
    public Func<GatewayCosmeticChangeContext, Task>? OnCosmeticChanged { get; set; }

    /// <summary>Optional Gateway adapter override (defaults to cached reflection over <c>GameServer.Gateway</c>).</summary>
    public ILocalExileGatewayAdapter? Adapter { get; set; }

    /// <summary>Optional Gateway/Modding assembly hint scoping reflection resolution to its <see cref="AssemblyLoadContext"/>.</summary>
    public Assembly? GatewayAssembly { get; set; }
}

/// <summary>
/// Fluent builder configuring a <see cref="LocalExileGatewayHost"/> created via <see cref="LocalExileGatewayHost.Create"/>.
/// </summary>
public sealed class LocalExileGatewayHostBuilder
{
    internal LocalExileGatewayHostOptions Options { get; } = new();

    /// <summary>Sets the mod owner identifier (defaults to <c>modContext.Manifest.Id</c>).</summary>
    public LocalExileGatewayHostBuilder WithOwnerId(string ownerId)
    {
        Options.OwnerId = ownerId;
        return this;
    }

    /// <summary>Sets the log callback (defaults to <c>modContext.Log</c>).</summary>
    public LocalExileGatewayHostBuilder WithLogger(Action<string> log)
    {
        Options.Log = log;
        return this;
    }

    /// <summary>Sets <see cref="ClientBridgeOptions"/>.</summary>
    public LocalExileGatewayHostBuilder WithBridgeOptions(ClientBridgeOptions bridgeOptions)
    {
        Options.BridgeOptions = bridgeOptions ?? throw new ArgumentNullException(nameof(bridgeOptions));
        return this;
    }

    /// <summary>Configures <see cref="GatewayBridgeBus"/> attachment and domain key.</summary>
    public LocalExileGatewayHostBuilder WithBus(bool attach = true, string? domainKey = null, Action<string, Exception>? onDispatchError = null)
    {
        Options.AttachToBus = attach;
        Options.BusDomainKey = domainKey;
        Options.OnBusDispatchError = onDispatchError;
        return this;
    }

    /// <summary>Configures whether <c>global::Client</c> core channel hooks are preferred over raw <c>GameEvents.Packet(0x02CA)</c>.</summary>
    public LocalExileGatewayHostBuilder UseCoreClientChannel(bool enabled = true)
    {
        Options.UseCoreClientChannel = enabled;
        return this;
    }

    /// <summary>Sets the custom <see cref="ILocalExileGatewayAdapter"/>.</summary>
    public LocalExileGatewayHostBuilder WithAdapter(ILocalExileGatewayAdapter adapter)
    {
        Options.Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        return this;
    }

    /// <summary>Scopes Gateway reflection resolution to the <see cref="AssemblyLoadContext"/> containing <paramref name="gatewayAssembly"/>.</summary>
    public LocalExileGatewayHostBuilder WithGatewayAssembly(Assembly gatewayAssembly)
    {
        Options.GatewayAssembly = gatewayAssembly ?? throw new ArgumentNullException(nameof(gatewayAssembly));
        return this;
    }

    /// <summary>Sets a callback invoked before fanned-out bus dispatch.</summary>
    public LocalExileGatewayHostBuilder OnGatewayContext(Action<string, string?, object?> callback)
    {
        Options.OnGatewayContext = callback;
        return this;
    }

    /// <summary>Sets a callback invoked on <c>GameEvents.PlayerEntered</c>.</summary>
    public LocalExileGatewayHostBuilder OnPlayerEntered(Func<GatewayPlayerContext, Task> handler)
    {
        Options.OnPlayerEntered = handler;
        return this;
    }

    /// <summary>Sets a callback invoked on <c>GameEvents.PlayerLeaving</c>.</summary>
    public LocalExileGatewayHostBuilder OnPlayerLeaving(Func<GatewayPlayerContext, string, Task> handler)
    {
        Options.OnPlayerLeaving = handler;
        return this;
    }

    /// <summary>Sets a callback invoked on <c>GameEvents.ClientReady</c>.</summary>
    public LocalExileGatewayHostBuilder OnClientReady(Func<GatewayPlayerContext, Task> handler)
    {
        Options.OnClientReady = handler;
        return this;
    }

    /// <summary>Sets a callback invoked on <c>GameEvents.ClientMessage</c>.</summary>
    public LocalExileGatewayHostBuilder OnClientMessage(Func<GatewayClientMessageContext, Task<bool>> handler)
    {
        Options.OnClientMessage = handler;
        return this;
    }

    /// <summary>Sets a callback invoked on <c>GameEvents.CosmeticChanged</c>.</summary>
    public LocalExileGatewayHostBuilder OnCosmeticChanged(Func<GatewayCosmeticChangeContext, Task> handler)
    {
        Options.OnCosmeticChanged = handler;
        return this;
    }
}

/// <summary>
/// Reusable <see cref="IGatewayPacketSink"/> forwarding outbound packets and chat replies to live
/// LocalExile Gateway <c>Player</c> instances via <c>global::Client.For(player)</c> (or <c>Program.Send</c> fallback).
/// Silently no-ops when a player is absent or closed.
/// </summary>
public sealed class LocalExileGatewaySink : IGatewayPacketSink
{
    private readonly Func<string, object?> _playerLookup;
    private readonly ILocalExileGatewayAdapter _adapter;

    /// <summary>Creates a sink backed by a player lookup delegate.</summary>
    public LocalExileGatewaySink(Func<string, object?> playerLookup, ILocalExileGatewayAdapter? adapter = null)
    {
        _playerLookup = playerLookup ?? throw new ArgumentNullException(nameof(playerLookup));
        _adapter = adapter ?? ReflectionGatewayAdapter.Shared;
    }

    /// <summary>Creates a sink backed by a connection-keyed player dictionary (such as <c>ConcurrentDictionary&lt;string, Player&gt;</c>).</summary>
    public LocalExileGatewaySink(IDictionary players, ILocalExileGatewayAdapter? adapter = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        _playerLookup = connId => players.Contains(connId) ? players[connId] : null;
        _adapter = adapter ?? ReflectionGatewayAdapter.Shared;
    }

    /// <inheritdoc/>
    public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        object? player = _playerLookup(connectionId);
        if (player is null || _adapter.IsPlayerClosed(player))
        {
            return Task.CompletedTask;
        }

        string log = $"0x{opcode:x4} native-mods payload ({payload.Length} B)";
        return _adapter.SendPacketAsync(player, opcode, payload, log, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        object? player = _playerLookup(connectionId);
        if (player is null || _adapter.IsPlayerClosed(player))
        {
            return Task.CompletedTask;
        }

        return _adapter.SendChatReplyAsync(player, text, cancellationToken);
    }
}

/// <summary>
/// Single-call LocalExile Gateway host bootstrapper wiring <see cref="ClientBridgeHost"/>,
/// <see cref="GatewayBridgeBus"/> cross-ALC fan-out, <c>0x02C9</c>/<c>0x02CA</c> ruleset traffic
/// (properly re-framing <c>global::Client.OnRuleset</c> payloads), <c>/nm</c> chat fallback,
/// and Gateway player lifecycle events.
/// </summary>
public sealed class LocalExileGatewayHost : IDisposable
{
    private readonly ConcurrentDictionary<string, object> _players = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gate = new();
    private readonly ILocalExileGatewayAdapter _adapter;
    private readonly LocalExileGatewayHostOptions _options;
    private readonly bool _ownsBridge;
    private int _disposed;

    /// <summary>Mod owner identifier bound to this Gateway host.</summary>
    public string OwnerId { get; }

    /// <summary>Managed ServerBridge host.</summary>
    public ClientBridgeHost Bridge { get; }

    /// <summary>Outbound Gateway packet/chat sink backed by this host's active player registry.</summary>
    public LocalExileGatewaySink Sink { get; }

    /// <summary>Cross-ALC <see cref="GatewayBridgeBus"/> registration, if attached.</summary>
    public GatewayBridgeRegistration? BusRegistration { get; }

    private LocalExileGatewayHost(
        object modContext,
        ClientBridgeHost? externalBridge,
        LocalExileGatewayHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(modContext);
        _options = options ?? new LocalExileGatewayHostOptions();
        _adapter = _options.Adapter ?? CreateScopedAdapter(modContext, _options);

        string? resolvedOwner = !string.IsNullOrWhiteSpace(_options.OwnerId)
            ? _options.OwnerId.Trim()
            : _adapter.TryGetOwnerId(modContext);
        OwnerId = !string.IsNullOrWhiteSpace(resolvedOwner)
            ? resolvedOwner
            : throw new ArgumentException("OwnerId could not be resolved from modContext or options.", nameof(options));

        Action<string>? logger = _options.Log ?? _adapter.TryGetLogger(modContext);

        Sink = new LocalExileGatewaySink(
            connId => _players.TryGetValue(connId, out object? p) ? p : null,
            _adapter);

        if (externalBridge is null)
        {
            ClientBridgeOptions bridgeOpts = _options.BridgeOptions with
            {
                DefaultChannel = _options.BridgeOptions.DefaultChannel ?? OwnerId,
            };
            Bridge = new ClientBridgeHost(Sink, bridgeOpts);
            _ownsBridge = true;
        }
        else
        {
            Bridge = externalBridge;
            Bridge.DefaultChannel ??= OwnerId;
            _ownsBridge = false;
        }

        if (_options.AttachToBus)
        {
            BusRegistration = Bridge.AttachToGatewayBus(
                OwnerId,
                onGatewayContext: (connId, charName, gatewayContext) =>
                {
                    if (gatewayContext is not null)
                    {
                        _players[connId] = gatewayContext;
                    }

                    _options.OnGatewayContext?.Invoke(connId, charName, gatewayContext);
                },
                domainKey: _options.BusDomainKey,
                onDispatchError: _options.OnBusDispatchError ?? ((owner, ex) =>
                    logger?.Invoke($"[gateway-bus] participant '{owner}' failed: {ex.Message}")));
        }

        HookGatewayTransportAndLifecycle();
    }

    /// <summary>
    /// Creates a new self-contained <see cref="LocalExileGatewayHost"/> (owning its <see cref="ClientBridgeHost"/>
    /// and <see cref="LocalExileGatewaySink"/>) for <paramref name="modContext"/>.
    /// </summary>
    public static LocalExileGatewayHost Create(object modContext, Action<LocalExileGatewayHostBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modContext);
        var builder = new LocalExileGatewayHostBuilder();
        configure?.Invoke(builder);
        return new LocalExileGatewayHost(modContext, externalBridge: null, builder.Options);
    }

    /// <summary>
    /// Attaches an existing <see cref="ClientBridgeHost"/> to LocalExile Gateway events and <see cref="GatewayBridgeBus"/>.
    /// </summary>
    public static IDisposable Attach(
        object modContext,
        ClientBridgeHost bridge,
        out GatewayBridgeRegistration busRegistration,
        LocalExileGatewayHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modContext);
        ArgumentNullException.ThrowIfNull(bridge);

        LocalExileGatewayHostOptions effective = options ?? new LocalExileGatewayHostOptions();
        effective.AttachToBus = true;
        var host = new LocalExileGatewayHost(modContext, bridge, effective);
        busRegistration = host.BusRegistration
            ?? throw new InvalidOperationException("GatewayBridgeBus registration was not created.");
        return host;
    }

    /// <summary>
    /// Attaches an existing <see cref="ClientBridgeHost"/> to LocalExile Gateway events and <see cref="GatewayBridgeBus"/>.
    /// </summary>
    public static IDisposable Attach(
        object modContext,
        ClientBridgeHost bridge,
        LocalExileGatewayHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modContext);
        ArgumentNullException.ThrowIfNull(bridge);
        return new LocalExileGatewayHost(modContext, bridge, options ?? new LocalExileGatewayHostOptions());
    }

    /// <summary>Returns the canonical connection key for <paramref name="player"/> (<c>$"{Remote}#{Id}"</c>).</summary>
    public static string ConnectionKey(object player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return ReflectionGatewayAdapter.Shared.GetConnectionId(player);
    }

    /// <summary>Returns the active character name for <paramref name="player"/>.</summary>
    public static string GetCharacterName(object player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return ReflectionGatewayAdapter.Shared.GetCharacterName(player);
    }

    /// <summary>Looks up an active Gateway <c>Player</c> object by <paramref name="connectionId"/>.</summary>
    public bool TryGetPlayer(string connectionId, out object? player)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            player = null;
            return false;
        }

        return _players.TryGetValue(connectionId, out player);
    }

    /// <summary>Registers a Gateway slash command (<c>GameEvents.Command</c>) tracked for automatic disposal.</summary>
    public IDisposable RegisterCommand(string name, string usage, Func<GatewayPlayerContext, string[], Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(handler);

        IDisposable sub = _adapter.SubscribeCommand(OwnerId, name, usage, (player, args) =>
        {
            GatewayPlayerContext ctx = TrackAndBuildContext(player);
            return handler(ctx, args);
        });
        return TrackSubscription(sub);
    }

    /// <summary>Registers a Gateway packet handler (<c>GameEvents.Packet(opcode)</c>) tracked for automatic disposal.</summary>
    public IDisposable RegisterPacket(ushort opcode, Func<GatewayPlayerContext, byte[], Task<bool>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        IDisposable sub = _adapter.SubscribePacket(OwnerId, opcode, (player, bytes) =>
        {
            GatewayPlayerContext ctx = TrackAndBuildContext(player);
            return handler(ctx, bytes);
        });
        return TrackSubscription(sub);
    }

    private void HookGatewayTransportAndLifecycle()
    {
        if (_options.UseCoreClientChannel && _adapter.SupportsCoreClientChannel)
        {
            TrackSubscription(_adapter.SubscribeRuleset(
                OwnerId,
                Bridge.Options.HostRulesetId,
                async (player, connId, innerPayload) =>
                {
                    _players[connId] = player;
                    string characterName = _adapter.GetCharacterName(player);
                    // global::Client.OnRuleset strips [u8 rulesetId][CompactUInt length]; re-frame so
                    // ClientBridgeHost & GatewayBridgeBus process an identical 0x02CA body on both paths.
                    byte[] framedBody = MessageCodec.EncodeRulesetBody(Bridge.Options.HostRulesetId, innerPayload.Span);
                    return await Bridge.HandleIncomingPacketAsync(
                        connId,
                        IOpcodeRegistry.ClientRulesetOpcode,
                        framedBody,
                        characterName,
                        player).ConfigureAwait(false);
                }));

            TrackSubscription(_adapter.SubscribeClientCommand(
                OwnerId,
                async (player, connId, commandLine) =>
                {
                    _players[connId] = player;
                    string characterName = _adapter.GetCharacterName(player);
                    return await Bridge.HandleIncomingChatCommandAsync(
                        connId,
                        commandLine,
                        characterName,
                        player).ConfigureAwait(false);
                }));
        }
        else
        {
            TrackSubscription(_adapter.SubscribePacket(
                OwnerId,
                IOpcodeRegistry.ClientRulesetOpcode,
                async (player, bytes) =>
                {
                    if (bytes.Length <= 2)
                    {
                        return false;
                    }

                    string connId = _adapter.GetConnectionId(player);
                    string characterName = _adapter.GetCharacterName(player);
                    _players[connId] = player;
                    return await Bridge.HandleIncomingPacketAsync(
                        connId,
                        IOpcodeRegistry.ClientRulesetOpcode,
                        bytes.AsMemory(2),
                        characterName,
                        player).ConfigureAwait(false);
                }));

            TrackSubscription(_adapter.SubscribeCommand(
                OwnerId,
                "nm",
                "/nm <native-mods-envelope>",
                async (player, args) =>
                {
                    string connId = _adapter.GetConnectionId(player);
                    string characterName = _adapter.GetCharacterName(player);
                    _players[connId] = player;
                    string line = args.Length == 0 ? "/nm" : "/nm " + string.Join(' ', args);
                    await Bridge.HandleIncomingChatCommandAsync(connId, line, characterName, player).ConfigureAwait(false);
                }));
        }

        TrackSubscription(_adapter.SubscribePlayerEntered(OwnerId, async player =>
        {
            GatewayPlayerContext ctx = TrackAndBuildContext(player);
            if (_options.OnPlayerEntered is not null)
            {
                await _options.OnPlayerEntered(ctx).ConfigureAwait(false);
            }
        }));

        TrackSubscription(_adapter.SubscribePlayerLeaving(OwnerId, async (player, reason) =>
        {
            GatewayPlayerContext ctx = BuildContext(player);
            try
            {
                if (_options.OnPlayerLeaving is not null)
                {
                    await _options.OnPlayerLeaving(ctx, reason).ConfigureAwait(false);
                }
            }
            finally
            {
                if (_options.RemoveSessionOnDisconnect && _adapter.IsPlayerClosed(player))
                {
                    _players.TryRemove(ctx.ConnectionId, out _);
                    await Bridge.RemoveSessionAsync(ctx.ConnectionId).ConfigureAwait(false);
                }
            }
        }));

        if (_options.OnClientReady is not null &&
            _adapter.SubscribeClientReady(OwnerId, async player =>
            {
                GatewayPlayerContext ctx = TrackAndBuildContext(player);
                await _options.OnClientReady(ctx).ConfigureAwait(false);
            }) is { } readySub)
        {
            TrackSubscription(readySub);
        }

        if (_options.OnClientMessage is not null &&
            _adapter.SubscribeClientMessage(OwnerId, async msg =>
            {
                _players[msg.ConnectionId] = msg.Player;
                return await _options.OnClientMessage(msg).ConfigureAwait(false);
            }) is { } msgSub)
        {
            TrackSubscription(msgSub);
        }

        if (_options.OnCosmeticChanged is not null &&
            _adapter.SubscribeCosmeticChanged(OwnerId, async evt =>
            {
                _players[evt.ConnectionId] = evt.Player;
                await _options.OnCosmeticChanged(evt).ConfigureAwait(false);
            }) is { } cosSub)
        {
            TrackSubscription(cosSub);
        }
    }

    private GatewayPlayerContext TrackAndBuildContext(object player)
    {
        GatewayPlayerContext ctx = BuildContext(player);
        _players[ctx.ConnectionId] = player;
        return ctx;
    }

    private GatewayPlayerContext BuildContext(object player)
    {
        string connId = _adapter.GetConnectionId(player);
        string charName = _adapter.GetCharacterName(player);
        object? channel = _adapter.TryGetClientChannel(player);
        return new GatewayPlayerContext(connId, charName, player, channel);
    }

    private IDisposable TrackSubscription(IDisposable sub)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                sub.Dispose();
                return sub;
            }

            _subscriptions.Add(sub);
        }

        return sub;
    }

    private static ReflectionGatewayAdapter CreateScopedAdapter(object modContext, LocalExileGatewayHostOptions options)
    {
        Assembly? hintAssembly = ResolveHintAssembly(modContext, options);
        return hintAssembly is null
            ? ReflectionGatewayAdapter.Shared
            : new ReflectionGatewayAdapter(hintAssembly);
    }

    private static Assembly? ResolveHintAssembly(object modContext, LocalExileGatewayHostOptions options)
    {
        if (options.GatewayAssembly is not null)
        {
            return options.GatewayAssembly;
        }

        Type modContextType = modContext.GetType();
        Assembly concreteAssembly = modContextType.Assembly;
        if (concreteAssembly.GetType("GameEvents", throwOnError: false) is not null &&
            concreteAssembly.GetType("Program", throwOnError: false) is not null)
        {
            return concreteAssembly;
        }

        Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Type iface in modContextType.GetInterfaces())
        {
            if ((iface.Name is "IModContext" or "IModuleContext" ||
                 string.Equals(iface.Namespace, "GameServer.Modding", StringComparison.Ordinal)) &&
                HasGatewayAssemblyInSameAlc(loadedAssemblies, iface.Assembly))
            {
                return iface.Assembly;
            }
        }

        return HasGatewayAssemblyInSameAlc(loadedAssemblies, concreteAssembly)
            ? concreteAssembly
            : null;
    }

    private static bool HasGatewayAssemblyInSameAlc(Assembly[] assemblies, Assembly candidate)
    {
        AssemblyLoadContext? alc = AssemblyLoadContext.GetLoadContext(candidate);
        if (alc is null)
        {
            return false;
        }

        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            Assembly asm = assemblies[i];
            if (AssemblyLoadContext.GetLoadContext(asm) == alc &&
                asm.GetType("GameEvents", throwOnError: false) is not null &&
                asm.GetType("Program", throwOnError: false) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IDisposable[] subs;
        lock (_gate)
        {
            subs = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (IDisposable sub in subs)
        {
            try
            {
                sub.Dispose();
            }
            catch
            {
                // Best-effort cleanup on unload.
            }
        }

        BusRegistration?.Dispose();
        if (_ownsBridge)
        {
            Bridge.Dispose();
        }

        _players.Clear();
    }
}

internal sealed class ReflectionGatewayAdapter(Assembly? defaultHintAssembly = null) : ILocalExileGatewayAdapter
{
    public static readonly ReflectionGatewayAdapter Shared = new();

    private readonly Assembly? _defaultHintAssembly = defaultHintAssembly;
    private readonly Lock _cacheGate = new();
    private readonly ConditionalWeakTable<Assembly, GatewayTypes> _typesByAssembly = new();

    public bool SupportsCoreClientChannel => ResolveTypes(_defaultHintAssembly)?.ClientType is not null;

    public string? TryGetOwnerId(object modContext)
    {
        ArgumentNullException.ThrowIfNull(modContext);
        Type t = modContext.GetType();
        object? manifest = t.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Instance)?.GetValue(modContext);
        if (manifest is null)
        {
            return null;
        }

        return manifest.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manifest) as string;
    }

    public Action<string>? TryGetLogger(object modContext)
    {
        ArgumentNullException.ThrowIfNull(modContext);
        Type t = modContext.GetType();
        MethodInfo? logMethod = t.GetMethod("Log", BindingFlags.Public | BindingFlags.Instance, [typeof(string)]);
        if (logMethod is null)
        {
            return null;
        }

        return message => logMethod.Invoke(modContext, [message]);
    }

    public object? TryGetClientChannel(object player)
    {
        ArgumentNullException.ThrowIfNull(player);
        GatewayTypes? types = ResolveTypes(player.GetType().Assembly);
        if (types?.ClientForPlayer is null)
        {
            return null;
        }

        return types.ClientForPlayer.Invoke(null, [player]);
    }

    public string GetConnectionId(object player)
    {
        ArgumentNullException.ThrowIfNull(player);
        object? channel = TryGetClientChannel(player);
        if (channel is not null)
        {
            if (channel.GetType().GetProperty("ConnectionId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channel) is string cid &&
                !string.IsNullOrWhiteSpace(cid))
            {
                return cid;
            }
        }

        Type pt = player.GetType();
        object? idObj = pt.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player);
        object? connObj = pt.GetProperty("Connection", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player);
        object? remoteObj = connObj?.GetType().GetProperty("Remote", BindingFlags.Public | BindingFlags.Instance)?.GetValue(connObj);
        return string.Create(CultureInfo.InvariantCulture, $"{remoteObj}#{idObj}");
    }

    public string GetCharacterName(object player)
    {
        ArgumentNullException.ThrowIfNull(player);
        GatewayTypes? types = ResolveTypes(player.GetType().Assembly);
        if (types?.ProgramCharacterName is not null &&
            types.ProgramCharacterName.Invoke(null, [player]) is string name)
        {
            return name;
        }

        Type pt = player.GetType();
        object? ch = pt.GetProperty("Character", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player);
        if (ch?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ch) is string charName)
        {
            return charName;
        }

        return string.Empty;
    }

    public bool IsPlayerClosed(object player)
    {
        ArgumentNullException.ThrowIfNull(player);
        object? closed = player.GetType().GetProperty("Closed", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player);
        return closed is bool b && b;
    }

    public Task SendPacketAsync(object player, ushort opcode, ReadOnlyMemory<byte> payload, string? log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (TryGetClientChannel(player) is { } channel &&
            channel.GetType().GetMethod("SendPacket", BindingFlags.Public | BindingFlags.Instance,
                [typeof(ushort), typeof(ReadOnlyMemory<byte>), typeof(string), typeof(CancellationToken)]) is { } sendPacket)
        {
            return (Task)sendPacket.Invoke(channel, [opcode, payload, log, cancellationToken])!;
        }

        GatewayTypes types = RequireTypes(player.GetType().Assembly);
        if (types.ProgramSend is null)
        {
            throw new InvalidOperationException("Could not resolve global::Client.SendPacket or Program.Send.");
        }

        byte[] frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, opcode);
        payload.Span.CopyTo(frame.AsSpan(2));
        return InvokeWithDefaultArgs<Task>(types.ProgramSend, null, player, frame, log);
    }

    public Task SendChatReplyAsync(object player, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (TryGetClientChannel(player) is { } channel &&
            channel.GetType().GetMethod("SendChat", BindingFlags.Public | BindingFlags.Instance,
                [typeof(string), typeof(CancellationToken)]) is { } sendChat)
        {
            return (Task)sendChat.Invoke(channel, [text, cancellationToken])!;
        }

        GatewayTypes types = RequireTypes(player.GetType().Assembly);
        if (types.ProgramPlayerOf is not null)
        {
            object? modPlayer = types.ProgramPlayerOf.Invoke(null, [player]);
            if (modPlayer is not null &&
                modPlayer.GetType().GetMethod("Tell", BindingFlags.Public | BindingFlags.Instance, [typeof(string)]) is { } tell)
            {
                return (Task)tell.Invoke(modPlayer, [text])!;
            }
        }

        throw new InvalidOperationException("Could not resolve global::Client.SendChat or Program.PlayerOf(player).Tell.");
    }

    public IDisposable SubscribeRuleset(string ownerId, byte rulesetId, Func<object, string, ReadOnlyMemory<byte>, Task<bool>> onInnerPayload)
    {
        GatewayTypes types = RequireTypes();
        MethodInfo method = types.ClientOnRuleset
            ?? throw new InvalidOperationException("global::Client.OnRuleset is not available.");

        Type delegateType = method.GetParameters()[2].ParameterType;
        ParameterExpression chParam = Expression.Parameter(delegateType.GetGenericArguments()[0], "channel");
        ParameterExpression payloadParam = Expression.Parameter(typeof(ReadOnlyMemory<byte>), "payload");

        Func<object, ReadOnlyMemory<byte>, Task<bool>> bridge = (channelObj, payload) =>
        {
            object player = channelObj.GetType().GetProperty("Player", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelObj)
                ?? throw new InvalidOperationException("IClientChannel.Player returned null.");
            string connId = channelObj.GetType().GetProperty("ConnectionId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelObj) as string
                ?? GetConnectionId(player);
            return onInnerPayload(player, connId, payload);
        };

        MethodCallExpression call = Expression.Call(
            Expression.Constant(bridge),
            bridge.GetType().GetMethod("Invoke")!,
            Expression.Convert(chParam, typeof(object)),
            payloadParam);
        Delegate del = Expression.Lambda(delegateType, call, chParam, payloadParam).Compile();
        return (IDisposable)method.Invoke(null, [ownerId, rulesetId, del])!;
    }

    public IDisposable SubscribeClientCommand(string ownerId, Func<object, string, string, Task<bool>> onCommand)
    {
        GatewayTypes types = RequireTypes();
        MethodInfo method = types.ClientOnCommand
            ?? throw new InvalidOperationException("global::Client.OnCommand is not available.");

        Type delegateType = method.GetParameters()[1].ParameterType;
        ParameterExpression chParam = Expression.Parameter(delegateType.GetGenericArguments()[0], "channel");
        ParameterExpression cmdParam = Expression.Parameter(typeof(string), "commandLine");

        Func<object, string, Task<bool>> bridge = (channelObj, line) =>
        {
            object player = channelObj.GetType().GetProperty("Player", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelObj)
                ?? throw new InvalidOperationException("IClientChannel.Player returned null.");
            string connId = channelObj.GetType().GetProperty("ConnectionId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelObj) as string
                ?? GetConnectionId(player);
            return onCommand(player, connId, line);
        };

        MethodCallExpression call = Expression.Call(
            Expression.Constant(bridge),
            bridge.GetType().GetMethod("Invoke")!,
            Expression.Convert(chParam, typeof(object)),
            cmdParam);
        Delegate del = Expression.Lambda(delegateType, call, chParam, cmdParam).Compile();
        return (IDisposable)method.Invoke(null, [ownerId, del])!;
    }

    public IDisposable SubscribePacket(string ownerId, ushort opcode, Func<object, byte[], Task<bool>> onPacket)
    {
        GatewayTypes types = RequireTypes();
        object gameEvent = types.GameEventsPacket.Invoke(null, [opcode])!;
        return AddToGameEvent(gameEvent, ownerId, onPacket);
    }

    public IDisposable SubscribeCommand(string ownerId, string commandName, string usage, Func<object, string[], Task> onCommand)
    {
        GatewayTypes types = RequireTypes();
        MethodInfo cmdMethod = types.GameEventsCommand;
        ParameterInfo[] ps = cmdMethod.GetParameters();
        Type delegateType = ps[3].ParameterType;
        ParameterExpression playerParam = Expression.Parameter(delegateType.GetGenericArguments()[0], "player");
        ParameterExpression argsParam = Expression.Parameter(typeof(string[]), "args");

        MethodCallExpression call = Expression.Call(
            Expression.Constant(onCommand),
            onCommand.GetType().GetMethod("Invoke")!,
            Expression.Convert(playerParam, typeof(object)),
            argsParam);
        Delegate del = Expression.Lambda(delegateType, call, playerParam, argsParam).Compile();

        object?[] invokeArgs = new object?[ps.Length];
        invokeArgs[0] = ownerId;
        invokeArgs[1] = commandName;
        invokeArgs[2] = usage;
        invokeArgs[3] = del;
        if (ps.Length > 4)
        {
            invokeArgs[4] = Array.Empty<string>();
        }

        return (IDisposable)cmdMethod.Invoke(null, invokeArgs)!;
    }

    public IDisposable SubscribePlayerEntered(string ownerId, Func<object, Task> onEntered)
    {
        GatewayTypes types = RequireTypes();
        object gameEvent = types.GameEventsPlayerEntered.GetValue(null)!;
        return AddToGameEvent(gameEvent, ownerId, onEntered);
    }

    public IDisposable SubscribePlayerLeaving(string ownerId, Func<object, string, Task> onLeaving)
    {
        GatewayTypes types = RequireTypes();
        object gameEvent = types.GameEventsPlayerLeaving.GetValue(null)!;
        return AddToGameEvent(gameEvent, ownerId, onLeaving);
    }

    public IDisposable? SubscribeClientReady(string ownerId, Func<object, Task> onReady)
    {
        GatewayTypes types = RequireTypes();
        if (types.GameEventsClientReady?.GetValue(null) is not { } gameEvent)
        {
            return null;
        }

        return AddToGameEvent(gameEvent, ownerId, onReady);
    }

    public IDisposable? SubscribeClientMessage(string ownerId, Func<GatewayClientMessageContext, Task<bool>> onMessage)
    {
        GatewayTypes types = RequireTypes();
        if (types.GameEventsClientMessage?.GetValue(null) is not { } gameEvent)
        {
            return null;
        }

        Func<object, Task<bool>> bridge = msgObj =>
        {
            Type mt = msgObj.GetType();
            object player = mt.GetProperty("Player", BindingFlags.Public | BindingFlags.Instance)!.GetValue(msgObj)!;
            string text = mt.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)?.GetValue(msgObj) as string ?? string.Empty;
            var links = mt.GetProperty("Links", BindingFlags.Public | BindingFlags.Instance)?.GetValue(msgObj) as IReadOnlyList<uint> ?? [];
            bool isCmd = mt.GetProperty("IsCommand", BindingFlags.Public | BindingFlags.Instance)?.GetValue(msgObj) is bool b ? b : text.StartsWith('/');
            var ctx = new GatewayClientMessageContext(GetConnectionId(player), GetCharacterName(player), player, text, links, isCmd);
            return onMessage(ctx);
        };

        return AddToGameEvent(gameEvent, ownerId, bridge);
    }

    public IDisposable? SubscribeCosmeticChanged(string ownerId, Func<GatewayCosmeticChangeContext, Task> onCosmeticChanged)
    {
        GatewayTypes types = RequireTypes();
        if (types.GameEventsCosmeticChanged?.GetValue(null) is not { } gameEvent)
        {
            return null;
        }

        Func<object, Task> bridge = evtObj =>
        {
            Type ct = evtObj.GetType();
            object player = ct.GetProperty("Player", BindingFlags.Public | BindingFlags.Instance)!.GetValue(evtObj)!;
            byte slot = Convert.ToByte(ct.GetProperty("Slot", BindingFlags.Public | BindingFlags.Instance)!.GetValue(evtObj), CultureInfo.InvariantCulture);
            uint recordId = Convert.ToUInt32(ct.GetProperty("RecordId", BindingFlags.Public | BindingFlags.Instance)!.GetValue(evtObj), CultureInfo.InvariantCulture);
            uint mtxHash = Convert.ToUInt32(ct.GetProperty("MtxTypeHash", BindingFlags.Public | BindingFlags.Instance)!.GetValue(evtObj), CultureInfo.InvariantCulture);
            byte option = Convert.ToByte(ct.GetProperty("Option", BindingFlags.Public | BindingFlags.Instance)?.GetValue(evtObj) ?? 0, CultureInfo.InvariantCulture);
            uint gemHash = Convert.ToUInt32(ct.GetProperty("GemHash", BindingFlags.Public | BindingFlags.Instance)?.GetValue(evtObj) ?? 0, CultureInfo.InvariantCulture);
            bool isSkill = ct.GetProperty("IsSkillCosmetic", BindingFlags.Public | BindingFlags.Instance)?.GetValue(evtObj) is bool b ? b : gemHash != 0;
            var ctx = new GatewayCosmeticChangeContext(GetConnectionId(player), GetCharacterName(player), player, slot, recordId, mtxHash, option, gemHash, isSkill);
            return onCosmeticChanged(ctx);
        };

        return AddToGameEvent(gameEvent, ownerId, bridge);
    }

    private static IDisposable AddToGameEvent(object gameEvent, string ownerId, Delegate bridge)
    {
        Type eventType = gameEvent.GetType();
        MethodInfo addMethod = eventType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"GameEvent '{eventType.Name}' does not expose Add.");
        ParameterInfo[] addParams = addMethod.GetParameters();
        Type delegateType = addParams[1].ParameterType;

        ParameterInfo[] targetParams = delegateType.GetMethod("Invoke")!.GetParameters();
        int argCount = targetParams.Length;
        MethodInfo invokeBridge = bridge.GetType().GetMethod("Invoke")!;
        ParameterInfo[] bridgeParams = invokeBridge.GetParameters();

        var exprParams = new ParameterExpression[argCount];
        var callArgs = new Expression[argCount];
        for (int i = 0; i < argCount; i++)
        {
            Type pType = targetParams[i].ParameterType;
            exprParams[i] = Expression.Parameter(pType, $"arg{i}");
            callArgs[i] = pType == bridgeParams[i].ParameterType
                ? exprParams[i]
                : Expression.Convert(exprParams[i], bridgeParams[i].ParameterType);
        }

        MethodCallExpression call = Expression.Call(Expression.Constant(bridge), invokeBridge, callArgs);
        Delegate compiled = Expression.Lambda(delegateType, call, exprParams).Compile();

        object?[] args = new object?[addParams.Length];
        args[0] = ownerId;
        args[1] = compiled;
        for (int i = 2; i < addParams.Length; i++)
        {
            args[i] = addParams[i].HasDefaultValue ? addParams[i].DefaultValue : null;
        }

        return (IDisposable)addMethod.Invoke(gameEvent, args)!;
    }

    private static T InvokeWithDefaultArgs<T>(MethodInfo method, object? instance, params object?[] prefixArgs)
    {
        ParameterInfo[] ps = method.GetParameters();
        object?[] allArgs = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i < prefixArgs.Length)
            {
                allArgs[i] = prefixArgs[i];
            }
            else if (ps[i].HasDefaultValue)
            {
                allArgs[i] = ps[i].DefaultValue;
            }
            else
            {
                allArgs[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            }
        }

        return (T)method.Invoke(instance, allArgs)!;
    }

    private GatewayTypes RequireTypes(Assembly? hintAssembly = null) =>
        ResolveTypes(hintAssembly ?? _defaultHintAssembly) ?? throw new InvalidOperationException("GameServer.Gateway runtime types (GameEvents/Program) are not loaded in the current AppDomain.");

    private GatewayTypes? ResolveTypes(Assembly? hintAssembly = null)
    {
        hintAssembly ??= _defaultHintAssembly;
        if (hintAssembly is not null && _typesByAssembly.TryGetValue(hintAssembly, out GatewayTypes? cachedHint))
        {
            return cachedHint;
        }

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        Assembly? targetAssembly = ResolveTargetGatewayAssembly(assemblies, hintAssembly);
        if (targetAssembly is null)
        {
            return null;
        }

        if (_typesByAssembly.TryGetValue(targetAssembly, out GatewayTypes? cachedTarget))
        {
            return cachedTarget;
        }

        lock (_cacheGate)
        {
            if (_typesByAssembly.TryGetValue(targetAssembly, out cachedTarget))
            {
                return cachedTarget;
            }

            Type? gameEvents = targetAssembly.GetType("GameEvents", throwOnError: false);
            Type? program = targetAssembly.GetType("Program", throwOnError: false);
            Type? client = targetAssembly.GetType("Client", throwOnError: false)
                ?? FindGlobalType(assemblies, targetAssembly, "Client");
            if (gameEvents is null || program is null)
            {
                return null;
            }

            GatewayTypes resolved = new(
                ClientType: client,
                ClientForPlayer: ResolveClientForPlayer(client),
                ClientOnRuleset: client?.GetMethod("OnRuleset", BindingFlags.Public | BindingFlags.Static),
                ClientOnCommand: client?.GetMethod("OnCommand", BindingFlags.Public | BindingFlags.Static),
                ProgramSend: program.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Send" && m.GetParameters().Length >= 3 && m.GetParameters()[1].ParameterType == typeof(byte[])),
                ProgramCharacterName: program.GetMethod("CharacterName", BindingFlags.Public | BindingFlags.Static),
                ProgramPlayerOf: program.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "PlayerOf" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "Player"),
                GameEventsPacket: gameEvents.GetMethod("Packet", BindingFlags.Public | BindingFlags.Static, [typeof(ushort)])!,
                GameEventsCommand: gameEvents.GetMethod("Command", BindingFlags.Public | BindingFlags.Static)!,
                GameEventsPlayerEntered: gameEvents.GetField("PlayerEntered", BindingFlags.Public | BindingFlags.Static)!,
                GameEventsPlayerLeaving: gameEvents.GetField("PlayerLeaving", BindingFlags.Public | BindingFlags.Static)!,
                GameEventsClientReady: gameEvents.GetField("ClientReady", BindingFlags.Public | BindingFlags.Static),
                GameEventsClientMessage: gameEvents.GetField("ClientMessage", BindingFlags.Public | BindingFlags.Static),
                GameEventsCosmeticChanged: gameEvents.GetField("CosmeticChanged", BindingFlags.Public | BindingFlags.Static));

            _typesByAssembly.AddOrUpdate(targetAssembly, resolved);
            return resolved;
        }
    }

    private static Assembly? ResolveTargetGatewayAssembly(Assembly[] assemblies, Assembly? hintAssembly)
    {
        if (hintAssembly is not null &&
            hintAssembly.GetType("GameEvents", throwOnError: false) is not null &&
            hintAssembly.GetType("Program", throwOnError: false) is not null)
        {
            return hintAssembly;
        }

        AssemblyLoadContext? hintAlc = hintAssembly is not null ? AssemblyLoadContext.GetLoadContext(hintAssembly) : null;
        if (hintAlc is not null)
        {
            if (ScanGatewayAssembly(assemblies, hintAlc) is { } fromSameAlc)
            {
                return fromSameAlc;
            }
        }

        return ScanGatewayAssembly(assemblies, filterAlc: null);
    }

    private static Assembly? ScanGatewayAssembly(Assembly[] assemblies, AssemblyLoadContext? filterAlc)
    {
        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            Assembly asm = assemblies[i];
            if (filterAlc is not null && AssemblyLoadContext.GetLoadContext(asm) != filterAlc)
            {
                continue;
            }

            if (string.Equals(asm.GetName().Name, "GameServer.Gateway", StringComparison.OrdinalIgnoreCase) &&
                asm.GetType("GameEvents", throwOnError: false) is not null &&
                asm.GetType("Program", throwOnError: false) is not null)
            {
                return asm;
            }
        }

        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            Assembly asm = assemblies[i];
            if (filterAlc is not null && AssemblyLoadContext.GetLoadContext(asm) != filterAlc)
            {
                continue;
            }

            if (asm.GetType("GameEvents", throwOnError: false) is not null &&
                asm.GetType("Program", throwOnError: false) is not null)
            {
                return asm;
            }
        }

        return null;
    }

    private static MethodInfo? ResolveClientForPlayer(Type? clientType)
    {
        if (clientType is null)
        {
            return null;
        }

        return clientType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "For" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "Player");
    }

    private static Type? FindGlobalType(Assembly[] assemblies, Assembly? hintAssembly, string fullName)
    {
        if (hintAssembly is not null && hintAssembly.GetType(fullName, throwOnError: false) is { } hinted)
        {
            return hinted;
        }

        AssemblyLoadContext? hintAlc = hintAssembly is not null ? AssemblyLoadContext.GetLoadContext(hintAssembly) : null;
        if (hintAlc is not null)
        {
            if (ScanGlobalType(assemblies, fullName, hintAlc) is { } fromSameAlc)
            {
                return fromSameAlc;
            }
        }

        return ScanGlobalType(assemblies, fullName, filterAlc: null);
    }

    private static Type? ScanGlobalType(Assembly[] assemblies, string fullName, AssemblyLoadContext? filterAlc)
    {
        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            Assembly asm = assemblies[i];
            if (filterAlc is not null && AssemblyLoadContext.GetLoadContext(asm) != filterAlc)
            {
                continue;
            }

            if (string.Equals(asm.GetName().Name, "GameServer.Gateway", StringComparison.OrdinalIgnoreCase) &&
                asm.GetType(fullName, throwOnError: false) is { } fromGateway)
            {
                return fromGateway;
            }
        }

        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            Assembly asm = assemblies[i];
            if (filterAlc is not null && AssemblyLoadContext.GetLoadContext(asm) != filterAlc)
            {
                continue;
            }

            if (asm.GetType(fullName, throwOnError: false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed record GatewayTypes(
        Type? ClientType,
        MethodInfo? ClientForPlayer,
        MethodInfo? ClientOnRuleset,
        MethodInfo? ClientOnCommand,
        MethodInfo? ProgramSend,
        MethodInfo? ProgramCharacterName,
        MethodInfo? ProgramPlayerOf,
        MethodInfo GameEventsPacket,
        MethodInfo GameEventsCommand,
        FieldInfo GameEventsPlayerEntered,
        FieldInfo GameEventsPlayerLeaving,
        FieldInfo? GameEventsClientReady,
        FieldInfo? GameEventsClientMessage,
        FieldInfo? GameEventsCosmeticChanged);
}
