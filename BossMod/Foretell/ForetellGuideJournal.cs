using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossMod.Foretell;

internal sealed record GuideAnalysisStep(string Stage, string Boss = "", int Attempt = 0, string Detail = "");
internal sealed record GuideAnalysisEvent(DateTime At, string Stage, string Boss, int Attempt, string Detail);
internal sealed record GuideAnalysisCall(int Index, DateTime StartedAt, string State, string Stage, string Boss, int Attempt,
    string System = "", string Source = "", string Response = "", string Reasoning = "", string FinishReason = "",
    int? PromptTokens = null, int? OutputTokens = null, string Error = "", double Seconds = 0)
{
    public string RequestJson { get; init; } = "";
}
internal sealed record GuideAnalysisReport(string ID, DateTime StartedAt, GuideDuty Duty, string ModelID, bool CaptureConversation)
{
    public DateTime? FinishedAt { get; init; }
    public string Stage { get; init; } = "Queued";
    public string Boss { get; init; } = "";
    public int Attempt { get; init; }
    public string Error { get; init; } = "";
    public string RecordingError { get; init; } = "";
    public string FilePath { get; init; } = "";
    public bool ContentOmitted { get; init; }
    public GuideAnalysisCall[] Calls { get; init; } = [];
    public GuideAnalysisEvent[] Events { get; init; } = [];
    public string RuntimeLog { get; init; } = "";
    public int ContextTokens { get; init; }
    public int MemoryGiB { get; init; }
    public bool Gpu { get; init; }
    public string SourceHash { get; init; } = "";
    public GuideDocument? SourceDocument { get; init; }
}

internal sealed class GuideAnalysisJournal(string directory)
{
    internal const int FileLimit = 32 * 1024 * 1024;
    internal const int ReportLimit = 6;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly object _gate = new();
    private GuideAnalysisReport[] _reports = [];
    public GuideAnalysisReport[] Reports { get { lock (_gate) return _reports.ToArray(); } }

