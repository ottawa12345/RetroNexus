using System.Text.Json;

namespace EmulatorLauncher.Models;

/// <summary>
/// レイアウト確認用のダミーデータ。GetGamesは現在GameSelectPage/GamePlayPageからは
/// 使われておらず(RomFileRepository経由でDBから読み込む方式に置き換えたため)、
/// 参考用に残してある(GetCollections/ResolveCollectionGamesは、コレクション機能が
/// Data.RomCollectionRepository経由の実データに置き換わったため削除済み)。
/// コンソール一覧(GetConsoles)とlibretroコア一覧(GetLaunchers)は、アプリ実行ファイルと
/// 同じ場所に置く Consoles.json / Launchers.json から読み込む(LoadConsoles/LoadLaunchers参照。
/// これは現役)。以前はこの2つが1つのGameLauncher.json(トップレベルにconsoles/launchersの
/// 2配列)にまとまっていたが、それぞれ独立して管理・編集しやすいようファイルを分離した
/// (中身のConsoleInfo/LauncherInfoの構造自体は変えていない)。
///
/// CorePath について:
/// GamePlayPage はゲーム開始時、選択された RomFile.ConsoleId から逆引きした
/// ConsoleInfo(Id, LauncherName)に一致する LauncherInfo.CorePath
/// (<see cref="ResolveLauncher"/> が引く)をそのまま EmulationHost のコンストラクタへ渡す。
/// Launchers.json 側にはファイル名のみを設定し、実際のパス解決
/// (実行ファイル基準の cores フォルダを基準にする)は <see cref="ResolveCorePath"/> が行う。
/// ROM本体のパスは RomFile.FileName + 親RomFolder.Path から
/// <see cref="Data.RomFileRepository.ResolveFullPathAsync"/> が組み立てる。
/// 実ファイルを配置していない場合、EmulationHost.Start() は例外で失敗するが、
/// GamePlayPage 側でその失敗を捕捉し状態表示するため、アプリ自体はクラッシュしない。
/// </summary>
public static class SampleData
{
    private const string ConsolesConfigFileName = "Consoles.json";
    private const string LaunchersConfigFileName = "Launchers.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 手で編集したJSONの末尾カンマやコメントで、一覧が丸ごと空になるのを防ぐ
        // (設定画面側の書き込み処理 LauncherConfigService.LoadArray と同じ許容範囲に揃える)。
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// アプリ実行ファイルと同じ場所の Consoles.json(ConsoleInfoのトップレベル配列)を
    /// 読み込んで返す。ファイルが無い/JSONとして壊れている場合は、CoreSettingsStore.Load等と
    /// 同じ方針で空データ(0件)として扱い、例外を投げずにアプリの起動自体は継続させる。
    /// GetConsolesから呼ばれるたびに読み直すので、ファイルをアプリの外から書き換えれば
    /// 次回呼び出し以降に反映される(実行中のキャッシュは持たない)。
    /// </summary>
    private static List<ConsoleInfo> LoadConsoles()
    {
        try
        {
            string path = EmulatorLauncher.Services.UserConfigFiles.ConsolesPath;
            string json = File.ReadAllText(path);
            List<ConsoleInfo> consoles = JsonSerializer.Deserialize<List<ConsoleInfo>>(json, JsonOptions) ?? new List<ConsoleInfo>();

            // iconPath/logoPathは"./Images/Console/famicom.png"のような、実行フォルダ基準の
            // 相対パスで書ける。画面側(new Uri(path))では絶対パスが必要なので、ここで解決する。
            foreach (ConsoleInfo console in consoles)
            {
                console.IconPath = ResolveImagePath(console.IconPath);
                console.LogoPath = ResolveImagePath(console.LogoPath);

                // ヒーロー画像は、ファイルが実在するときだけ使う(無ければ空にして、画面側がアイコンで代用する)。
                string hero = ResolveImagePath(console.HeroPath);
                console.HeroPath = !string.IsNullOrEmpty(hero) && (hero.Contains("://", StringComparison.Ordinal) || File.Exists(hero))
                    ? hero
                    : string.Empty;

                // ダーク用アイコンも同様に、ファイルが実在するときだけ使う(無ければ標準のアイコンで代用)。
                string darkIcon = ResolveImagePath(console.DarkIconPath);
                console.DarkIconPath = !string.IsNullOrEmpty(darkIcon) && (darkIcon.Contains("://", StringComparison.Ordinal) || File.Exists(darkIcon))
                    ? darkIcon
                    : string.Empty;
            }

            return consoles;
        }
        catch (Exception ex)
        {
            // ファイル未配置/壊れたJSON等は「コンソール0件」として扱う(起動自体は継続できるようにする)。
            // 毎回読み直す処理なので、動作ログへは最初の1回だけ書く。
            EmulatorLauncher.Services.AppLog.WarnOnce("Consoles.json", "Data", $"{ConsolesConfigFileName} を読み込めませんでした(コンソール0件として扱います)", ex);
            return new List<ConsoleInfo>();
        }
    }

