using Grpc.Core;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Contracts.Admin;
using NovaDB.Core.Hosting;
using NovaDB.Core.Models;
using NovaDB.Monitoring;
using NovaDB.Networking;
using NovaDB.Persistence.Aof;
using NovaDB.Persistence.Snapshot;
using NovaDB.PubSub;
using NovaDB.Storage;
using NovaDB.Storage.Eviction;
using NovaDB.Storage.Values;
using System.Text;

namespace NovaDB.Server.Admin.Services;

/// <summary>gRPC metrics snapshot for the Admin UI.</summary>
public sealed class MetricsGrpcService : MetricsService.MetricsServiceBase
{
    private readonly INovaDbMetrics _metrics;
    private readonly IStorageEngine _storage;
    private readonly MemoryStorageEngine _memory;

    /// <summary>Creates the service.</summary>
    public MetricsGrpcService(INovaDbMetrics metrics, IStorageEngine storage, MemoryStorageEngine memory)
    {
        _metrics = metrics;
        _storage = storage;
        _memory = memory;
    }

    /// <inheritdoc />
    public override Task<MetricsSnapshot> GetSnapshot(Empty request, ServerCallContext context)
    {
        var snap = _metrics.GetSnapshot();
        var total = snap.CacheHits + snap.CacheMisses;
        var hitRatio = total == 0 ? 0 : (double)snap.CacheHits / total;
        return Task.FromResult(new MetricsSnapshot
        {
            ConnectedClients = snap.ConnectedClients,
            MemoryBytes = snap.MemoryBytes > 0 ? snap.MemoryBytes : _storage.EstimatedMemoryBytes,
            MemoryLimitBytes = _memory.MemoryLimitBytes,
            CacheHits = snap.CacheHits,
            CacheMisses = snap.CacheMisses,
            Evictions = snap.Evictions,
            ExpiredKeys = snap.ExpiredKeys,
            KeyCount = _storage.KeyCount,
            HitRatio = hitRatio,
            UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }
}

/// <summary>gRPC key browsing and mutation.</summary>
public sealed class KeyGrpcService : KeyService.KeyServiceBase
{
    private readonly IStorageEngine _storage;

    /// <summary>Creates the service.</summary>
    public KeyGrpcService(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public override async Task<ScanKeysResponse> ScanKeys(ScanKeysRequest request, ServerCallContext context)
    {
        var count = request.Count <= 0 ? 50 : Math.Min(request.Count, 500);
        var (next, keys) = await _storage.ScanKeysAsync(request.Cursor, count, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new ScanKeysResponse { NextCursor = next };
        foreach (var key in keys)
        {
            var text = Encoding.UTF8.GetString(key.Bytes);
            if (!string.IsNullOrEmpty(request.Prefix)
                && !text.StartsWith(request.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            var typeName = entry.Type.ToString();
            if (!string.IsNullOrEmpty(request.TypeFilter)
                && !string.Equals(typeName, request.TypeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ttl = entry.ExpireAtUnixMs is long exp
                ? Math.Max(0, exp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                : -1;
            response.Keys.Add(new KeySummary
            {
                Key = text,
                Type = typeName,
                TtlMs = ttl,
                EstimatedBytes = EntryMemoryEstimator.Estimate(key, entry)
            });
        }

        return response;
    }

    /// <inheritdoc />
    public override async Task<KeyDetails> GetKey(KeyRequest request, ServerCallContext context)
    {
        var key = RedisKey.FromString(request.Key);
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new KeyDetails { Key = request.Key, Found = false };
        }

        var ttl = entry.ExpireAtUnixMs is long exp
            ? Math.Max(0, exp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : -1;
        var details = new KeyDetails
        {
            Key = request.Key,
            Found = true,
            Type = entry.Type.ToString(),
            TtlMs = ttl,
            EstimatedBytes = EntryMemoryEstimator.Estimate(key, entry)
        };

        if (entry.Value is StringValue sv)
        {
            details.StringValue = Encoding.UTF8.GetString(sv.Bytes);
        }

        return details;
    }

    /// <inheritdoc />
    public override async Task<MutationResult> SetString(SetStringRequest request, ServerCallContext context)
    {
        var key = RedisKey.FromString(request.Key);
        long? expire = request.TtlMs > 0
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + request.TtlMs
            : null;
        await _storage.SetAsync(
            key,
            new DatabaseEntry(RedisValueType.String, new StringValue(Encoding.UTF8.GetBytes(request.Value)), expire),
            context.CancellationToken).ConfigureAwait(false);
        return new MutationResult { Ok = true, Affected = 1, Message = "OK" };
    }

    /// <inheritdoc />
    public override async Task<MutationResult> DeleteKey(KeyRequest request, ServerCallContext context)
    {
        var deleted = await _storage.DeleteAsync(RedisKey.FromString(request.Key), context.CancellationToken)
            .ConfigureAwait(false);
        return new MutationResult { Ok = deleted, Affected = deleted ? 1 : 0, Message = deleted ? "deleted" : "missing" };
    }

    /// <inheritdoc />
    public override async Task<MutationResult> RenameKey(RenameKeyRequest request, ServerCallContext context)
    {
        var source = RedisKey.FromString(request.Key);
        var dest = RedisKey.FromString(request.NewKey);
        var entry = await _storage.GetAsync(source, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new MutationResult { Ok = false, Message = "source missing" };
        }

        await _storage.SetAsync(dest, entry, context.CancellationToken).ConfigureAwait(false);
        await _storage.DeleteAsync(source, context.CancellationToken).ConfigureAwait(false);
        return new MutationResult { Ok = true, Affected = 1, Message = "renamed" };
    }
}

/// <summary>gRPC client connection directory.</summary>
public sealed class ClientGrpcService : ClientService.ClientServiceBase
{
    private readonly ConnectionManager _connections;

    /// <summary>Creates the service.</summary>
    public ClientGrpcService(ConnectionManager connections) => _connections = connections;

    /// <inheritdoc />
    public override Task<ClientList> ListClients(Empty request, ServerCallContext context)
    {
        var list = new ClientList();
        foreach (var c in _connections.GetConnections())
        {
            list.Clients.Add(new ClientInfo
            {
                ConnectionId = c.ConnectionId,
                RemoteAddress = c.RemoteAddress,
                Authenticated = c.IsAuthenticated,
                Subscribed = c.IsSubscribed
            });
        }

        return Task.FromResult(list);
    }

    /// <inheritdoc />
    public override Task<MutationResult> KillClient(KillClientRequest request, ServerCallContext context)
    {
        var match = _connections.GetConnections()
            .FirstOrDefault(c => string.Equals(c.ConnectionId, request.ConnectionId, StringComparison.Ordinal));
        if (match is null)
        {
            return Task.FromResult(new MutationResult { Ok = false, Message = "not found" });
        }

        match.RequestClose();
        return Task.FromResult(new MutationResult { Ok = true, Affected = 1, Message = "closing" });
    }
}

/// <summary>gRPC live configuration.</summary>
public sealed class ConfigGrpcService : ConfigService.ConfigServiceBase
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly MemoryStorageEngine _storage;
    private readonly ConfigurableEvictionPolicy _eviction;
    private readonly ILogger<ConfigGrpcService> _logger;

    /// <summary>Creates the service.</summary>
    public ConfigGrpcService(
        IOptions<NovaDbOptions> options,
        MemoryStorageEngine storage,
        ConfigurableEvictionPolicy eviction,
        ILogger<ConfigGrpcService> logger)
    {
        _options = options;
        _storage = storage;
        _eviction = eviction;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task<ConfigSnapshot> GetConfig(Empty request, ServerCallContext context)
    {
        var o = _options.Value;
        return Task.FromResult(new ConfigSnapshot
        {
            MemoryLimitBytes = _storage.MemoryLimitBytes,
            EvictionPolicy = _eviction.PolicyName,
            AofEnabled = o.AofEnabled,
            AofFlushPolicy = o.AofFlushPolicy,
            MaxConnections = o.MaxConnections,
            IdleTimeout = o.IdleTimeout.ToString(),
            ShardCount = o.ShardCount
        });
    }

    /// <inheritdoc />
    public override Task<MutationResult> SetConfig(SetConfigRequest request, ServerCallContext context)
    {
        var key = request.Key.Trim().ToLowerInvariant();
        var value = request.Value;
        switch (key)
        {
            case "maxmemory":
            case "memorylimitbytes":
                if (!long.TryParse(value, out var limit) || limit < 0)
                {
                    return Task.FromResult(new MutationResult { Ok = false, Message = "invalid maxmemory" });
                }

                _storage.SetMemoryLimitBytes(limit);
                _options.Value.MemoryLimitBytes = limit;
                break;
            case "maxmemory-policy":
            case "evictionpolicy":
                if (!EvictionPolicyFactory.IsKnown(value))
                {
                    return Task.FromResult(new MutationResult { Ok = false, Message = "unknown policy" });
                }

                _eviction.Replace(value);
                _options.Value.EvictionPolicy = value;
                break;
            default:
                return Task.FromResult(new MutationResult { Ok = false, Message = "unsupported key" });
        }

        _logger.LogInformation("Config changed by {Actor}: {Key}={Value}", request.Actor, key, value);
        return Task.FromResult(new MutationResult { Ok = true, Message = "OK", Affected = 1 });
    }
}

/// <summary>gRPC health projection.</summary>
public sealed class HealthGrpcService : HealthService.HealthServiceBase
{
    private readonly IStorageEngine _storage;
    private readonly IDatabaseReadiness _readiness;

    /// <summary>Creates the service.</summary>
    public HealthGrpcService(IStorageEngine storage, IDatabaseReadiness readiness)
    {
        _storage = storage;
        _readiness = readiness;
    }

    /// <inheritdoc />
    public override Task<HealthSnapshot> GetHealth(Empty request, ServerCallContext context)
    {
        var ready = _readiness.IsReady;
        return Task.FromResult(new HealthSnapshot
        {
            Ready = ready,
            Status = ready ? "Healthy" : "Unhealthy",
            Description = ready
                ? $"NovaDB is ready. Keys={_storage.KeyCount}, MemoryBytes={_storage.EstimatedMemoryBytes}"
                : "Recovery incomplete",
            KeyCount = _storage.KeyCount,
            MemoryBytes = _storage.EstimatedMemoryBytes
        });
    }
}

/// <summary>gRPC persistence status and triggers.</summary>
public sealed class PersistenceGrpcService : PersistenceService.PersistenceServiceBase
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly IStorageEngine _storage;
    private readonly SnapshotWriter _snapshotWriter;
    private readonly IAofLog? _aofLog;

    /// <summary>Creates the service.</summary>
    public PersistenceGrpcService(
        IOptions<NovaDbOptions> options,
        IStorageEngine storage,
        SnapshotWriter snapshotWriter,
        IServiceProvider services)
    {
        _options = options;
        _storage = storage;
        _snapshotWriter = snapshotWriter;
        _aofLog = services.GetService<IAofLog>();
    }

    /// <inheritdoc />
    public override Task<PersistenceStatus> GetStatus(Empty request, ServerCallContext context)
    {
        var dir = _options.Value.DataDirectory;
        var snapshotPath = Path.Combine(dir, "snapshot.novdb");
        var aofPath = Path.Combine(dir, "appendonly.aof");
        return Task.FromResult(new PersistenceStatus
        {
            AofEnabled = _options.Value.AofEnabled,
            AofFlushPolicy = _options.Value.AofFlushPolicy,
            DataDirectory = dir,
            AofSizeBytes = File.Exists(aofPath) ? new FileInfo(aofPath).Length : 0,
            SnapshotExists = File.Exists(snapshotPath),
            LastSnapshotPath = File.Exists(snapshotPath) ? snapshotPath : string.Empty,
            AofRewriteInProgress = false
        });
    }

    /// <inheritdoc />
    public override async Task<MutationResult> TriggerSnapshot(Empty request, ServerCallContext context)
    {
        await _snapshotWriter.WriteAsync(_storage, context.CancellationToken).ConfigureAwait(false);
        return new MutationResult { Ok = true, Message = "snapshot written", Affected = 1 };
    }

    /// <inheritdoc />
    public override Task<MutationResult> TriggerAofRewrite(Empty request, ServerCallContext context)
    {
        if (!_options.Value.AofEnabled || _aofLog is null)
        {
            return Task.FromResult(new MutationResult { Ok = false, Message = "AOF disabled" });
        }

        if (!_aofLog.TryScheduleBackgroundRewrite(_storage, context.CancellationToken))
        {
            return Task.FromResult(new MutationResult { Ok = false, Message = "rewrite already in progress" });
        }

        return Task.FromResult(new MutationResult { Ok = true, Message = "rewrite scheduled", Affected = 1 });
    }
}

/// <summary>gRPC pub/sub ops.</summary>
public sealed class PubSubGrpcService : PubSubService.PubSubServiceBase
{
    private readonly IPubSubHub _hub;

    /// <summary>Creates the service.</summary>
    public PubSubGrpcService(IPubSubHub hub) => _hub = hub;

    /// <inheritdoc />
    public override Task<PubSubStats> GetStats(Empty request, ServerCallContext context)
        => Task.FromResult(new PubSubStats
        {
            TrackedChannels = _hub.TrackedChannelCount,
            Note = "Phase-1 channel count only"
        });

    /// <inheritdoc />
    public override async Task<MutationResult> Publish(PublishRequest request, ServerCallContext context)
    {
        var receivers = await _hub.PublishAsync(
            request.Channel,
            Encoding.UTF8.GetBytes(request.Message),
            context.CancellationToken).ConfigureAwait(false);
        return new MutationResult { Ok = true, Affected = receivers, Message = $"delivered to {receivers}" };
    }
}