    public void Load()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("analysis-*.tmp").Where(file => file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                DeleteTemporary(file.FullName);
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("analysis-*.json").OrderByDescending(file => file.Name).Take(ReportLimit))
            {
                if (file.Length > FileLimit || !ValidID(Path.GetFileNameWithoutExtension(file.Name))) continue;
                try
                {
                    var report = JsonSerializer.Deserialize<GuideAnalysisReport>(File.ReadAllText(file.FullName), Json);
                    if (report == null || report.ID + ".json" != file.Name || !ValidReport(report)) continue;
                    if (report.FinishedAt == null) report = report with { Stage = "Interrupted", Error = "Analysis ended before a result was recorded." };
                    Remember(report with { FilePath = file.FullName });
                }
                catch (Exception error) when (error is JsonException or IOException or ArgumentException or InvalidOperationException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public GuideAnalysisSession Begin(GuideDocument document, string modelID, bool gpu, int context, int memory, bool conversation)
    {
        var now = DateTime.UtcNow;
        var id = $"analysis-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..33];
        return new(this, new(id, now, document.Duty, modelID, conversation)
        {
            ContextTokens = context, MemoryGiB = memory, Gpu = gpu, SourceHash = document.SourceHash,
            SourceDocument = conversation ? document : null, FilePath = Path.Combine(_directory, id + ".json")
        });
    }

    private static bool ValidID(string id) => System.Text.RegularExpressions.Regex.IsMatch(id,
        @"^analysis-[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static bool ValidReport(GuideAnalysisReport report)
        => report.Duty?.Valid == true && report.ModelID != null && report.Stage != null && report.Boss != null && report.Error != null
            && report.RecordingError != null && report.RuntimeLog != null && report.SourceHash != null
            && report.Calls is { Length: <= 256 } && report.Events is { Length: <= 256 }
            && report.Calls.All(call => call != null && call.Index > 0 && call.State != null && call.Stage != null && call.Boss != null
                && call.System != null && call.Source != null && call.Response != null && call.Reasoning != null && call.RequestJson != null
                && call.Error != null && call.FinishReason != null && double.IsFinite(call.Seconds))
            && report.Events.All(entry => entry != null && entry.Stage != null && entry.Boss != null && entry.Detail != null);

    internal void Remember(GuideAnalysisReport report)
    {
        lock (_gate) _reports = _reports.Where(previous => previous.ID != report.ID).Append(report)
            .OrderByDescending(report => report.StartedAt).Take(ReportLimit).ToArray();
    }

    internal GuideAnalysisReport Save(GuideAnalysisReport report)
    {
        if (!ValidID(report.ID)) return report with { RecordingError = "Invalid diagnostic identity." };
        var path = Path.Combine(_directory, report.ID + ".json");
        try
        {
            report = report with { RecordingError = "", FilePath = path };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
            if (bytes.Length > FileLimit)
            {
                report = report with
                {
                    ContentOmitted = true, SourceDocument = null,
                    Calls = report.Calls.Select(call => call with { System = "", Source = "", Response = "", Reasoning = "", RequestJson = "" }).ToArray()
                };
                bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
            }
            Directory.CreateDirectory(_directory);
            CheckTemporaryCapacity();
            if (bytes.Length > FileLimit) throw new IOException("Diagnostic size limit reached.");
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, true);
            report = report with { RecordingError = "", FilePath = path };
            Prune("analysis-*.json");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            report = report with { RecordingError = error.Message };
        }
        finally { DeleteTemporary(path + ".tmp"); }
        Remember(report);
        return report;
    }

    private void Prune(string pattern)
    {
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles(pattern).OrderByDescending(file => file.LastWriteTimeUtc).Skip(ReportLimit)) file.Delete();
    }

    private void CheckTemporaryCapacity()
    {
        if (new DirectoryInfo(_directory).EnumerateFiles("analysis-*.tmp").Take(ReportLimit).Count() >= ReportLimit)
            throw new IOException("Temporary diagnostic files could not be cleaned up.");
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public Task<string> ExportAsync(string id) => Task.Run(() =>
    {
        var report = Reports.FirstOrDefault(report => report.ID == id) ?? throw new InvalidOperationException("Diagnostic no longer available.");
        if (!ValidID(id)) throw new InvalidDataException("Invalid diagnostic identity.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
        if (bytes.Length > FileLimit) throw new InvalidDataException("Diagnostic exceeds the export limit.");
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, id + ".zip");
        var temporary = path + ".tmp";
        try
        {
            CheckTemporaryCapacity();
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            using (var output = archive.CreateEntry("analysis.json", CompressionLevel.Optimal).Open()) output.Write(bytes);
            File.Move(temporary, path, true);
        }
        finally { DeleteTemporary(temporary); }
        Prune("analysis-*.zip");
        return path;
    });
}

internal sealed class GuideAnalysisSession
{
    private readonly GuideAnalysisJournal _journal;
    private readonly object _gate = new();
    private GuideAnalysisReport _report;
    private bool _ignoreExchange;
    public GuideAnalysisReport Report { get { lock (_gate) return _report; } }

    public GuideAnalysisSession(GuideAnalysisJournal journal, GuideAnalysisReport report)
    {
        _journal = journal;
        _report = _journal.Save(report);
    }

    public void Step(GuideAnalysisStep step)
    {
        lock (_gate)
        {
            if (_report.FinishedAt != null) return;
            _report = _journal.Save(_report with
            {
                Stage = step.Stage, Boss = step.Boss, Attempt = step.Attempt,
                Events = _report.Events.Append(new(DateTime.UtcNow, step.Stage, step.Boss, step.Attempt, step.Detail)).TakeLast(256).ToArray()
            });
        }
    }

    public void Exchange(GuideModelExchange exchange)
    {
        lock (_gate)
        {
            if (_report.FinishedAt != null) return;
            var calls = _report.Calls;
            if (exchange.State == "Started") _ignoreExchange = calls.Length >= 256;
            if (_ignoreExchange)
            {
                if (!_report.ContentOmitted) _report = _journal.Save(_report with { ContentOmitted = true });
                return;
            }
            if (exchange.State == "Started" || calls.Length == 0)
            {
                if (calls.Length >= 256) { _report = _report with { ContentOmitted = true }; return; }
                calls = [.. calls, new(calls.Length + 1, DateTime.UtcNow, exchange.State, _report.Stage, _report.Boss, _report.Attempt)];
            }
            var capture = _report.CaptureConversation && !_report.ContentOmitted;
            calls = calls.ToArray();
            calls[^1] = calls[^1] with
            {
                State = exchange.State, System = capture ? exchange.System : "", Source = capture ? exchange.Source : "",
                Response = capture ? exchange.Response : "", Reasoning = capture ? exchange.Reasoning : "",
                RequestJson = capture ? exchange.RequestJson : "",
                FinishReason = exchange.FinishReason, PromptTokens = exchange.PromptTokens, OutputTokens = exchange.OutputTokens,
                Error = exchange.Error, Seconds = exchange.Seconds
            };
            _report = _journal.Save(_report with { Calls = calls });
        }
    }

    public void Runtime(string line)
    {
        lock (_gate)
        {
            if (_report.FinishedAt != null) return;
            var log = _report.RuntimeLog + line + "\n";
            if (log.Length > 32768) log = "[Earlier runtime lines omitted]\n" + log[^32000..];
            _report = _report with { RuntimeLog = log };
            _journal.Remember(_report);
        }
    }

    public void Finish(string stage, Exception? error = null)
    {
        lock (_gate)
        {
            if (_report.FinishedAt != null) return;
            _report = _journal.Save(_report with { Stage = stage, FinishedAt = DateTime.UtcNow, Error = error?.ToString() ?? "" });
        }
    }
}
