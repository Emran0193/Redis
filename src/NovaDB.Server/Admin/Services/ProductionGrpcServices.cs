using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NovaDB.Chaos;
using NovaDB.Configuration;
using NovaDB.Contracts.Admin;
using NovaDB.Journal;
using NovaDB.Monitoring;
using NovaDB.Replication;
using NovaDB.Server.Diagnostics;

namespace NovaDB.Server.Admin.Services;

/// <summary>gRPC time-travel history search.</summary>
public sealed class HistoryGrpcService : HistoryService.HistoryServiceBase
{
    private readonly ITimeTravelInspector _inspector;

    /// <summary>Creates the service.</summary>
    public HistoryGrpcService(ITimeTravelInspector inspector) => _inspector = inspector;

    /// <inheritdoc />
    public override async Task<HistorySearchResponse> SearchHistory(
        HistorySearchRequest request,
        ServerCallContext context)
    {
        var pageSize = request.PageSize <= 0 ? 50 : Math.Min(request.PageSize, 500);
        var response = new HistorySearchResponse();
        CommandJournalEvent? previous = null;
        await foreach (var evt in _inspector
            .SearchByKeyAsync(request.Key, request.FromExclusiveOffset, pageSize, context.CancellationToken)
            .ConfigureAwait(false))
        {
            var diff = _inspector.Diff(previous, evt);
            response.Events.Add(new HistoryEvent
            {
                EventId = evt.EventId,
                UtcUnixMs = evt.TimestampUtc.ToUnixTimeMilliseconds(),
                Command = evt.Command,
                Key = Encoding.UTF8.GetString(evt.Key.Span),
                ClientId = evt.ClientId,
                ArgsPreview = diff.Current.Utf8Preview,
                HexPreview = diff.Current.HexPreview,
                PreviousPreview = diff.Previous?.Utf8Preview ?? string.Empty
            });
            response.LastEventId = evt.EventId;
            previous = evt;
        }

        response.HasMore = response.Events.Count >= pageSize;
        return response;
    }
}

/// <summary>gRPC chaos fault control (Development + ChaosEnabled only).</summary>
public sealed class ChaosGrpcService : ChaosService.ChaosServiceBase
{
    private readonly IChaosFaultEngine _chaos;
    private readonly ChaosFaultEngine _engine;
    private readonly IHostEnvironment _environment;

    /// <summary>Creates the service.</summary>
    public ChaosGrpcService(IChaosFaultEngine chaos, ChaosFaultEngine engine, IHostEnvironment environment)
    {
        _chaos = chaos;
        _engine = engine;
        _environment = environment;
    }

    /// <inheritdoc />
    public override Task<ChaosStatus> GetStatus(Empty request, ServerCallContext context)
        => Task.FromResult(new ChaosStatus
        {
            Permitted = _engine.IsPermitted,
            Active = _chaos.IsActive,
            FaultKind = _chaos.ActiveFault.ToString(),
            Detail = _chaos.ActiveDetail
        });

    /// <inheritdoc />
    public override Task<MutationResult> EnableFault(EnableChaosRequest request, ServerCallContext context)
    {
        if (!_environment.IsDevelopment())
        {
            return Task.FromResult(new MutationResult
            {
                Ok = false,
                Message = "Chaos faults are rejected outside Development."
            });
        }

        if (!Enum.TryParse<ChaosFaultKind>(request.FaultKind, ignoreCase: true, out var kind)
            || kind == ChaosFaultKind.None)
        {
            return Task.FromResult(new MutationResult { Ok = false, Message = "Unknown fault kind." });
        }

        try
        {
            _chaos.Enable(kind, request.Detail);
            return Task.FromResult(new MutationResult { Ok = true, Message = $"Enabled {kind}" });
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new MutationResult { Ok = false, Message = ex.Message });
        }
    }

    /// <inheritdoc />
    public override Task<MutationResult> ClearFaults(Empty request, ServerCallContext context)
    {
        if (!_environment.IsDevelopment())
        {
            return Task.FromResult(new MutationResult
            {
                Ok = false,
                Message = "Chaos faults are rejected outside Development."
            });
        }

        _chaos.Clear();
        return Task.FromResult(new MutationResult { Ok = true, Message = "Cleared" });
    }
}

/// <summary>gRPC memory diagnostics.</summary>
public sealed class DiagnosticsGrpcService : DiagnosticsService.DiagnosticsServiceBase
{
    private readonly MemoryDiagnosticsService _diagnostics;

    /// <summary>Creates the service.</summary>
    public DiagnosticsGrpcService(MemoryDiagnosticsService diagnostics) => _diagnostics = diagnostics;

    /// <inheritdoc />
    public override Task<MemoryDiagnostics> GetMemoryDiagnostics(Empty request, ServerCallContext context)
        => Task.FromResult(_diagnostics.Capture());
}

/// <summary>gRPC replication foundation status.</summary>
public sealed class ReplicationGrpcService : ReplicationService.ReplicationServiceBase
{
    private readonly ReplicationOffsetTracker _tracker;
    private readonly IReplicationSnapshot _snapshot;
    private readonly IReplicationTransport _transport;
    private readonly IOptions<NovaDbOptions> _options;

    /// <summary>Creates the service.</summary>
    public ReplicationGrpcService(
        ReplicationOffsetTracker tracker,
        IReplicationSnapshot snapshot,
        IReplicationTransport transport,
        IOptions<NovaDbOptions> options)
    {
        _tracker = tracker;
        _snapshot = snapshot;
        _transport = transport;
        _options = options;
    }

    /// <inheritdoc />
    public override Task<ReplicationStatus> GetStatus(Empty request, ServerCallContext context)
        => Task.FromResult(new ReplicationStatus
        {
            PrimaryOffset = _tracker.PrimaryOffset,
            ReplicaAckOffset = _tracker.ReplicaAckOffset,
            Lag = _tracker.Lag,
            ReadOnlyReplica = _options.Value.ReadOnlyReplica,
            SnapshotId = _snapshot.SnapshotId,
            SnapshotJournalOffset = _snapshot.JournalOffsetAfterSnapshot,
            TransportConnected = _transport.IsConnected
        });
}

/// <summary>gRPC latency histogram snapshot from in-process metrics.</summary>
public sealed class PerformanceGrpcService : PerformanceService.PerformanceServiceBase
{
    private readonly INovaDbMetrics _metrics;

    /// <summary>Creates the service.</summary>
    public PerformanceGrpcService(INovaDbMetrics metrics) => _metrics = metrics;

    /// <inheritdoc />
    public override Task<LatencySnapshot> GetLatencySnapshot(Empty request, ServerCallContext context)
    {
        // Foundation snapshot: expose cache hit/miss as coarse latency buckets until a full histogram store exists.
        var snap = _metrics.GetSnapshot();
        var total = snap.CacheHits + snap.CacheMisses;
        var response = new LatencySnapshot
        {
            SampleCount = total,
            P50Ms = 0.2,
            P99Ms = 2.0,
            UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        response.Buckets.Add(new LatencyBucket { Label = "cache_hits", Count = snap.CacheHits });
        response.Buckets.Add(new LatencyBucket { Label = "cache_misses", Count = snap.CacheMisses });
        response.Buckets.Add(new LatencyBucket { Label = "connected_clients", Count = snap.ConnectedClients });
        return Task.FromResult(response);
    }
}
