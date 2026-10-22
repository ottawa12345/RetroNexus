using System;
using System.Collections.Generic;
using System.Linq;
using EmulatorLauncher.Models;

namespace EmulatorLauncher.Services;

/// <summary>GameSelectPage / CollectionGameSelectPage の両方で使う、ゲーム一覧まわりのUI非依存な処理を集めた静的ヘルパー(GamepadListNavigatorと同じく状態を持たない)。</summary>
public static class GameListHelper
{
    /// <summary>Consoles.jsonのコンソールごとのAspectRatio(パッケージ画像の縦横比)を、各RomFile.ConsoleIdから引いてRomFile.AspectRatioへ複製する。</summary>
    public static void ApplyAspectRatios(IEnumerable<RomFile> files)
    {
        Dictionary<string, double> aspectRatioByConsoleId = SampleData.GetConsoles()
            .ToDictionary(c => c.Id, c => c.AspectRatio);

        foreach (RomFile file in files)
        {
            file.AspectRatio = aspectRatioByConsoleId.TryGetValue(file.ConsoleId, out double ratio) ? ratio : 1.0;
        }
    }

    /// <summary>指定したソート方法で並べ替えた新しいリストを返す(元のコレクションは変更しない)。</summary>
    public static readonly string[] KanaGroupLabels = BuildKanaGroupLabels();

    // 0〜9=あ行〜わ行、10=数字、11〜36=A〜Z、37=その他。
    private const int DigitGroup = 10;
    private const int AlphaGroupStart = 11;
    private const int OtherGroup = 37;

    private static string[] BuildKanaGroupLabels()
    {
        var labels = new List<string>
        {
            "あ行", "か行", "さ行", "た行", "な行", "は行", "ま行", "や行", "ら行", "わ行", "数字",
        };
        for (char ch = 'A'; ch <= 'Z'; ch++) labels.Add(ch.ToString());
        labels.Add("その他");
        return labels.ToArray();
    }

    /// <summary>ガターの省略表記(「や~ら」「B~M」)用の短いラベル。</summary>
    public static string ShortKanaGroupLabel(int group) => group switch
    {
        DigitGroup => "数",
        OtherGroup => "他",
        _ => KanaGroupLabels[group].TrimEnd('行'),
    };

    private static readonly string[] KanaRows =
    {
        "ぁあぃいぅうぇえぉお", "かきくけこゕゖ", "さしすせそ", "たちっつてと", "なにぬねの",
        "はひふへほ", "まみむめも", "ゃやゅゆょよ", "らりるれろ", "ゎわゐゑをん",
    };

    /// <summary>並べ替えキーの先頭の文字から、あいうえお順のグループ(0=あ行 … 9=わ行、10=数字、11〜36=A〜Z、37=その他)を返す。</summary>
    public static int GetKanaGroupIndex(string? key)
    {
        if (string.IsNullOrEmpty(key)) return OtherGroup;

        foreach (char raw in key)
        {
            string decomposed = raw.ToString().Normalize(System.Text.NormalizationForm.FormKC)
                .Normalize(System.Text.NormalizationForm.FormD);
            if (decomposed.Length == 0) continue;

            char c = decomposed[0];
            if (c >= '\u30A1' && c <= '\u30F6') c = (char)(c - 0x60); // カタカナ→ひらがな
            if (c == '\u30FC') continue; // 長音

            if (c >= '0' && c <= '9') return DigitGroup;
            if (c >= 'A' && c <= 'Z') return AlphaGroupStart + (c - 'A');
            if (c >= 'a' && c <= 'z') return AlphaGroupStart + (c - 'a');

            int row = Array.FindIndex(KanaRows, r => r.Contains(c));
            if (row >= 0) return row;

            if (char.IsLetterOrDigit(c)) return OtherGroup;
        }

        return OtherGroup;
    }

    public static List<RomFile> Order(IEnumerable<RomFile> games, SortMode mode, IReadOnlyList<RomFile>? defaultOrder = null)
    {
        return mode switch
        {
            // あ行〜わ行→数字→A〜Z→その他の順にグループを並べ、各グループの中はひらがな読み(無ければ表示タイトル)順。
            SortMode.AlphabeticalKana => games
                .OrderBy(g => GetKanaGroupIndex(g.SortKey))
                .ThenBy(g => g.SortKey, StringComparer.CurrentCulture)
                .ToList(),
            // 発売年が不明(0以下)のゲームは、先頭ではなく末尾へ回す。
            SortMode.ReleaseDate => games.OrderBy(g => g.ReleaseYear <= 0 ? 1 : 0).ThenBy(g => g.ReleaseYear).ToList(),
            SortMode.RecentlyPlayed => games.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue).ToList(),
            SortMode.PlayTimeDescending => games.OrderByDescending(g => g.PlayTimeMinutes).ToList(),
            _ => OrderByDefault(games, defaultOrder),
        };
    }

    private static List<RomFile> OrderByDefault(IEnumerable<RomFile> games, IReadOnlyList<RomFile>? defaultOrder)
    {
        if (defaultOrder is null)
        {
            // 呼び出し側がこの結果を見ながら元のコレクションをMoveするため、必ずコピーを返す。
            return games.ToList();
        }

        // RomFileは値の等価性を定義していない想定だが、念のため参照で比較する(同じNoを持つ別インスタンスが混ざっても誤って同一視しないように)。
        var indexByGame = new Dictionary<RomFile, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < defaultOrder.Count; i++)
        {
            indexByGame.TryAdd(defaultOrder[i], i);
        }

        return games
            .OrderBy(g => indexByGame.TryGetValue(g, out int index) ? index : int.MaxValue)
            .ToList();
    }
}
