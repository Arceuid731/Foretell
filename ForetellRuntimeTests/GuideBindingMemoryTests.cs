using BossMod.Foretell;
using System.Text.Json;

internal static class GuideBindingMemoryTests
{
    public static void Run()
    {
        InMemory();
        RoundTrip().GetAwaiter().GetResult();
        Bounds().GetAwaiter().GetResult();
        Corruption().GetAwaiter().GetResult();
        ConcurrentLoadAndRemember().GetAwaiter().GetResult();
        FlushAndDisposal().GetAwaiter().GetResult();
        AutomaticPersistence().GetAwaiter().GetResult();
        DisposalWithoutContext();
        Console.WriteLine("Guide binding memory: roundtrip, validation, bounds, corruption, load races and flush/disposal passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static GuideBindingRecord Record(int index = 1) => new(index.ToString("X64"), new string('A', 64), "Hammer", "cast",
        (uint)index, "Hammer", 42, 43, 1, new DateTime(2026, 1, 1).AddSeconds(index));

    private static async Task Ready(GuideBindingMemory memory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!memory.Ready) await Task.Delay(10, timeout.Token);
    }

    private static void InMemory()
    {
        using var memory = new GuideBindingMemory();
        var value = Record();
        Check(memory.Ready && memory.Entries.Length == 0, "In-memory construction was not immediately ready.");
        Check(memory.Remember(value) && memory.Remember(value), "Caller-supplied occurrences were ignored.");
        Check(memory.Entries.Single().Observations == 2, "Occurrences created duplicate keys or lost their count.");
        Check(memory.Remember(value with { Observations = 2 }) && memory.Entries.Single().Observations == 4, "Caller-supplied occurrence delta was ignored.");
        Check(memory.Remember(value with { Observations = int.MaxValue }), "High observation count was rejected.");
        Check(memory.Entries.Single().Observations == GuideBindingMemory.ObservationLimit, "Observation count was not clamped.");
        Check(!memory.Remember(value) && memory.Entries.Single().Observations == GuideBindingMemory.ObservationLimit, "Saturated unchanged record overflowed or reported a change.");
        Check(memory.Remember(Record(2) with { Observations = int.MinValue }) && memory.Entries.Single(entry => entry.ID == 2).Observations == 1,
            "Negative observation count was not clamped.");
        var snapshot = memory.Entries;
        snapshot[0] = Record(100);
        Check(memory.Entries.All(entry => entry.ID != 100), "Snapshot mutation changed memory.");
        foreach (var invalid in new[]
        {
            value with { Key = new string('a', 64) }, value with { Key = new string('G', 64) }, value with { Key = "short" },
            value with { Scope = "" }, value with { Scope = new string('b', 64) }, value with { ID = 0 },
            value with { SourceOID = 0 }, value with { SourceNameID = 0 }, value with { Kind = "Cast" },
            value with { Name = new string('n', 121) }, value with { Mechanic = new string('m', 121) },
            value with { Name = "\n" }, value with { Name = null! }, value with { Mechanic = "" },
            value with { LastSeen = default }, value with { LastSeen = DateTime.MaxValue }
        }) Check(!memory.Remember(invalid), "Invalid binding was accepted: " + invalid);
        Check(!memory.Remember(null!), "Null binding was accepted.");
        Check(memory.Remember(value with { Scope = new string('B', 64), Observations = 1 }), "Changed scope could not replace a key.");
        Check(memory.Entries.Single(entry => entry.Key == value.Key).Observations == 1, "Changed fingerprints inherited prior observations.");
        Check(memory.FlushAsync().IsCompletedSuccessfully, "In-memory flush required background work.");
    }

