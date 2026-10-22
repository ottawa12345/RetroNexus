using System.Text;
using System.Text.RegularExpressions;
using EmulatorLauncher.Models;
using EmulatorLauncher.Storage;

namespace EmulatorLauncher.RomLibrary;

/// <summary>複数枚組のゲーム(.m3u)の「まとめる/解除」の結果。</summary>
public sealed class DiscSetResult
{
    public bool Success { get; init; }

    /// <summary>失敗したときの理由(画面に出せる文章)。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>まとめたときに登録された.m3uのRomFile(解除では null)。</summary>
    public RomFile? Set { get; init; }

    /// <summary>解除したとき、元の1枚目のディスクのRomFile.No(解除後の一覧でフォーカスする先。まとめたときは null)。</summary>
    public int? FirstMemberNo { get; init; }

    public static DiscSetResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// 複数枚組のゲーム(.m3u)の扱い。
/// ・.m3uファイルを正とし、DB(RomDiscMember)には「どのRomFileがどの.m3uのメンバーか」を索引として持つ。
///   メンバーのディスクは一覧から隠し、.m3u自体を1本のゲームとして出す(RomFileRepositoryの一覧取得が除外する)。
/// ・.m3uは、ユーザーが画面から作る(<see cref="CreateAsync"/>)ほか、手で置いたものもスキャンで同じように登録される。
/// ・起動は.m3uのパスをそのままコアへ渡す(m3uを読めるかはコア次第。ゲーム中の入れ替えは既存のディスク交換を使う)。
/// 対応コンソールは<see cref="SupportedConsoles"/>(いまはPS1・サターン・PCエンジンCD-ROM²・ドリームキャストとMSX/MSX2とFM TOWNS(CDとフロッピーの混在も可)とX68000(フロッピーのみ)。増やすときはここと、rdblist.jsonの拡張子に".m3u"を足す)。
/// </summary>
public static class RomDiscSetService
{
    /// <summary>「複数枚をまとめる」を使えるコンソールのId。</summary>
    private static readonly HashSet<string> SupportedConsoles = new(StringComparer.OrdinalIgnoreCase) { "ps1", "stn", "pcecd", "dc", "msx", "msx2", "fmtowns", "x68000" };

    /// <summary>
    /// コンソールによっては、まとめられるファイルの種類を絞る(MSX/MSX2は、ROMやカセットテープ(.cas)ではなく、
    /// フロッピーのイメージ(.dsk)だけ。X68000もフロッピーのイメージだけ)。ここに無いコンソールは、ファイルの種類を問わない。
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> MemberExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msx"] = new(StringComparer.OrdinalIgnoreCase) { ".dsk" },
        ["msx2"] = new(StringComparer.OrdinalIgnoreCase) { ".dsk" },
        // X68000はフロッピーのイメージだけ(ハードディスク(.hdf)と、対象外の.cmdは除く)。
        ["x68000"] = new(StringComparer.OrdinalIgnoreCase) { ".dim", ".img", ".d88", ".88d", ".hdm", ".dup", ".2hd", ".xdf" },
    };

    /// <summary>メディアがフロッピーのコンソール(複数枚の表示に、光ディスクではなくフロッピーのアイコンを使う)。</summary>
    private static readonly HashSet<string> FloppyConsoles = new(StringComparer.OrdinalIgnoreCase) { "msx", "msx2", "x68000" };

