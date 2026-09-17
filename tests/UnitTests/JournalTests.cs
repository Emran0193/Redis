using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Journal;

namespace UnitTests;

/// <summary>Unit coverage for the durable command journal and time-travel inspector.</summary>
public sealed class JournalTests
{
    [Fact]
    public async Task FileCommandJournal_AppendAndRead_RoundTripsEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "novadb-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = Options.Create(new NovaDbOptions
            {
                DataDirectory = dir,
                JournalEnabled = true,
                JournalFlushOnAppend = true
            });
            await using var journal = new FileCommandJournal(options, NullLogger<FileCommandJournal>.Instance);
            await journal.StartAsync(CancellationToken.None);

            var key = "user:1"u8.ToArray();
            var appended = await journal.AppendAsync(
                "c1",
                string.Empty,
                "SET",
                key,
                [key, "v1"u8.ToArray()],
                version: 1,
                dedupeKey: null,
                CancellationToken.None);

            appended.EventId.Should().Be(1UL);
            journal.CurrentOffset.Should().Be(1UL);

            var events = new List<CommandJournalEvent>();
            await foreach (var evt in journal.ReadAsync(0, keyFilter: null, commandFilter: null, pageSize: 10, CancellationToken.None))
            {
                events.Add(evt);
            }

            events.Should().ContainSingle();
            events[0].Command.Should().Be("SET");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task TimeTravelInspector_SearchByKey_RespectsPageSize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "novadb-tt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = Options.Create(new NovaDbOptions { DataDirectory = dir, JournalEnabled = true, JournalFlushOnAppend = true });
            await using var journal = new FileCommandJournal(options, NullLogger<FileCommandJournal>.Instance);
            await journal.StartAsync(CancellationToken.None);
            var key = "k"u8.ToArray();
            for (var i = 0; i < 5; i++)
            {
                await journal.AppendAsync("c", "", "SET", key, [key, new byte[] { (byte)('0' + i) }], 0, null, CancellationToken.None);
            }

            var inspector = new TimeTravelInspector(journal);
            var page = new List<CommandJournalEvent>();
            await foreach (var evt in inspector.SearchByKeyAsync("k", 0, pageSize: 2, CancellationToken.None))
            {
                page.Add(evt);
            }

            page.Should().HaveCount(2);
            var diff = inspector.Diff(page[0], page[1]);
            diff.Current.HexPreview.Should().NotBeNullOrEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