    private static async Task RoundTrip()
    {
        using var directory = new TestDirectory();
        GuideBindingRecord[] expected;
        using (var memory = new GuideBindingMemory(directory.File))
        {
            await Ready(memory);
            Check(memory.Error.Length == 0, "Missing file produced a diagnostic.");
            Check(memory.Remember(Record() with { Name = "Écrasement" }), "Cast was rejected.");
            Check(memory.Remember(Record(2) with { Kind = "status", LastSeen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local) }),
                "Local DateTime or status was rejected.");
            Check(memory.Remember(Record(3) with { LastSeen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }), "UTC DateTime was rejected.");
            expected = memory.Entries;
            await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.Exists(directory.File) && memory.Error.Length == 0, "Flush did not persist the snapshot.");
        }
        using var restored = new GuideBindingMemory(directory.File);
        await Ready(restored);
        Check(expected.SequenceEqual(restored.Entries), "Roundtrip altered bindings or DateTimes.");
        Check(restored.Error.Length == 0 && Directory.GetFiles(directory.Path).Length == 1, "Roundtrip left an error or temporary file.");
    }

    private static async Task Bounds()
    {
        using var directory = new TestDirectory();
        using (var memory = new GuideBindingMemory(directory.File))
        {
            for (var index = 1; index <= GuideBindingMemory.EntryLimit + 32; ++index)
                memory.Remember(Record(index) with { Name = new string('\uFFFF', 120), Mechanic = new string('\uFFFF', 120) });
            Check(memory.Entries.Length == GuideBindingMemory.EntryLimit && memory.Entries.All(entry => entry.ID > 32), "Oldest entries were not pruned.");
            Check(!memory.Remember(Record()), "Immediately pruned entry reported a change.");
            await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(memory.Error.Length == 0 && new FileInfo(directory.File).Length <= GuideBindingMemory.FileSizeLimit,
                "Maximum escaped text exceeded the file bound.");
        }
        using (var restored = new GuideBindingMemory(directory.File))
        {
            await Ready(restored);
            Check(restored.Entries.Length == GuideBindingMemory.EntryLimit && restored.Entries.All(entry => entry.ID > 32), "Pruned entries returned on reload.");
        }
        File.WriteAllText(directory.File, JsonSerializer.Serialize(new { Schema = 1, Entries = Enumerable.Range(1, 1056).Select(index => Record(index)).ToArray() }));
        using var oversized = new GuideBindingMemory(directory.File);
        await Ready(oversized);
        Check(oversized.Entries.Length == GuideBindingMemory.EntryLimit && oversized.Entries.All(entry => entry.ID > 32), "Loaded entries exceeded the memory bound.");
    }

    private static async Task Corruption()
    {
        using var directory = new TestDirectory();
        var valid = JsonSerializer.Serialize(new { Schema = 1, Entries = new[] { Record() } });
        foreach (var content in new[]
        {
            "{broken", "null", "{}", "{\"Schema\":999,\"Entries\":[]}", "{\"Schema\":\"1\",\"Entries\":[]}",
            "{\"Schema\":1,\"Entries\":[null]}", valid.Replace("2026-01-01T00:00:01", "invalid-date"),
            JsonSerializer.Serialize(new { Schema = 1, Entries = new[] { Record(), Record(2) with { SourceNameID = 0 } } }),
            JsonSerializer.Serialize(new { Schema = 1, Entries = new[] { Record(), Record() } }),
            new string(' ', GuideBindingMemory.FileSizeLimit + 1)
        })
        {
            File.WriteAllText(directory.File, content);
            using var memory = new GuideBindingMemory(directory.File);
            await Ready(memory);
            Check(memory.Entries.Length == 0 && memory.Error.Length is > 0 and <= 512, "Corruption was accepted or lost its bounded diagnostic.");
            await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.ReadAllText(directory.File) == content, "Read-only flush overwrote an invalid file.");
            Check(memory.Remember(Record(3)), "Corrupt storage disabled live observations.");
            await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(memory.Error.Length > 0, "Successful write erased the retained load diagnostic.");
            using var restored = new GuideBindingMemory(directory.File);
            await Ready(restored);
            Check(restored.Entries.SequenceEqual(new[] { Record(3) }), "Recovery lost live observations.");
        }
        using var invalidPath = new GuideBindingMemory(System.IO.Path.Combine(directory.Path, new string('x', 3000)));
        await Ready(invalidPath);
        Check(invalidPath.Error.Length is > 0 and <= 512, "Long path failure exceeded the diagnostic limit.");
        invalidPath.Remember(Record());
        await invalidPath.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(invalidPath.Error.Length is > 0 and <= 512, "Write failure exceeded the diagnostic limit.");
    }

    private static async Task ConcurrentLoadAndRemember()
    {
        using var directory = new TestDirectory();
        for (var attempt = 0; attempt < 16; ++attempt)
        {
            var seed = Enumerable.Range(1, 128).Select(index => Record(index) with { Observations = 10 }).ToArray();
            File.WriteAllText(directory.File, JsonSerializer.Serialize(new { Schema = 1, Entries = seed }));
            using (var memory = new GuideBindingMemory(directory.File))
            {
                memory.Remember(Record());
                memory.Remember(Record(2) with { Scope = new string('B', 64) });
                await Task.WhenAll(Enumerable.Range(129, 128).Select(index => Task.Run(() =>
                {
                    Check(memory.Remember(Record(index)), "Concurrent observation was rejected.");
                })));
                await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(memory.Ready && memory.Entries.Length == 256, "Load overwrote concurrent observations or lost persisted entries.");
                Check(memory.Entries.Single(entry => entry.ID == 1).Observations == 11, "Load inflated or discarded caller counts.");
                Check(memory.Entries.Single(entry => entry.ID == 2) == (Record(2) with { Scope = new string('B', 64) }), "Load restored obsolete scope data.");
                Check(memory.Entries.Where(entry => entry.ID is > 2 and <= 128).All(entry => entry.Observations == 10), "Load lost untouched counts.");
            }
            using var restored = new GuideBindingMemory(directory.File);
            await Ready(restored);
            Check(restored.Entries.Length == 256, "Concurrent observations did not survive flush/reload.");
        }
    }

    private static async Task FlushAndDisposal()
    {
        using var directory = new TestDirectory();
        var memory = new GuideBindingMemory(directory.File);
        memory.Remember(Record());
        var pending = Enumerable.Range(0, 100).Select(_ => memory.FlushAsync()).ToArray();
        memory.Remember(Record(2));
        await Task.Run(memory.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        memory.Dispose();
        Check(!memory.Remember(Record(3)), "Disposed memory accepted an observation.");
        Check(Directory.GetFiles(directory.Path).Length == 1, "Disposal left temporary files.");
        using (var restored = new GuideBindingMemory(directory.File))
        {
            await Ready(restored);
            Check(restored.Entries.Length == 2, "Disposal lost observations accepted after a flush request.");
        }

        var parent = System.IO.Path.Combine(directory.Path, "blocked");
        File.WriteAllText(parent, "not a directory");
        var target = System.IO.Path.Combine(parent, "bindings.json");
        using var failed = new GuideBindingMemory(target);
        failed.Remember(Record());
        await failed.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(failed.Error.Length is > 0 and <= 512 && failed.Entries.Length == 1, "Write failure lost observations or bounded diagnostics.");
        File.Delete(parent);
        await failed.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(File.Exists(target), "Explicit flush could not retry failed persistence.");
        using var recovered = new GuideBindingMemory(target);
        await Ready(recovered);
        Check(recovered.Entries.SequenceEqual(new[] { Record() }), "Retry persisted the wrong snapshot.");
    }

    private static async Task AutomaticPersistence()
    {
        using var directory = new TestDirectory();
        using var memory = new GuideBindingMemory(directory.File);
        memory.Remember(Record());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(directory.File)) await Task.Delay(10, timeout.Token);
        using (var restored = new GuideBindingMemory(directory.File))
        {
            await Ready(restored);
            Check(restored.Entries.SequenceEqual(new[] { Record() }), "Debounced persistence required an explicit flush.");
        }
        for (var index = 2; index < 10; ++index)
        {
            memory.Remember(Record(index));
            await memory.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var restored = new GuideBindingMemory(directory.File);
            await Ready(restored);
            Check(restored.Error.Length == 0 && restored.Entries.Length == index, "Atomic replacement produced an incomplete snapshot.");
        }
    }

    private static void DisposalWithoutContext()
    {
        using var directory = new TestDirectory();
        Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new RejectPosts());
            try
            {
                using var memory = new GuideBindingMemory(directory.File);
                memory.Remember(Record());
                memory.FlushAsync().GetAwaiter().GetResult();
                memory.Remember(Record(2));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        using var restored = new GuideBindingMemory(directory.File);
        Ready(restored).GetAwaiter().GetResult();
        Check(restored.Entries.Length == 2, "Context-free disposal did not drain pending persistence.");
    }

    private sealed class RejectPosts : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => throw new InvalidOperationException("Persistence requires a UI message loop.");
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foretell-guide-bindings-" + Guid.NewGuid().ToString("N"));
        public string File => System.IO.Path.Combine(Path, "bindings.json");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