    /// <summary>
    /// 「起動時に何枚目から始めるか」を選べないコンソール。コアがディスクコントロールを持たず、
    /// 入れ替えをコア内蔵の操作で行うなど、
    /// アプリ側からディスクを入れ替えられない。.m3uの並びが、そのままドライブ1/2へ入る。
    /// </summary>
    private static readonly HashSet<string> NoStartDiscChoice = new(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsStartDiscChoice(string consoleId) => !NoStartDiscChoice.Contains(consoleId);

    public static bool IsSupported(string consoleId) => SupportedConsoles.Contains(consoleId);

    public static bool IsFloppyMedia(string consoleId) => FloppyConsoles.Contains(consoleId);

    /// <summary>
    /// このゲームについて「複数枚(ディスク)」の操作(まとめる/解除)を出してよいか。
    /// すでにまとめたゲーム(.m3u)は常に出す(解除のため)。そうでなければ、対応コンソールで、
    /// ファイルの種類が合うものだけ(MSX/MSX2は.dsk)。
    /// </summary>
    public static bool CanGroup(RomFile game)
    {
        if (game.IsBiosBoot) return false;
        if (game.IsDiscSet) return true;
        if (!IsSupported(game.ConsoleId)) return false;
        return IsAllowedMember(game);
    }

    private static bool IsAllowedMember(RomFile file)
    {
        if (!MemberExtensions.TryGetValue(file.ConsoleId, out HashSet<string>? allowed)) return true;
        return allowed.Contains(Path.GetExtension(file.FileName));
    }

    // ======================================================================
    // .m3u の読み取り
    // ======================================================================

    /// <summary>.m3uの各行(メンバーのパス)を返す。空行・#で始まる行は除き、"ファイル|表示名"形式は"|"より前だけを取る。</summary>
    public static IReadOnlyList<string> ReadEntries(string m3uPath)
    {
        var entries = new List<string>();
        try
        {
            foreach (string raw in File.ReadAllLines(m3uPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                int bar = line.IndexOf('|');
                if (bar >= 0) line = line.Substring(0, bar).Trim();
                if (line.Length > 0) entries.Add(line);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomDiscSetService] .m3uを読めませんでした({m3uPath}): {ex.Message}");
        }

        return entries;
    }

    /// <summary>.m3uの各行を、フルパスにして返す(相対パスは.m3uのあるフォルダ基準)。ファイルが実在するかは見ない。</summary>
    public static IReadOnlyList<string> ResolveMemberPaths(string m3uPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(m3uPath)) ?? string.Empty;
        var paths = new List<string>();
        foreach (string entry in ReadEntries(m3uPath))
        {
            try
            {
                string relative = entry.Replace('\\', '/');
                if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative.Substring(2);
                paths.Add(Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar))));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RomDiscSetService] パスを解釈できませんでした({entry}): {ex.Message}");
            }
        }

        return paths;
    }

    /// <summary>.m3uに書かれたディスクのうち、実在しないファイルのパス(起動前の確認用)。すべてあれば空。</summary>
    public static IReadOnlyList<string> FindMissingMembers(string m3uPath)
        => ResolveMemberPaths(m3uPath).Where(p => !File.Exists(p)).ToList();

    // ======================================================================
    // メンバー対応表(DB)の同期
    // ======================================================================

    /// <summary>
    /// 登録済みフォルダ内の.m3u(RomFile)ごとに、中身(メンバー)を読んでRomDiscMemberを合わせる。
    /// メンバーは、同じフォルダの登録済みRomFile(フルパスが一致するもの)から探す。見つからない行は飛ばす
    /// (ディスクがまだ登録されていない・ファイルが無い等。次のスキャンで拾う)。
    /// スキャンの終わり(ScanFolderContentsAsync)と、画面からまとめた直後に呼ぶ。何度呼んでも結果は同じ。
    /// </summary>
    public static async Task SyncFolderAsync(int folderNo, string folderPath)
    {
        try
        {
            IReadOnlyList<RomFile> files = await RomFileRepository.GetByParentIdAsync(folderNo).ConfigureAwait(false);
            List<RomFile> sets = files.Where(file => file.IsDiscSet).ToList();

            if (sets.Count > 0)
            {
                var byFullPath = new Dictionary<string, RomFile>(StringComparer.OrdinalIgnoreCase);
                foreach (RomFile rom in files)
                {
                    if (rom.IsDiscSet) continue;
                    string full = Path.GetFullPath(Path.Combine(folderPath, rom.FileName.Replace('/', Path.DirectorySeparatorChar)));
                    byFullPath[full] = rom;
                }

                foreach (RomFile set in sets)
                {
                    string m3uPath = Path.Combine(folderPath, set.FileName.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(m3uPath)) continue;

                    var memberNos = new List<int>();
                    foreach (string memberPath in ResolveMemberPaths(m3uPath))
                    {
                        if (byFullPath.TryGetValue(memberPath, out RomFile? member) && member.No != set.No)
                        {
                            memberNos.Add(member.No);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[RomDiscSetService] {set.FileName}: メンバーが登録されていません: {memberPath}");
                        }
                    }

                    await RomDiscSetRepository.ReplaceMembersAsync(set.No, memberNos).ConfigureAwait(false);
                }
            }

            await RomDiscSetRepository.PurgeOrphansAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomDiscSetService] メンバー対応表の更新に失敗しました: {ex.Message}");
        }
    }

    // ======================================================================
    // 画面からの「まとめる」「解除」
    // ======================================================================

    /// <summary>
    /// ディスク名に付く「(Disc 1)」「[CD2]」「(Disk 1 of 2)」「 - Disc 1」を取り除いた名前(まとめたときの.m3uの名前の候補)。
    /// 取り除くと空になるときは、元の名前のまま返す。
    /// </summary>
    public static string SuggestName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName.Replace('/', Path.DirectorySeparatorChar));
        string name = Regex.Replace(stem, @"\s*[\(\[（]\s*(?:Disc|Disk|CD|ディスク)\s*\d+(?:\s*(?:of|/)\s*\d+)?\s*[\)\]）]", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        name = Regex.Replace(name, @"\s*[-_]\s*(?:Disc|Disk|CD|ディスク)\s*\d+\s*$", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        name = name.Trim();
        return name.Length > 0 ? name : stem;
    }

    /// <summary>
    /// 「まとめる」で2枚目以降に選べるゲーム。同じフォルダ・同じコンソールで、複数枚組(.m3u)でも、
    /// すでに別の.m3uのメンバーでもないもの。ディスクの名前が1枚目と同じ系統のものを先に、あとはファイル名順。
    /// </summary>
    public static async Task<IReadOnlyList<RomFile>> GetCandidatesAsync(RomFile game)
    {
        IReadOnlyList<RomFile> files = await RomFileRepository.GetByParentIdAsync(game.ParentId).ConfigureAwait(false);
        HashSet<int> members = await RomDiscSetRepository.GetActiveMemberNosAsync().ConfigureAwait(false);
        string baseName = SuggestName(game.FileName);

        return files
            .Where(f => f.No != game.No && !f.IsDiscSet && !f.IsBiosBoot && IsAllowedMember(f)
                && string.Equals(f.ConsoleId, game.ConsoleId, StringComparison.OrdinalIgnoreCase)
                && !members.Contains(f.No))
            .OrderBy(f => string.Equals(SuggestName(f.FileName), baseName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// <paramref name="first"/>(1枚目)と<paramref name="others"/>(2枚目以降、選んだ順)をまとめる。
    /// 1枚目と同じフォルダへ「(名前).m3u」を書き出し、そのフォルダを更新して.m3uをゲームとして登録し、
    /// メンバーの対応表を作る(メンバーは以後、一覧から隠れる)。
    /// 名前は<see cref="SuggestName"/>。同じ名前のファイルが既にあるときは「 (複数枚)」「 (複数枚2)」…を付ける。
    /// </summary>
    public static async Task<DiscSetResult> CreateAsync(RomFile first, IReadOnlyList<RomFile> others)
    {
        if (others.Count == 0) return DiscSetResult.Fail("2枚以上を選んでください。");
        if (!IsSupported(first.ConsoleId) || !IsAllowedMember(first))
        {
            return DiscSetResult.Fail("このコンソール(またはこの種類のファイル)では、複数枚のまとめに対応していません。");
        }

        var members = new List<RomFile> { first };
        members.AddRange(others);
        if (members.Any(m => m.ParentId != first.ParentId))
        {
            return DiscSetResult.Fail("同じフォルダのゲームだけをまとめられます。");
        }

        RomFolder? folder = await RomFolderRepository.GetAsync(first.ParentId).ConfigureAwait(false);
        if (folder is null || !Directory.Exists(folder.Path))
        {
            return DiscSetResult.Fail("ゲームのフォルダが見つかりません。");
        }

        IReadOnlyList<RomFile> existing = await RomFileRepository.GetByParentIdAsync(first.ParentId, includeDeleted: true).ConfigureAwait(false);
        string stem = SanitizeFileName(SuggestName(first.FileName));
        string? fileName = null;
        for (int i = 0; i < 10 && fileName is null; i++)
        {
            string candidate = stem + (i == 0 ? string.Empty : i == 1 ? " (複数枚)" : $" (複数枚{i})") + ".m3u";
            bool inDb = existing.Any(f => string.Equals(f.FileName, candidate, StringComparison.OrdinalIgnoreCase));
            if (!inDb && !File.Exists(Path.Combine(folder.Path, candidate))) fileName = candidate;
        }

        if (fileName is null) return DiscSetResult.Fail("同じ名前の.m3uが多すぎて、新しい名前を決められません。");

        string m3uPath = Path.Combine(folder.Path, fileName);
        try
        {
            string text = string.Join("\n", members.Select(m => m.FileName.Replace('\\', '/'))) + "\n";
            await File.WriteAllTextAsync(m3uPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return DiscSetResult.Fail($".m3uを書き出せませんでした: {ex.Message}");
        }

        RomFolderRefreshResult refresh = await RomFolderScanService.RefreshFolderAsync(folder.No).ConfigureAwait(false);
        IReadOnlyList<RomFile> after = await RomFileRepository.GetByParentIdAsync(folder.No).ConfigureAwait(false);
        RomFile? set = after.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (set is null)
        {
            TryDelete(m3uPath);
            return DiscSetResult.Fail("まとめたゲームを登録できませんでした。" + (string.IsNullOrEmpty(refresh.Message) ? string.Empty : "\n" + refresh.Message));
        }

        await SyncFolderAsync(folder.No, folder.Path).ConfigureAwait(false);
        return new DiscSetResult { Success = true, Set = set };
    }

    /// <summary>
    /// 複数枚のまとめを解除する。.m3uファイルを削除し、メンバーの対応表と.m3uの登録を消す
    /// (メンバーのディスクは一覧に戻る。ディスクのファイルは消さない)。
    /// </summary>
    public static async Task<DiscSetResult> RemoveAsync(RomFile set)
    {
        if (!set.IsDiscSet) return DiscSetResult.Fail("複数枚のまとめではありません。");

        RomFolder? folder = await RomFolderRepository.GetAsync(set.ParentId).ConfigureAwait(false);
        if (folder is not null)
        {
            string m3uPath = Path.Combine(folder.Path, set.FileName.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(m3uPath)) File.Delete(m3uPath);
            }
            catch (Exception ex)
            {
                return DiscSetResult.Fail($".m3uを削除できませんでした: {ex.Message}");
            }
        }

        IReadOnlyList<int> memberNos = await RomDiscSetRepository.GetMemberNosAsync(set.No).ConfigureAwait(false);
        await RomDiscSetRepository.DeleteSetAsync(set.No).ConfigureAwait(false);
        await RomCollectionRepository.RemoveRomFilesFromAllCollectionsAsync(new[] { set.No }).ConfigureAwait(false);
        await RomFileRepository.DeleteAsync(set.No).ConfigureAwait(false);
        return new DiscSetResult { Success = true, FirstMemberNo = memberNos.Count > 0 ? memberNos[0] : null };
    }

    /// <summary>
    /// .m3uに書かれたディスクの表示名(拡張子を除いたファイル名)を、書かれた順(=コアのディスク番号の順)に返す。
    /// 起動時にどのディスクから始めるかを選ぶ一覧に使う。読めなければ空。
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetDiscNamesAsync(RomFile set)
    {
        if (!set.IsDiscSet) return Array.Empty<string>();

        RomFolder? folder = await RomFolderRepository.GetAsync(set.ParentId).ConfigureAwait(false);
        if (folder is null) return Array.Empty<string>();

        string m3uPath = Path.Combine(folder.Path, set.FileName.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(m3uPath)) return Array.Empty<string>();

        return ReadEntries(m3uPath)
            .Select(entry => Path.GetFileNameWithoutExtension(entry.Replace('\\', '/')))
            .ToList();
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length > 0 ? name : "複数枚ゲーム";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 消せなくても、次回のスキャンで.m3uとして拾われるだけ。
        }
    }
}
