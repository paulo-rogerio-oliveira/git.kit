using System.Text;

namespace GitKit.Core.Services;

/// <summary>
/// Monta as linhas de argumento para os executáveis do TortoiseGit.
/// As ferramentas do TortoiseGit usam o parser próprio (CCmdLineParser), que
/// exige aspas envolvendo apenas o <b>valor</b> (<c>/key:"valor com espaço"</c>),
/// e NÃO o argumento inteiro — por isso não se pode usar <c>ArgumentList</c>,
/// que envolveria <c>"/key:valor"</c> e quebraria caminhos com espaço.
/// </summary>
public static class TortoiseGitArguments
{
    /// <summary>Argumentos para o <c>TortoiseGitProc.exe</c> (ex.: resolve, conflicteditor).</summary>
    public static string BuildProc(string command, string path)
        => $"/command:{command} /path:{Quote(path)}";

    /// <summary>Argumentos para o <c>TortoiseGitMerge.exe</c> em merge 3-way.</summary>
    public static string BuildMerge(
        string baseFile, string mineFile, string theirsFile, string mergedFile,
        string? baseName = null, string? mineName = null, string? theirsName = null, string? mergedName = null)
    {
        var sb = new StringBuilder();
        Append(sb, "base", baseFile);
        Append(sb, "mine", mineFile);
        Append(sb, "theirs", theirsFile);
        Append(sb, "merged", mergedFile);
        Append(sb, "basename", baseName);
        Append(sb, "minename", mineName);
        Append(sb, "theirsname", theirsName);
        Append(sb, "mergedname", mergedName);
        return sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (sb.Length > 0)
            sb.Append(' ');

        sb.Append('/').Append(key).Append(':').Append(Quote(value));
    }

    // Envolve o valor em aspas. Aspas internas (raras em caminhos) são removidas
    // para não encerrar a citação prematuramente.
    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty)}\"";
}
