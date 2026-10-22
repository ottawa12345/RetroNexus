using System.Text;
using System.Text.RegularExpressions;

namespace EmulatorLauncher.RomLibrary;

/// <summary>RDBのタイトルと、libretro-thumbnailsの実ファイル名を「名寄せ」して一致させる。</summary>
public static class BoxArtTitleMatcher
{
    /// <summary>"(...)"および"[...]"で括られた1グループ(丸カッコ/角カッコの中身)。</summary>
    private static readonly Regex BracketGroupPattern = new(@"[\(\[]([^\(\)\[\]]*)[\)\]]", RegexOptions.Compiled);

    /// <summary>リージョン・言語トークンの正規化表。</summary>
    private static readonly Dictionary<string, string> RegionLanguageAliases = new(StringComparer.Ordinal)
    {
        ["JAPAN"] = "JP",
        ["J"] = "JP",
        ["JA"] = "JP",
        ["JPN"] = "JP",
        ["NIPPON"] = "JP",
        ["JAPANESE"] = "JP",
        ["USA"] = "US",
        ["U"] = "US",
        ["US"] = "US",
        ["AMERICA"] = "US",
        ["EUROPE"] = "EU",
        ["E"] = "EU",
        ["EUR"] = "EU",
        ["WORLD"] = "WORLD",
        ["W"] = "WORLD",
        ["ENGLISH"] = "EN",
        ["EN"] = "EN",
        ["ENG"] = "EN",
        ["FRENCH"] = "FR",
        ["FRANCE"] = "FR",
        ["FR"] = "FR",
        ["FRA"] = "FR",
        ["GERMAN"] = "DE",
        ["GERMANY"] = "DE",
        ["DE"] = "DE",
        ["GER"] = "DE",
        ["G"] = "DE",
        ["SPANISH"] = "ES",
        ["SPAIN"] = "ES",
        ["ES"] = "ES",
        ["ESP"] = "ES",
        ["S"] = "ES",
        ["ITALIAN"] = "IT",
        ["ITALY"] = "IT",
        ["IT"] = "IT",
        ["ITA"] = "IT",
        ["I"] = "IT",
        ["DUTCH"] = "NL",
        ["NETHERLANDS"] = "NL",
        ["HOLLAND"] = "NL",
        ["NL"] = "NL",
        ["H"] = "NL",
        ["PORTUGUESE"] = "PT",
        ["PORTUGAL"] = "PT",
        ["PT"] = "PT",
        ["POR"] = "PT",
        ["SWEDISH"] = "SV",
        ["SWEDEN"] = "SV",
        ["SV"] = "SV",
        ["SWE"] = "SV",
        ["KOREAN"] = "KO",
        ["KOREA"] = "KO",
        ["KO"] = "KO",
        ["KOR"] = "KO",
        ["K"] = "KO",
        ["CHINESE"] = "ZH",
        ["CHINA"] = "ZH",
        ["ZH"] = "ZH",
        ["CHN"] = "ZH",
        ["C"] = "ZH",
        ["ASIA"] = "ASIA",
        ["BRAZIL"] = "BR",
        ["BRA"] = "BR",
        ["B"] = "BR",
        ["AUSTRALIA"] = "AU",
        ["AUS"] = "AU",
        ["A"] = "AU",
        ["RUSSIAN"] = "RU",
        ["RUSSIA"] = "RU",
        ["RU"] = "RU",
        ["RUS"] = "RU",
        ["TAIWAN"] = "TW",
        ["TW"] = "TW",
    };

    /// <summary>製品版とは別物として扱うべきキーワード。</summary>
    private static readonly string[] RiskyKeywords =
    {
        "DEMO", "SAMPLE", "KIOSK", "PROTOTYPE", "PROTO", "BETA", "ALPHA",
        "UNLICENSED", "UNL", "PIRATE", "BOOTLEG", "BAD DUMP", "OVERDUMP",
        "HACK", "TRAINER", "DEBUG", "NOT FOR RESALE", "AFTERMARKET",
    };

