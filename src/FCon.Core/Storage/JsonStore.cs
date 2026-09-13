using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCon.Core.Storage;

/// <summary>
/// Small atomic JSON file store. Writes go to a temp file and are then swapped in, so a
/// crash or a power cut can never leave a half-written profile list behind.
/// </summary>
public sealed class JsonStore<T>(string path, Func<T> createDefault) where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = path;

    public T Load()
    {
        try
        {
            if (!File.Exists(Path)) return createDefault();
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? createDefault();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            BackupCorrupt();
            return createDefault();
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            var json = JsonSerializer.Serialize(value, Options);
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);

            if (File.Exists(Path)) File.Replace(temp, Path, null, ignoreMetadataErrors: true);
            else File.Move(temp, Path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Save(T value) => SaveAsync(value).GetAwaiter().GetResult();

    /// <summary>Keep an unreadable file around rather than silently overwriting the user's data.</summary>
    private void BackupCorrupt()
    {
        try
        {
            if (!File.Exists(Path)) return;
            var backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(Path, backup, overwrite: true);
        }
        catch (IOException)
        {
            // Nothing useful to do; the caller falls back to defaults either way.
        }
    }
}