    /// <summary>
    /// Consoles.jsonの画像パスを画面で使える形へ解決する。"./Images/Console/xx.png"のような
    /// 相対パスは実行フォルダ基準の絶対パスへ、"ms-appx:///..."等のURIや絶対パス、空文字は
    /// そのまま返す。
    /// </summary>
    public static string ResolveImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(path)) return path;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// アプリ実行ファイルと同じ場所の Launchers.json(LauncherInfoのトップレベル配列)を
    /// 読み込んで返す。読み込み方針はLoadConsolesと同じ(壊れていれば0件扱い、毎回読み直す)。
    /// </summary>
    private static List<LauncherInfo> LoadLaunchers()
    {
        try
        {
            string path = EmulatorLauncher.Services.UserConfigFiles.LaunchersPath;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<LauncherInfo>>(json, JsonOptions) ?? new List<LauncherInfo>();
        }
        catch (Exception ex)
        {
            // ファイル未配置/壊れたJSON等は「コア0件」として扱う(起動自体は継続できるようにする)。
            EmulatorLauncher.Services.AppLog.WarnOnce("Launchers.json", "Data", $"{LaunchersConfigFileName} を読み込めませんでした(ランチャー0件として扱います)", ex);
            return new List<LauncherInfo>();
        }
    }

    public static List<ConsoleInfo> GetConsoles() => LoadConsoles();

    /// <summary>
    /// 選択可能なlibretroコア(ランチャー)のフラットな一覧。ConsoleInfo.Launcher* のような
    /// 入れ子ではなく、Id(=ConsoleInfo.Id)ごとに複数件を持てる独立した一覧として持つことで、
    /// 「同じコンソール(例: sfc)に対して複数のコアを用意し、画面上でユーザーに選び直させる」
    /// という拡張がしやすいようにしてある。Launchers.json のトップレベル配列に、同じIdの
    /// エントリを追加するだけで選択肢を増やせる。
    /// </summary>
    public static List<LauncherInfo> GetLaunchers() => LoadLaunchers();

    /// <summary>
    /// ConsoleInfo(Id, LauncherName)から、実際に使うLauncherInfoをGetLaunchers()の一覧から
    /// 引く。同じIdのLauncherInfoが複数あっても、Nameが一致するもの1件だけが選ばれる。
    /// 一致するものが無い場合(設定が壊れている/コアが削除された等)はnullを返す。
    /// </summary>
    public static LauncherInfo? ResolveLauncher(string consoleId, string launcherName)
        => GetLaunchers().FirstOrDefault(l => l.Id == consoleId && l.Name == launcherName);

    /// <summary>
    /// LauncherInfo.CorePath / GameInfo.RomPath がファイル名のみ(相対パス)の場合に、
    /// アプリ実行ファイルと同じ場所を基準にした絶対パスへ解決する。
    /// すでに絶対パス(ドライブレター始まり等)が指定されている場合はそのまま返す。
    /// </summary>
    public static string ResolveCorePath(string corePath)
    {
        if (string.IsNullOrEmpty(corePath)) return corePath;
        if (Path.IsPathRooted(corePath)) return corePath;
        return Path.Combine(AppContext.BaseDirectory, "cores", corePath);
    }

    /// <summary>
    /// 外部ランチャーの実行ファイルパスを解決する。環境変数を展開した上で、
    /// 絶対パスならそのまま、ディレクトリ区切りを含む相対パスはアプリ実行ファイル基準、
    /// 区切りを含まない名前は「アプリ実行ファイル基準に存在すればそれ、無ければPATH検索に任せて
    /// そのまま」とする。
    /// </summary>
    public static string ResolveExePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return string.Empty;
        string expanded = Environment.ExpandEnvironmentVariables(exePath.Trim().Trim('"'));
        if (Path.IsPathRooted(expanded)) return expanded;

        string underBase = Path.Combine(AppContext.BaseDirectory, expanded);
        bool hasSeparator = expanded.IndexOfAny(new[] { '\\', '/' }) >= 0;
        if (hasSeparator || File.Exists(underBase)) return underBase;
        return expanded; // PATH上のコマンド名
    }

    /// <summary>外部ランチャーの作業フォルダを解決する。未指定なら実行ファイルのあるフォルダ(不明ならnull)。</summary>
    public static string? ResolveWorkingPath(string workingPath, string resolvedExePath)
    {
        if (!string.IsNullOrWhiteSpace(workingPath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(workingPath.Trim().Trim('"'));
            return Path.IsPathRooted(expanded) ? expanded : Path.Combine(AppContext.BaseDirectory, expanded);
        }

        return Path.IsPathRooted(resolvedExePath) ? Path.GetDirectoryName(resolvedExePath) : null;
    }

    /// <summary>
    /// <see cref="LauncherInfo.Args"/> のプレースホルダー({rom}/{romdir}/{romname})を展開する。
    /// {rom} を含まない場合は末尾に引用符付きでROMのフルパスを追加する。
    /// </summary>
    public static string BuildExternalArguments(string args, string romPath)
    {
        args ??= string.Empty;
        bool hasRom = args.Contains("{rom}", StringComparison.OrdinalIgnoreCase);

        string result = args
            .Replace("{romdir}", Path.GetDirectoryName(romPath) ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{romname}", Path.GetFileNameWithoutExtension(romPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{rom}", romPath, StringComparison.OrdinalIgnoreCase);

        if (!hasRom)
        {
            result = (result + " \"" + romPath + "\"").TrimStart();
        }

        return Environment.ExpandEnvironmentVariables(result);
    }

    /// <summary>
    /// ROMファイルのパス解決。"roms\&lt;consoleId&gt;\&lt;ファイル名&gt;" を基準にする。
    /// </summary>
    public static string ResolveRomPath(string consoleId, string romPath)
    {
        if (string.IsNullOrEmpty(romPath)) return romPath;
        if (Path.IsPathRooted(romPath)) return romPath;
        return Path.Combine(AppContext.BaseDirectory, "roms", consoleId, romPath);
    }

    public static List<GameInfo> GetGames(string consoleId)
    {
        var titles = consoleId switch
        {
            "fc" => new[] { "デンパの森", "スペースレーサーZ", "パズルナイト", "ロックマスターズ", "銀河騎士団", "森のパン屋さん" },
            "sfc" => new[] { "光と影のクロニクル", "レジェンド・オブ・スカイ", "超銀河サッカー", "妖精の学園", "鋼鉄戦隊エクス", "夢幻のダンジョン" },
            "md" => new[] { "サンダーブレイズ", "メタルファイター翔", "ソニックとは無関係のハリネズミ", "熱風カーレース" },
            "pce" => new[] { "月光剣士伝", "レインボーアイランズ紀行", "スカイキャプテン" },
            "gb" => new[] { "ポケットモンスター(仮)风の街", "ミニ冒険記", "テトリスもどき" },
            _ => new[] { "サンプルゲーム" },
        };

        var accentPalette = new[] { "#455A64", "#5D4037", "#00838F", "#6D4C41", "#8E24AA", "#3949AB" };

        var games = new List<GameInfo>();
        for (int i = 0; i < titles.Length; i++)
        {
            games.Add(new GameInfo
            {
                Id = $"{consoleId}-{i:00}",
                ConsoleId = consoleId,
                Title = titles[i],
                Publisher = "架空ソフト",
                ReleaseYear = 1990 + i,
                Players = i % 3 == 0 ? "1〜2人用" : "1人用",
                Description = $"「{titles[i]}」は{1990 + i}年に発売されたアクションアドベンチャーゲームです。" +
                               "主人公を操作し、各地を冒険しながら謎を解いていきます。",
                AccentColorHex = accentPalette[i % accentPalette.Length],
                LastPlayed = i % 2 == 0 ? DateTimeOffset.Now.AddDays(-i) : null,
                PlayTimeMinutes = (i * 37) % 500,
                // 実ファイルは同梱していないダミーパス。roms\<consoleId>\ 以下に
                // 同名ファイルを置けば ResolveRomPath() 経由でそのまま起動できる。
                RomPath = $"{consoleId}-{i:00}.rom",
            });
        }


        if (consoleId == "n64")
        {
            games.Clear();

            // ダミーデータを配列で定義しておくと追加・管理が楽になります
            var n64Games = new[]
            {
        new { Title = "ゼルダの伝説 時のオカリナ", Year = 1998, Icon = "zeldaoca.png" },
        new { Title = "スーパーマリオ64", Year = 1996, Icon = "mario64.png" },
        new { Title = "スターフォックス64", Year = 1997, Icon = "starfox64.png" }
    };

            for (int i = 0; i < n64Games.Length; i++)
            {
                var item = n64Games[i];
                games.Add(new GameInfo
                {
                    Id = $"{consoleId}-{i:00}",
                    ConsoleId = consoleId,
                    Title = item.Title,
                    Publisher = "任天堂",
                    ReleaseYear = item.Year,
                    Players = "1人用",
                    Description = $"「{item.Title}」は{item.Year}年に発売されたアクションアドベンチャーゲームです。" +
                                   "主人公を操作し、各地を冒険しながら謎を解いていきます。",
                    LastPlayed = i % 2 == 0 ? DateTimeOffset.Now.AddDays(-i) : null,
                    PlayTimeMinutes = ((i + 1) * 37) % 500, // 0分回避のため (i + 1)
                    IconPath = $"ms-appx:///Assets/{item.Icon}",
                    RomPath = $"E:\\Roms\\N64\\Zelda no Densetsu - Toki no Ocarina (Japan) (Rev B).z64",
                });
            }
        }

        if (consoleId == "fc")
        {
            games.Clear();

            // ダミーデータを配列で定義しておくと追加・管理が楽になります
            var fcGames = new[]
            {
        new { Title = "ゼルダの伝説 時のオカリナ", Year = 1998, Icon = "zeldaoca.png" },
        new { Title = "スーパーマリオ64", Year = 1996, Icon = "mario64.png" },
        new { Title = "スターフォックス64", Year = 1997, Icon = "starfox64.png" }
    };

            for (int i = 0; i < fcGames.Length; i++)
            {
                var item = fcGames[i];
                games.Add(new GameInfo
                {
                    Id = $"{consoleId}-{i:00}",
                    ConsoleId = consoleId,
                    Title = item.Title,
                    Publisher = "任天堂",
                    ReleaseYear = item.Year,
                    Players = "1人用",
                    Description = $"「{item.Title}」は{item.Year}年に発売されたアクションアドベンチャーゲームです。" +
                                   "主人公を操作し、各地を冒険しながら謎を解いていきます。",
                    LastPlayed = i % 2 == 0 ? DateTimeOffset.Now.AddDays(-i) : null,
                    PlayTimeMinutes = ((i + 1) * 37) % 500, // 0分回避のため (i + 1)
                    IconPath = $"ms-appx:///Assets/{item.Icon}",
                    RomPath = $"E:\\Roms\\NES\\roms\\Super Mario Bros 3 (J).zip",
                });
            }
        }

        if (consoleId == "pce")
        {
            games.Clear();

            // ダミーデータを配列で定義しておくと追加・管理が楽になります
            var fcGames = new[]
            {
        new { Title = "ゼルダの伝説 時のオカリナ", Year = 1998, Icon = "zeldaoca.png" },
        new { Title = "スーパーマリオ64", Year = 1996, Icon = "mario64.png" },
        new { Title = "スターフォックス64", Year = 1997, Icon = "starfox64.png" }
    };

            for (int i = 0; i < fcGames.Length; i++)
            {
                var item = fcGames[i];
                games.Add(new GameInfo
                {
                    Id = $"{consoleId}-{i:00}",
                    ConsoleId = consoleId,
                    Title = item.Title,
                    Publisher = "任天堂",
                    ReleaseYear = item.Year,
                    Players = "1人用",
                    Description = $"「{item.Title}」は{item.Year}年に発売されたアクションアドベンチャーゲームです。" +
                                   "主人公を操作し、各地を冒険しながら謎を解いていきます。",
                    LastPlayed = i % 2 == 0 ? DateTimeOffset.Now.AddDays(-i) : null,
                    PlayTimeMinutes = ((i + 1) * 37) % 500, // 0分回避のため (i + 1)
                    IconPath = $"ms-appx:///Assets/{item.Icon}",
                    RomPath = $"E:\\Roms\\pce\\R-Type Part-1 (Japan).zip",
                });
            }
        }


        if (consoleId == "sfc")
        {
            games.Clear();

            // ダミーデータを配列で定義しておくと追加・管理が楽になります
            var fcGames = new[]
            {
        new { Title = "ゼルダの伝説 時のオカリナ", Year = 1998, Icon = "zeldaoca.png" },
        new { Title = "スーパーマリオ64", Year = 1996, Icon = "mario64.png" },
        new { Title = "スターフォックス64", Year = 1997, Icon = "starfox64.png" }
    };

            for (int i = 0; i < fcGames.Length; i++)
            {
                var item = fcGames[i];
                games.Add(new GameInfo
                {
                    Id = $"{consoleId}-{i:00}",
                    ConsoleId = consoleId,
                    Title = item.Title,
                    Publisher = "任天堂",
                    ReleaseYear = item.Year,
                    Players = "1人用",
                    Description = $"「{item.Title}」は{item.Year}年に発売されたアクションアドベンチャーゲームです。" +
                                   "主人公を操作し、各地を冒険しながら謎を解いていきます。",
                    LastPlayed = i % 2 == 0 ? DateTimeOffset.Now.AddDays(-i) : null,
                    PlayTimeMinutes = ((i + 1) * 37) % 500, // 0分回避のため (i + 1)
                    IconPath = $"ms-appx:///Assets/{item.Icon}",
                    RomPath = $"E:\\Roms\\SNES\\Super Mario World - Super Mario Bros. 4 (Japan).zip",
                });
            }
        }

        return games;
    }

    // 注意: 以前ここに GetSaveSlots()(ダミーのSaveSlotInfoリスト)があったが、
    // SaveSlotInfo は Microsoft.UI.Xaml.Media.ImageSource を持つためEmulatorLauncher.Core側には
    // 置けない(exeプロジェクト側のModels/SaveSlotInfo.csに残置)。またこのメソッド自体、
    // 呼び出し元が無い参考用コードだった(実際のセーブスロット一覧はGamePlayPage.xaml.csが
    // EmulationHost.GetSaveSlots()から取得している)ため、Core分離を機に削除した。

    public static List<PauseMenuItem> GetPauseMenuItems() => new()
    {
        new PauseMenuItem { Text = "つづける",           Action = PauseMenuAction.Continue },
        new PauseMenuItem { Text = "セーブ",             Action = PauseMenuAction.Save },
        new PauseMenuItem { Text = "ロード",             Action = PauseMenuAction.Load },
        new PauseMenuItem { Text = "設定",             Action = PauseMenuAction.OpenSettings },
        new PauseMenuItem { Text = "操作方法の確認",   Action = PauseMenuAction.ShowControls },
        new PauseMenuItem { Text = "リセット",           Action = PauseMenuAction.Reset },
        new PauseMenuItem { Text = "タイトル選択にもどる", Action = PauseMenuAction.BackToTitleSelect },
    };

    /// <summary>
    /// ソートオーバーレイの選択肢。「お好きな順」は廃止し、既定(先頭)は「あいうえお順」にしてある
    /// (GameSelectPageの初期表示もこれに合わせてあいうえお順で読み込む。GameListHelper.Order参照)。
    /// </summary>
    public static List<SortOption> GetSortOptions() => new()
    {
        new SortOption { Text = "あいうえお順",     Mode = SortMode.AlphabeticalKana },
        new SortOption { Text = "発売された順",     Mode = SortMode.ReleaseDate },
        new SortOption { Text = "最近遊んだ順",     Mode = SortMode.RecentlyPlayed },
        new SortOption { Text = "プレイ時間が長い順", Mode = SortMode.PlayTimeDescending },
    };

    /// <summary>
    /// コンソール選択画面の「コレクション」タブは、Data.RomCollectionRepository経由で
    /// 実データ(RomCollection/RomCollectionItemテーブル)を読み込むようになったため、
    /// ここにあった旧ダミー実装(GetCollections/ResolveCollectionGames)は削除した。
    /// </summary>
}