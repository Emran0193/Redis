using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Journal;
using NovaDB.Replication;

namespace UnitTests;

/// <summary>Unit coverage for replication foundation types.</summary>
public sealed class ReplicationFoundationTests
{
    [Fact]
    public void ReplicaSession_AcknowledgeAndRotateToken_Works()
    {
        var session = new ReplicaSession("r1", reconnectToken: "token-a", lastAckOffset: 3);
        session.LastAckOffset.Should().Be(3UL);
        session.Acknowledge(10);
        session.LastAckOffset.Should().Be(10UL);
        session.Acknowledge(8);
        session.LastAckOffset.Should().Be(10UL);
        var rotated = session.RotateReconnectToken();
        rotated.Should().NotBe("token-a");
        session.ReconnectToken.Should().Be(rotated);
    }

    [Fact]
    public void FlowControlWindow_CreditsGateInFlightBytes()
    {
        var window = new FlowControlWindow(100);
        window.TryAcquire(60).Should().BeTrue();
        window.TryAcquire(50).Should().BeFalse();
        window.InFlightBytes.Should().Be(60);
        window.Release(60);
        window.TryAcquire(50).Should().BeTrue();
    }

    [Fact]
    public async Task InMemoryCommandStreamer_StreamsWithBackpressureChannel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "novadb-repl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = Options.Create(new NovaDbOptions { DataDirectory = dir, JournalEnabled = true, JournalFlushOnAppend = true });
            await using var journal = new FileCommandJournal(options, NullLogger<FileCommandJournal>.Instance);
            await journal.StartAsync(CancellationToken.None);
            var key = "a"u8.ToArray();
            await journal.AppendAsync("c", "", "SET", key, [key, "1"u8.ToArray()], 0, null, CancellationToken.None);
            await journal.AppendAsync("c", "", "SET", key, [key, "2"u8.ToArray()], 0, null, CancellationToken.None);

            var streamer = new InMemoryCommandStreamer(journal, channelCapacity: 1);
            var count = 0;
            await foreach (var _ in streamer.StreamAsync(0, CancellationToken.None))
            {
                count++;
            }

            count.Should().Be(2);

            var tracker = new ReplicationOffsetTracker(journal);
            tracker.PrimaryOffset.Should().Be(2UL);
            tracker.SetReplicaAck(1);
            tracker.Lag.Should().Be(1UL);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SnapshotHandshake_ExposesOffset()
    {
        var snap = SnapshotHandshake.FromJournalOffset(42, "snap-1");
        snap.SnapshotId.Should().Be("snap-1");
        snap.JournalOffsetAfterSnapshot.Should().Be(42UL);
    }
}
