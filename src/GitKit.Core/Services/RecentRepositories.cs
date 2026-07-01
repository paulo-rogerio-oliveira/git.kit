using System.Text;
using System.Text.Json;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IRecentRepositories"/> persistindo o histórico em
/// um arquivo JSON (lista MRU, limitada a <see cref="MaxEntries"/> entradas).
/// </summary>
public sealed class RecentRepositories : IRecentRepositories
{
    private const int MaxEntries = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _filePath;

    public RecentRepositories(string filePath) => _filePath = filePath;

    public IReadOnlyList<string> GetAll() => Load().Sources;

    public void Add(string source)
    {
        source = source?.Trim() ?? string.Empty;
        if (source.Length == 0)
            return;

        var index = Load();
        // Remove duplicata (case-insensitive) e coloca no topo como mais recente.
        index.Sources.RemoveAll(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));
        index.Sources.Insert(0, source);
        if (index.Sources.Count > MaxEntries)
            index.Sources.RemoveRange(MaxEntries, index.Sources.Count - MaxEntries);

        Save(index);
    }

    private RecentRepositoriesIndex Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath, Utf8NoBom);
                return JsonSerializer.Deserialize<RecentRepositoriesIndex>(json, JsonOptions) ?? new RecentRepositoriesIndex();
            }
        }
        catch
        {
            // Arquivo corrompido → recomeça vazio.
        }
        return new RecentRepositoriesIndex();
    }

    private void Save(RecentRepositoriesIndex index)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(index, JsonOptions), Utf8NoBom);
        }
        catch
        {
            // Persistência é melhor-esforço.
        }
    }
}
