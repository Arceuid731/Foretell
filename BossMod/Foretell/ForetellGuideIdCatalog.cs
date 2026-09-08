using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Lumina;
using Lumina.Data;
using Lumina.Excel;

namespace BossMod.Foretell;

internal sealed record GuideGameRow(string Kind, uint ID, string EnglishName, string[] Names)
{
    public int CastType { get; init; }
    public float EffectRange { get; init; }
}

internal sealed class GuideIdCatalog
{
    public const int RowLimit = 250_000;
    public const int NameLengthLimit = 512;
    public const int NamesPerRowLimit = 32;
    public const int CandidateLimit = 8192;
    public const int TotalNameLengthLimit = 32 * 1024 * 1024;

    private readonly Dictionary<(string Kind, uint ID), GuideGameRow> _rows = [];
    private readonly Dictionary<(string Kind, string Name), GuideGameRow[]> _aliases = [];

    public string Fingerprint { get; }
    public int Count => _rows.Count;

    public GuideIdCatalog(IEnumerable<GuideGameRow> rows) : this(rows, CancellationToken.None) { }

    public GuideIdCatalog(IEnumerable<GuideGameRow> rows, CancellationToken cancellation) : this(rows, cancellation, []) { }

    private GuideIdCatalog(IEnumerable<GuideGameRow> rows, CancellationToken cancellation, KeyValuePair<string, string>[] repositoryVersions)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rows);
        var inputCount = 0;
        var nameLength = 0;
        foreach (var input in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++inputCount > RowLimit)
                throw new InvalidDataException($"Game ID catalogue exceeds {RowLimit} input rows.");
            ArgumentNullException.ThrowIfNull(input);
            ValidateKind(input.Kind);
            ValidateName(input.EnglishName);
            ArgumentNullException.ThrowIfNull(input.Names);
            if (input.Names.Length > NamesPerRowLimit)
                throw new InvalidDataException($"Game ID row exceeds {NamesPerRowLimit} names.");
            if (input.CastType < 0 || !float.IsFinite(input.EffectRange) || input.EffectRange < 0)
                throw new InvalidDataException($"Invalid game ID metadata for {input.Kind}:{input.ID}.");

            var names = new HashSet<string>(StringComparer.Ordinal) { input.EnglishName };
            AddNameLength(input.EnglishName.Length);
            foreach (var name in input.Names)
            {
                ValidateName(name);
                AddNameLength(name.Length);
                names.Add(name);
            }
            var snapshot = input with { Names = [], EffectRange = input.EffectRange == 0 ? 0 : input.EffectRange };
            var key = (input.Kind, input.ID);
            if (_rows.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.EnglishName, snapshot.EnglishName, StringComparison.Ordinal)
                    || existing.CastType != snapshot.CastType || existing.EffectRange != snapshot.EffectRange)
                    throw new InvalidDataException($"Conflicting canonical name or metadata for {input.Kind}:{input.ID}.");
                names.UnionWith(existing.Names);
            }
            if (names.Count > NamesPerRowLimit)
                throw new InvalidDataException($"Game ID row exceeds {NamesPerRowLimit} merged names.");
            _rows[key] = snapshot with { Names = names.Order(StringComparer.Ordinal).ToArray() };
        }

        cancellation.ThrowIfCancellationRequested();
        var orderedRows = _rows.Values.OrderBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.ID).ToArray();
        var aliases = new Dictionary<(string Kind, string Name), List<GuideGameRow>>();
        foreach (var row in orderedRows)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var alias in row.Names.Select(name => Normalize(row.Kind, name)).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal))
            {
                var key = (row.Kind, alias);
                if (!aliases.TryGetValue(key, out var candidates))
                    aliases.Add(key, candidates = []);
                if (candidates.Count >= CandidateLimit)
                    throw new InvalidDataException($"Game ID alias exceeds {CandidateLimit} candidates in {row.Kind}.");
                candidates.Add(row);
            }
        }
        foreach (var (key, candidates) in aliases)
        {
            cancellation.ThrowIfCancellationRequested();
            _aliases.Add(key, candidates.ToArray());
        }
        Fingerprint = ComputeFingerprint(orderedRows, repositoryVersions, cancellation);

        void AddNameLength(int length)
        {
            if (length > TotalNameLengthLimit - nameLength)
                throw new InvalidDataException($"Game ID catalogue exceeds {TotalNameLengthLimit} input name characters.");
            nameLength += length;
        }
    }

    public GuideGameRow[] Find(string kind, string name)
    {
        ValidateKind(kind);
        ValidateName(name);
        return _aliases.TryGetValue((kind, Normalize(kind, name)), out var rows) ? rows.Select(Snapshot).ToArray() : [];
    }

    public GuideGameRow? Row(string kind, uint id)
    {
        ValidateKind(kind);
        return _rows.TryGetValue((kind, id), out var row) ? Snapshot(row) : null;
    }

    public static GuideIdCatalog LoadInstalled(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return FromGameData(Service.LuminaGameData ?? throw new InvalidOperationException("Installed game data is unavailable."), cancellation);
    }

    public static GuideIdCatalog FromGameData(GameData data, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);
        var repositoryVersions = data.Repositories
            .Select(repository => new KeyValuePair<string, string>(repository.Key, repository.Value.Version ?? ""))
            .Where(repository => !string.IsNullOrWhiteSpace(repository.Value))
            .OrderBy(repository => repository.Key, StringComparer.Ordinal).ToArray();
        return new(ReadRows(), cancellation, repositoryVersions);

        IEnumerable<GuideGameRow> ReadRows()
        {
            foreach (var row in ReadSheet<Lumina.Excel.Sheets.Action>(data, "cast", row => row.Name.ToString(),
                row => (row.CastType, row.EffectRange), cancellation))
                yield return row;
            foreach (var row in ReadSheet<Lumina.Excel.Sheets.Status>(data, "status", row => row.Name.ToString(), row => (0, 0), cancellation))
                yield return row;
            foreach (var row in ReadSheet<Lumina.Excel.Sheets.BNpcName>(data, "boss", row => row.Singular.ToString(), row => (0, 0), cancellation))
                yield return row;
        }
    }

    private static IEnumerable<GuideGameRow> ReadSheet<T>(GameData data, string kind, Func<T, string> name,
        Func<T, (int CastType, float EffectRange)> metadata, CancellationToken cancellation) where T : struct, IExcelRow<T>
    {
        Language[] languages = [Language.English, Language.French, Language.German, Language.Japanese];
        var sheets = new ExcelSheet<T>[languages.Length];
        for (var index = 0; index < languages.Length; ++index)
        {
            cancellation.ThrowIfCancellationRequested();
            var language = languages[index];
            var sheet = data.GetExcelSheet<T>(language) ?? throw new InvalidDataException($"Missing {typeof(T).Name} sheet for {language}.");
            if (sheet.Language != language)
                throw new InvalidDataException($"Wrong language for {typeof(T).Name}: requested {language}, received {sheet.Language}.");
            if (sheet.Count > RowLimit)
                throw new InvalidDataException($"{typeof(T).Name} sheet exceeds {RowLimit} rows.");
            if (index > 0 && sheet.Count != sheets[0].Count)
                throw new InvalidDataException($"Inconsistent {typeof(T).Name} row counts for {language}.");
            sheets[index] = sheet;
        }
        foreach (var english in sheets[0])
        {
            cancellation.ThrowIfCancellationRequested();
            var names = new string[languages.Length];
            names[0] = name(english);
            for (var index = 1; index < languages.Length; ++index)
            {
                cancellation.ThrowIfCancellationRequested();
                var localized = sheets[index].GetRowOrDefault(english.RowId)
                    ?? throw new InvalidDataException($"Missing {typeof(T).Name}:{english.RowId} for {languages[index]}.");
                names[index] = name(localized);
            }
            var values = metadata(english);
            yield return new(kind, english.RowId, names[0], names) { CastType = values.CastType, EffectRange = values.EffectRange };
        }
    }

    private static GuideGameRow Snapshot(GuideGameRow row) => row with { Names = (string[])row.Names.Clone() };

    private static void ValidateKind(string kind)
    {
        if (kind is not ("cast" or "status" or "boss"))
            throw new ArgumentException("Game ID kind must be cast, status or boss.", nameof(kind));
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > NameLengthLimit)
            throw new InvalidDataException($"Game ID name exceeds {NameLengthLimit} characters.");
    }

    private static string Normalize(string kind, string name)
    {
        var normalized = kind == "boss" ? GuideNames.Boss(name) : GuideNames.Normalize(name);
        if (normalized.Length > NameLengthLimit)
            throw new InvalidDataException($"Normalized game ID name exceeds {NameLengthLimit} characters.");
        return normalized;
    }

    private static string ComputeFingerprint(GuideGameRow[] rows, KeyValuePair<string, string>[] repositoryVersions, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText("Foretell.GuideIdCatalog.1");
        AppendNumber((uint)rows.Length);
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            AppendText(row.Kind);
            AppendNumber(row.ID);
            AppendText(row.EnglishName);
            AppendNumber((uint)row.CastType);
            AppendNumber(BitConverter.SingleToUInt32Bits(row.EffectRange));
            AppendNumber((uint)row.Names.Length);
            foreach (var name in row.Names)
                AppendText(name);
        }
        if (repositoryVersions.Length > 0)
        {
            AppendText("Lumina.RepositoryVersions.1");
            AppendNumber((uint)repositoryVersions.Length);
            foreach (var repository in repositoryVersions)
            {
                cancellation.ThrowIfCancellationRequested();
                AppendText(repository.Key);
                AppendText(repository.Value);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());

        void AppendNumber(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }

        void AppendText(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            AppendNumber((uint)bytes.Length);
            hash.AppendData(bytes);
        }
    }
}