    /// <summary>タイトルから、丸カッコ/角カッコを取り除いた「基本タイトル」を取り出す。</summary>
    public static string NormalizeBaseTitle(string title)
    {
        string withoutBrackets = BracketGroupPattern.Replace(title, " ");
        return CollapseWhitespace(withoutBrackets).ToUpperInvariant();
    }

    /// <summary>libretro-thumbnailsの実ファイル一覧(ファイル名の集合)から、指定タイトルに最も近いファイルを探す。</summary>
    public static string? FindBestMatch(string title, IReadOnlyDictionary<string, List<string>> baseTitleIndex)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (!baseTitleIndex.TryGetValue(NormalizeBaseTitle(title), out List<string>? candidates) || candidates.Count == 0)
        {
            return null;
        }

        return PickBest(title, candidates);
    }

    /// <summary>比較用の「簡略キー」(小文字・英数字だけ・括弧内を除く・アクセント除去)。</summary>
    public static string CompactKey(string title) => RdbDatabase.SplitLooseName(title).Title;

    /// <summary>簡略キー()が一致する実ファイルの中から、最も近いものを探す。</summary>
    public static string? FindBestMatchCompact(string title, IReadOnlyDictionary<string, List<string>> compactIndex)
    {
        string key = CompactKey(title);
        if (key.Length == 0 || !compactIndex.TryGetValue(key, out List<string>? candidates) || candidates.Count == 0)
        {
            return null;
        }

        return PickBest(title, candidates);
    }

    private static string? PickBest(string title, List<string> candidates)
    {

        (HashSet<string> Risky, HashSet<string> Tags) target = ExtractTagInfo(title);

        string? bestFileName = null;
        double bestScore = -1;
        int bestTagCount = int.MaxValue;

        foreach (string fileName in candidates)
        {
            (HashSet<string> Risky, HashSet<string> Tags) candidate = ExtractTagInfo(RemovePngExtension(fileName));

            // Demo/Proto/Beta等は、探している側とファイル側で有無が一致しない限り対象外にする(製品版のゲームにデモ版の画像を割り当てる、といった事故を防ぐため)。
            if (!target.Risky.SetEquals(candidate.Risky))
            {
                continue;
            }

            double score = JaccardSimilarity(target.Tags, candidate.Tags);

            // 同点の場合は、タグ数が少ない(より汎用的な)候補を優先する。
            if (score > bestScore || (score == bestScore && candidate.Tags.Count < bestTagCount))
            {
                bestScore = score;
                bestTagCount = candidate.Tags.Count;
                bestFileName = fileName;
            }
        }

        return bestFileName;
    }

    /// <summary>丸カッコ/角カッコの中身をすべて集め、危険タグと通常タグの集合に分類する。</summary>
    private static (HashSet<string> Risky, HashSet<string> Tags) ExtractTagInfo(string title)
    {
        var risky = new HashSet<string>(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match group in BracketGroupPattern.Matches(title))
        {
            // "(Japan, USA)"のように1つの丸カッコ内に複数のタグがカンマ区切りで入っていることがある。
            foreach (string rawToken in group.Groups[1].Value.Split(','))
            {
                string token = NormalizeToken(rawToken);
                if (token.Length == 0)
                {
                    continue;
                }

                string? riskyKeyword = RiskyKeywords.FirstOrDefault(
                    k => token.StartsWith(k, StringComparison.Ordinal));
                if (riskyKeyword is not null)
                {
                    risky.Add(riskyKeyword);
                    continue;
                }

                tags.Add(RegionLanguageAliases.TryGetValue(token, out string? canonical) ? canonical : token);
            }
        }

        return (risky, tags);
    }

    /// <summary>比較用トークンへ正規化する(大文字化、前後トリム、連続空白の圧縮)。</summary>
    private static string NormalizeToken(string rawToken)
    {
        var builder = new StringBuilder(rawToken.Length);
        foreach (char c in rawToken)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-')
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return CollapseWhitespace(builder.ToString());
    }

    private static string CollapseWhitespace(string value) => WhitespacePattern.Replace(value, " ").Trim();

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private static string RemovePngExtension(string fileName)
        => fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

    /// <summary>2つの集合のJaccard類似度(|A∩B| / |A∪B|)。</summary>
    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1.0;
        }

        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 1.0 : (double)intersection / union;
    }
}