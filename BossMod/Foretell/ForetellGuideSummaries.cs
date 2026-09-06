using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace BossMod.Foretell;

internal sealed record GuideSummarySnapshot(string SourceHash, GuideLanguage Language, ImmutableDictionary<string, string> Summaries,
    string Stage, int Completed, int Total, GuideModelProgress? Transfer = null, double? RemainingSeconds = null, string? LastIssue = null)
{
    public GuideDocument? Prepared { get; init; }
    public string ModelID { get; init; } = GuideModelCatalog.DefaultID;
    public GuideAnalysisTiming AnalysisTiming { get; init; } = new();
}

internal sealed class ForetellGuideSummaries : IDisposable
{
    private sealed record Request(GuideDocument Document, GuideLanguage Language, bool Gpu, int ContextTokens, int MemoryGiB, string ModelID, CancellationTokenSource Cancellation)
    {
        public bool Finished;
        public string? LastIssue;
        public bool Refresh;
        public bool Prepare;
        public GuideDocument? Partial;
        public readonly GuideAnalysisMemory Analysis = new();
        public GuideAnalysisTiming AnalysisTiming = new();
    }
    private sealed record Cache(string Revision, string SourceHash, GuideLanguage Language, Dictionary<string, string> Summaries);
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Channel<Request> _queue = Channel.CreateBounded<Request>(1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _activity = new();
    private Request? _latest;
    private volatile GuideSummarySnapshot? _snapshot;
    private volatile bool _paused;
    private volatile string _preferredBoss = "";
    private bool _disposed;
    private bool _refreshNext;
    private readonly Task _worker;
    private readonly Func<bool, IGuideSummaryModel>? _modelFactory;
    private volatile IGuideSummaryModel? _activeModel;
    private volatile GuideModelRuntime _lastRuntime = new();
    private (string ModelID, bool Gpu, long CheckedAt, GuideModelStorage Storage)? _storage;
    public GuideSummarySnapshot? Snapshot => _snapshot;
    public GuideModelRuntime Runtime => _activeModel?.Runtime ?? _lastRuntime;

    public GuideModelStorage Storage(string modelID, bool gpu)
    {
        var profile = GuideModelCatalog.Get(modelID);
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_storage is { } cached && cached.ModelID == profile.ID && cached.Gpu == gpu && Stopwatch.GetElapsedTime(cached.CheckedAt, now).TotalSeconds < 2)
                return cached.Storage;
            var directory = Path.Combine(_directory, "runtime");
            var storage = new GuideModelStorage(ForetellGuideLocalModel.InspectAsset(directory, profile.Asset),
                ForetellGuideLocalModel.InspectAsset(directory, gpu ? ForetellGuideLocalModel.Vulkan : ForetellGuideLocalModel.Cpu));
            _storage = (profile.ID, gpu, now, storage);
            return storage;
        }
    }

    internal Task Completion => _worker;
    internal static string Key(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic) => GuideNames.Hash(boss.Name + "\n" + phase.Name + "\n" + phase.ContextHash + "\n" + mechanic.Name + "\n" + mechanic.TextHash);
    internal static string ContextKey(GuideBoss boss, GuidePhase phase) => GuideNames.Hash("context\n" + boss.Name + "\n" + phase.Name + "\n" + phase.ContextHash);
    private static int ItemCount(GuideDocument document) => document.MechanicCount + document.Bosses.Sum(boss => boss.Phases.Count(phase => phase.Context.Length > 0));

    public ForetellGuideSummaries(string directory, Func<bool, IGuideSummaryModel>? modelFactory = null)
    {
        _directory = directory;
        _modelFactory = modelFactory;
        _worker = Task.Run(Run);
    }

    public void Update(GuideDocument? document, GuideLanguage language, bool enabled, bool combat, bool gpu, string preferredBoss,
        int contextTokens = GuideModelLimits.DefaultContext, int memoryGiB = GuideModelLimits.DefaultMemoryGiB, string modelID = GuideModelCatalog.DefaultID)
    {
        contextTokens = GuideModelLimits.Context(contextTokens);
        memoryGiB = GuideModelLimits.MemoryGiB(memoryGiB);
        modelID = GuideModelCatalog.Get(modelID).ID;
        lock (_gate)
        {
            if (_disposed) return;
            _preferredBoss = preferredBoss;
            if (_paused != combat)
            {
                _paused = combat;
                if (combat) _activity.Cancel();
                else { _activity.Dispose(); _activity = new(); }
            }
            if (document == null || !enabled && document.Page == null)
            {
                CancelLatest(); _latest = null; _snapshot = null;
                return;
            }
            if (_latest is { } latest && latest.Document.SourceHash == document.SourceHash && latest.Document.Duty == document.Duty && latest.Language == language
                && latest.Gpu == gpu && latest.ContextTokens == contextTokens && latest.MemoryGiB == memoryGiB && latest.ModelID == modelID && latest.Prepare == enabled) return;
            CancelLatest();
            if (_queue.Reader.TryRead(out var dropped)) dropped.Cancellation.Dispose();
            _latest = new(document, language, gpu, contextTokens, memoryGiB, modelID, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _latest.Refresh = _refreshNext;
            _latest.Prepare = enabled;
            _refreshNext = false;
            _snapshot = new(document.SourceHash, language, ImmutableDictionary<string, string>.Empty, "Queued", 0, ItemCount(document)) { ModelID = modelID };
            _queue.Writer.TryWrite(_latest);
        }
    }

    private void CancelLatest()
    {
        if (_latest is not { } latest) return;
        latest.Cancellation.Cancel();
        if (latest.Finished) latest.Cancellation.Dispose();
    }

    public void Retry(bool refresh = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _refreshNext = refresh;
            CancelLatest(); _latest = null; _snapshot = null;
        }
    }

    private void Publish(Request request, ImmutableDictionary<string, string> summaries, string stage, int completed, int total,
        GuideModelProgress? transfer = null, double? remaining = null, GuideDocument? prepared = null)
    {
        lock (_gate)
            if (!_disposed && _latest == request && !request.Cancellation.IsCancellationRequested)
            {
                request.AnalysisTiming = request.AnalysisTiming.AtStage(stage, Stopwatch.GetTimestamp());
                _snapshot = new(request.Document.SourceHash, request.Language, summaries, stage, completed, total, transfer, remaining, request.LastIssue)
                { Prepared = prepared ?? request.Partial, ModelID = request.ModelID, AnalysisTiming = request.AnalysisTiming };
            }
    }

    private string CachePath(Request request) => Path.Combine(_directory, request.Document.Duty.Key + "-" + request.Language + ".json");

    private ImmutableDictionary<string, string> Read(Request request, Dictionary<string, string> sources)
    {
        try
        {
            var path = CachePath(request);
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return ImmutableDictionary<string, string>.Empty;
            var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(path));
            if (cache?.Revision != ForetellGuideLocalModel.Revision || cache.SourceHash != request.Document.SourceHash || cache.Language != request.Language || cache.Summaries == null)
                return ImmutableDictionary<string, string>.Empty;
            return cache.Summaries.Where(pair => sources.TryGetValue(pair.Key, out var source) && pair.Value != null
                && GuideSummaryValidation.Accept(pair.Value, source) && GuideSummaryValidation.LanguageAndConditions(pair.Value, source, request.Language)).ToImmutableDictionary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return ImmutableDictionary<string, string>.Empty; }
    }

    private void Write(Request request, ImmutableDictionary<string, string> summaries)
    {
        Directory.CreateDirectory(_directory);
        var path = CachePath(request);
        var serialized = JsonSerializer.Serialize(new Cache(ForetellGuideLocalModel.Revision, request.Document.SourceHash, request.Language, summaries.ToDictionary()));
        if (Encoding.UTF8.GetByteCount(serialized) > 2 * 1024 * 1024) throw new InvalidDataException("Summary cache exceeds bounds.");
        File.WriteAllText(path + ".tmp", serialized);
        File.Move(path + ".tmp", path, true);
        var files = new DirectoryInfo(_directory).GetFiles("C*-T*-*.json")
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^C\d+-T\d+-(English|French|German|Japanese)\.json$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long retained = 0;
        for (var index = 0; index < files.Length; ++index)
        {
            retained += files[index].Length;
            if ((index >= 128 || retained > 64L * 1024 * 1024) && files[index].FullName != path) files[index].Delete();
        }
    }

    private async Task Run()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var summaries = ImmutableDictionary<string, string>.Empty;
                try
                {
                    if (request.Document.Page != null) { await AnalyzePage(request).ConfigureAwait(false); continue; }
                    var entries = request.Document.Bosses.SelectMany(boss => boss.Phases.SelectMany(phase =>
                        (phase.Context.Length > 0 ? new[] { (Boss: boss.Name, Key: ContextKey(boss, phase), Source: phase.Context) } : [])
                        .Concat(phase.Mechanics.Select(mechanic => (Boss: boss.Name, Key: Key(boss, phase, mechanic),
                            Source: string.Join("\n", new[] { phase.Context, mechanic.Text }.Where(text => text.Length > 0))))))).ToArray();
                    var sources = entries.GroupBy(entry => entry.Key).ToDictionary(group => group.Key, group => group.First().Source);
                    summaries = Read(request, sources);
                    var pending = entries.DistinctBy(entry => entry.Key).Where(entry => !summaries.ContainsKey(entry.Key)).ToList();
                    var skipped = 0;
                    var durations = new List<double>();
                    if (pending.Count == 0) { Publish(request, summaries, "Ready", summaries.Count, entries.Length); continue; }
                    var model = CreateModel(request);
                    _activeModel = model;
                    while (pending.Count > 0)
                    {
                        request.Cancellation.Token.ThrowIfCancellationRequested();
                        if (_paused)
                        {
                            model.Dispose();
                            Publish(request, summaries, "PausedInCombat", summaries.Count + skipped, entries.Length);
                            await Task.Delay(500, request.Cancellation.Token).ConfigureAwait(false);
                            continue;
                        }
                        CancellationTokenSource active;
                        lock (_gate) active = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token, _activity.Token);
                        using (active)
                        {
                            try
                            {
                                var entry = pending.FirstOrDefault(entry => entry.Boss == _preferredBoss);
                                if (entry.Key == null) entry = pending[0];
                                await model.Start(transfer => Publish(request, summaries, "InstallingOrStarting", summaries.Count + skipped, entries.Length, transfer), active.Token).ConfigureAwait(false);
                                Publish(request, summaries, "Summarizing", summaries.Count + skipped, entries.Length,
                                    remaining: durations.Count == 0 ? null : durations.Average() * pending.Count);
                                var watch = System.Diagnostics.Stopwatch.StartNew();
                                try { summaries = summaries.SetItem(entry.Key, await model.Summarize(entry.Source, request.Language, active.Token).ConfigureAwait(false)); }
                                catch (InvalidDataException error) { ++skipped; request.LastIssue = error.Message.Length <= 400 ? error.Message : error.Message[..400]; }
                                durations.Add(watch.Elapsed.TotalSeconds);
                                pending.Remove(entry);
                                try { Write(request, summaries); }
                                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                                { Publish(request, summaries, "CacheWriteFailed", summaries.Count + skipped, entries.Length); }
                            }
                            catch (OperationCanceledException) when (!request.Cancellation.IsCancellationRequested && active.IsCancellationRequested) { model.Dispose(); }
                        }
                    }
                    model.Dispose();
                    Publish(request, summaries, skipped == 0 ? "Ready" : "ReadyWithUnresolved", summaries.Count + skipped, entries.Length);
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    request.LastIssue = error is InvalidDataException ? error.Message[..Math.Min(error.Message.Length, 400)] : error.GetType().Name;
                    Publish(request, summaries, "Unavailable: " + error.GetType().Name, summaries.Count, ItemCount(request.Document));
                }
                finally
                {
                    if (_activeModel is { } model)
                    {
                        model.Dispose();
                        _lastRuntime = model.Runtime;
                        _activeModel = null;
                    }
                    lock (_gate)
                    {
                        request.Finished = true;
                        if (_latest != request || _disposed) request.Cancellation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { lock (_gate) { _activity.Dispose(); _lifetime.Dispose(); } }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); CancelLatest(); _activity.Cancel();
            _queue.Writer.TryComplete();
            if (_queue.Reader.TryRead(out var dropped)) dropped.Cancellation.Dispose();
        }
    }

    private IGuideSummaryModel CreateModel(Request request) => _modelFactory?.Invoke(request.Gpu)
        ?? new ForetellGuideLocalModel(Path.Combine(_directory, "runtime"), request.Gpu, request.ContextTokens, request.MemoryGiB, request.ModelID);

    private async Task AnalyzePage(Request request)
    {
        var profile = GuideModelCatalog.Get(request.ModelID);
        var cache = new ForetellGuideCache(Path.Combine(_directory, "pages", profile.ID, request.Language.ToString()));
        var prepared = cache.Read(request.Document.Duty);
        if (!request.Refresh && prepared != null && GuidePageAnalysis.ValidPrepared(prepared, request.Document, request.Language, profile))
        {
            PublishPrepared(request, prepared with { RetrievedAt = request.Document.RetrievedAt, Sources = request.Document.Sources, Providers = request.Document.Providers });
            return;
        }
        if (!request.Prepare)
        {
            Publish(request, ImmutableDictionary<string, string>.Empty, "PreparationDisabled", 0, 1);
            return;
        }
        while (true)
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            if (_paused)
            {
                _activeModel?.Dispose();
                Publish(request, ImmutableDictionary<string, string>.Empty, "PausedInCombat", 0, 1);
                await Task.Delay(300, request.Cancellation.Token).ConfigureAwait(false);
                continue;
            }
            CancellationTokenSource active;
            lock (_gate) active = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token, _activity.Token);
            using (active)
            {
                try
                {
                    _activeModel ??= CreateModel(request);
                    await _activeModel.Start(transfer => Publish(request, ImmutableDictionary<string, string>.Empty,
                        "InstallingOrStarting", 0, 1, transfer), active.Token).ConfigureAwait(false);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var resumable = new GuideResumableModel(_activeModel, request.Analysis);
                    prepared = await GuidePageAnalysis.Compile(request.Document, request.Language, profile, request.ContextTokens, resumable,
                        (done, total) => Publish(request, ImmutableDictionary<string, string>.Empty, "Analyzing", done, total,
                            remaining: done > 0 ? watch.Elapsed.TotalSeconds / done * (total - done) : null), active.Token,
                        partial => request.Partial = partial).ConfigureAwait(false);
                    active.Token.ThrowIfCancellationRequested();
                    _activeModel.Dispose();
                    try { cache.Write(prepared); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { request.LastIssue = "CacheWrite: " + error.GetType().Name; }
                    PublishPrepared(request, prepared);
                    return;
                }
                catch (OperationCanceledException) when (!request.Cancellation.IsCancellationRequested && active.IsCancellationRequested) { _activeModel?.Dispose(); }
            }
        }
    }

    private void PublishPrepared(Request request, GuideDocument prepared)
    {
        var summaries = prepared.Bosses.SelectMany(boss => boss.Phases.SelectMany(phase => phase.Mechanics
            .Where(mechanic => mechanic.Advice != null).Select(mechanic => new KeyValuePair<string, string>(Key(boss, phase, mechanic), mechanic.Advice!.Description))))
            .ToImmutableDictionary();
        Publish(request, summaries, "Ready", prepared.MechanicCount, prepared.MechanicCount, prepared: prepared);
    }
}
