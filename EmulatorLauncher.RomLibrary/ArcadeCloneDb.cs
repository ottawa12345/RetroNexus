using System.Text.Json;

namespace EmulatorLauncher.RomLibrary;

/// <summary>
/// アーケード系コア(FBNeo / MAME 2003-Plus)のクローン関係(data/arcade/{コンソールId}.json)。
/// 各コアのXML(FBNeoの-listinfo、MAME 2003-Plusのmetadata/mame2003-plus.xml)から、
/// 「クローン名 → 親名」と「一覧から隠すセット(BIOS等、単独で起動できないもの)」だけを抜き出したもの。
/// 名前はROMセット名(zipのファイル名から拡張子を除いたもの。例: "sfa3"、"sfa3u")。大文字小文字は区別しない。
/// 読み込みは1コンソールにつき1回だけ(結果をキャッシュ)。ファイルが無い/壊れている場合は「データなし」として扱い、例外は投げない。
/// </summary>
public sealed class ArcadeCloneInfo
{
    /// <summary>クローンのセット名 → 親のセット名。</summary>
    public Dictionary<string, string> Clones { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一覧に出さないセット名(BIOS・起動できないセット)。</summary>
    public HashSet<string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ArcadeCloneDb
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ArcadeCloneInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string GetPath(string consoleId)
        => Path.Combine(AppContext.BaseDirectory, "data", "arcade", consoleId + ".json");

    /// <summary>このコンソール用のクローン表があれば返す。無ければfalse。</summary>
    public static bool TryGet(string consoleId, out ArcadeCloneInfo info)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(consoleId, out ArcadeCloneInfo? cached))
            {
                cached = Load(consoleId);
                Cache[consoleId] = cached;
            }
            info = cached ?? new ArcadeCloneInfo();
            return cached is not null;
        }
    }

    private static ArcadeCloneInfo? Load(string consoleId)
    {
        try
        {
            string path = GetPath(consoleId);
            if (!File.Exists(path)) return null;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var info = new ArcadeCloneInfo();
            if (doc.RootElement.TryGetProperty("clones", out JsonElement clones) && clones.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in clones.EnumerateObject())
                {
                    string? parent = p.Value.GetString();
                    if (!string.IsNullOrEmpty(parent)) info.Clones[p.Name] = parent;
                }
            }
            if (doc.RootElement.TryGetProperty("hidden", out JsonElement hidden) && hidden.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in hidden.EnumerateArray())
                {
                    string? name = e.GetString();
                    if (!string.IsNullOrEmpty(name)) info.Hidden.Add(name);
                }
            }
            return info.Clones.Count > 0 || info.Hidden.Count > 0 ? info : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArcadeCloneDb] 読み込みに失敗しました: {ex.Message}");
            return null;
        }
    }
}
