using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmulatorLauncher.Input;

/// <summary>
/// コントローラー設定画面(Views.ControllerSettingsPage)が扱う設定を
/// data/controllers.jsonへ読み書きする。ボタン割り当てと呼び出し操作(ホットキー)は
/// コア毎ではなくコンソール毎(Consoles.jsonのid)に持つ。
///
/// ・SwapLauncherConfirmCancel: このランチャー自身のメニュー操作(コンソール選択・
///   ゲーム選択・設定画面等のUIナビゲーション)で、物理ボタンのA/Bを入れ替えて
///   「決定」「戻る/キャンセル」として扱うかどうか。既定はfalse(標準: 右のボタン(PSの○/Switchの A)=決定、
///   下のボタン(PSの×)=キャンセル。trueで下のボタン=決定・右のボタン=キャンセルに入れ替える)。MainWindowが実際のSDL入力をIControllerPageへ中継する前に
///   この設定を見て、必要なら物理A/Bを入れ替えてから渡す。
///   (X/Y等はメニュー画面ごとに個別の用途で既に使われているため、衝突を避けるべく
///   入れ替え対象はA/Bの2つだけに絞っている)。
/// ・DefaultMapping: RetroPad(A/B/X/Y/L/R/Start/Select)の、全コンソール共通の下地になる物理ボタン割り当て。
///   既定値は、このコントローラー設定画面を作る前からEmulationHostにハードコードされていた挙動
///   (Nintendo配列: 物理EAST→RetroPad A、物理SOUTH→RetroPad B等)とそのまま同じにしてある。
///   画面からは編集せず、コンソール毎の設定(Consoles)がこの上に重なる。
/// ・Consoles: コンソールID→そのコンソールで変更したボタン割り当て(RetroPadのキー→物理ボタン)と
///   ホットキー(中断メニュー/巻き戻し/早送り)の組み合わせ。変更した項目だけを持つ。
///   以前のコア毎の設定(CoreSettings.ButtonMapping)は廃止し、ゲーム起動時には使われない。
/// </summary>
public static class ControllerMappingService
{
    /// <summary>RetroPad側の設定可能なボタン名(表示順もこの並びに合わせる)。</summary>
    public static readonly string[] RetroPadButtonKeys = { "A", "B", "X", "Y", "L", "R", "Start", "Select" };

    /// <summary>
    /// コンソール別のコントローラー図で割り当てを変えられるRetroPad側のキー全部
    /// (RetroPadButtonKeysに、L2/R2/L3/R3を足したもの)。
    /// </summary>
    public static readonly string[] AllRetroPadKeys = { "A", "B", "X", "Y", "L", "R", "L2", "R2", "L3", "R3", "Start", "Select" };

    // ---- 方向入力(十字キー・アナログスティック)の割り当て先 ----
    // 通常のボタンと同じ「RetroPadのキー → 物理入力」の形で保存する(既定は同名の物理入力。
    // 例: "Up"→DPadUp、"LSUp"→LeftStickUp)。"Up".."Right"はRETRO_DEVICE_JOYPADの方向、
    // "LS*"/"RS*"は左右スティックのアナログ値(コアがRETRO_DEVICE_ANALOGで読むとき)になる。
    public static readonly string[] DirectionKeys =
    {
        "Up", "Down", "Left", "Right",
        "LSUp", "LSDown", "LSLeft", "LSRight",
        "RSUp", "RSDown", "RSLeft", "RSRight",
    };

    /// <summary>ボタンと方向入力を合わせた、割り当て可能なキー全部。</summary>
    public static readonly string[] AllMappingKeys = AllRetroPadKeys.Concat(DirectionKeys).ToArray();

    public static bool IsDirectionKey(string key) => Array.IndexOf(DirectionKeys, key) >= 0;

    /// <summary>方向入力のキーの表示名(例: "十字キー↑"、"左スティック↑")。</summary>
    public static string GetDirectionKeyLabel(string key) => key switch
    {
        "Up" => "十字キー↑", "Down" => "十字キー↓", "Left" => "十字キー←", "Right" => "十字キー→",
        "LSUp" => "左スティック↑", "LSDown" => "左スティック↓", "LSLeft" => "左スティック←", "LSRight" => "左スティック→",
        "RSUp" => "右スティック↑", "RSDown" => "右スティック↓", "RSLeft" => "右スティック←", "RSRight" => "右スティック→",
        _ => key,
    };

    /// <summary>コントローラー設定画面に並べる、方向の物理入力(十字キー4方向、左右スティック各4方向)。</summary>
    public static readonly GamepadButton[] AssignableDirectionInputs =
    {
        GamepadButton.DPadUp, GamepadButton.DPadDown, GamepadButton.DPadLeft, GamepadButton.DPadRight,
        GamepadButton.LeftStickUp, GamepadButton.LeftStickDown, GamepadButton.LeftStickLeft, GamepadButton.LeftStickRight,
        GamepadButton.RightStickUp, GamepadButton.RightStickDown, GamepadButton.RightStickLeft, GamepadButton.RightStickRight,
    };

    // ---- ホットキー(ゲーム中の呼び出し操作) ----
    public const string HotkeyMenu = "Menu";
    public const string HotkeyRewind = "Rewind";
    public const string HotkeyFastForward = "FastForward";
    public const string HotkeyFastForwardReverse = "FastForwardReverse";
    public const string HotkeyPointerRecenter = "PointerRecenter";

    /// <summary>設定画面に並べるホットキーの順。</summary>
    // 「早送り(遅く)」は廃止した(設定画面に出さず、ゲーム中も使わない)。
    public static readonly string[] HotkeyKeys = { HotkeyMenu, HotkeyRewind, HotkeyFastForward, HotkeyPointerRecenter };

    /// <summary>ホットキーに割り当てられる物理ボタン(十字キーは除く)。</summary>
    public static readonly GamepadButton[] AssignableHotkeyButtons =
    {
        GamepadButton.A, GamepadButton.B, GamepadButton.X, GamepadButton.Y,
        GamepadButton.LeftShoulder, GamepadButton.RightShoulder,
        GamepadButton.LeftTrigger, GamepadButton.RightTrigger,
        GamepadButton.LeftThumbstickButton, GamepadButton.RightThumbstickButton,
        GamepadButton.Menu, GamepadButton.View,
    };

    public static string GetHotkeyDisplayName(string key) => key switch
    {
        HotkeyMenu => "中断メニュー",
        HotkeyRewind => "巻き戻し",
        HotkeyFastForward => "早送り",
        HotkeyFastForwardReverse => "早送り(遅く)",
        HotkeyPointerRecenter => "ジャイロ照準を中央へ(押している間)",
        _ => key,
    };

    /// <summary>ホットキーの既定の組み合わせ(従来の固定操作と同じ)。</summary>
    public static GamepadButton[] GetDefaultHotkey(string key) => key switch
    {
        // 中断メニューと巻き戻しは、同じ組み合わせなら従来どおり「タップ=中断メニュー、長押し=巻き戻し」。
        HotkeyMenu => new[] { GamepadButton.LeftTrigger, GamepadButton.RightTrigger },
        HotkeyRewind => new[] { GamepadButton.LeftTrigger, GamepadButton.RightTrigger },
        HotkeyFastForward => new[] { GamepadButton.RightTrigger },
        HotkeyFastForwardReverse => new[] { GamepadButton.LeftShoulder, GamepadButton.LeftTrigger },
        // ジャイロでポインターを動かすWiiで、ずれたポインターを画面の中央へ戻す。
        HotkeyPointerRecenter => new[] { GamepadButton.LeftShoulder, GamepadButton.RightShoulder },
        _ => Array.Empty<GamepadButton>(),
    };

    /// <summary>組み合わせを保存用の文字列にする(例: "LeftTrigger+RightTrigger"。空なら"None")。</summary>
    public static string FormatCombo(IReadOnlyList<GamepadButton> buttons)
        => buttons.Count == 0 ? "None" : string.Join("+", buttons.Select(b => b.ToString()));

    /// <summary>保存用の文字列から組み合わせを復元する。"None"は空(無効)、壊れた値はfallback。ボタン数の上限なし。</summary>
    public static GamepadButton[] ParseCombo(string? text, GamepadButton[] fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (string.Equals(text, "None", StringComparison.OrdinalIgnoreCase)) return Array.Empty<GamepadButton>();

        var result = new List<GamepadButton>();
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(part, out GamepadButton button)) return fallback;
            if (!result.Contains(button)) result.Add(button);
        }

        return result.Count >= 1 ? result.ToArray() : fallback;
    }

    /// <summary>画面表示用(例: "ZL + ZR"。空なら"なし")。</summary>
    public static string DescribeCombo(IReadOnlyList<GamepadButton> buttons)
        => buttons.Count == 0 ? "なし" : string.Join(" + ", buttons.Select(GetPhysicalButtonShortLabel));

    /// <summary>L2/R2/L3/R3のように、設定が無いときの既定の物理ボタン(表示用)。</summary>
    public static GamepadButton GetBuiltinPhysicalButton(string retroPadKey) => retroPadKey switch
    {
        "A" => GamepadButton.B,
        "B" => GamepadButton.A,
        "X" => GamepadButton.Y,
        "Y" => GamepadButton.X,
        "L" => GamepadButton.LeftShoulder,
        "R" => GamepadButton.RightShoulder,
        "L2" => GamepadButton.LeftTrigger,
        "R2" => GamepadButton.RightTrigger,
        "L3" => GamepadButton.LeftThumbstickButton,
        "R3" => GamepadButton.RightThumbstickButton,
        "Start" => GamepadButton.Menu,
        "Select" => GamepadButton.View,
        "Up" => GamepadButton.DPadUp,
        "Down" => GamepadButton.DPadDown,
        "Left" => GamepadButton.DPadLeft,
        "Right" => GamepadButton.DPadRight,
        "LSUp" => GamepadButton.LeftStickUp,
        "LSDown" => GamepadButton.LeftStickDown,
        "LSLeft" => GamepadButton.LeftStickLeft,
        "LSRight" => GamepadButton.LeftStickRight,
        "RSUp" => GamepadButton.RightStickUp,
        "RSDown" => GamepadButton.RightStickDown,
        "RSLeft" => GamepadButton.RightStickLeft,
        "RSRight" => GamepadButton.RightStickRight,
        _ => GamepadButton.A,
    };

    /// <summary>
    /// コントローラー設定画面でRetroPadの各ボタンに割り当てられる物理入力の候補
    /// (ボタン類、スティック押し込み(L3/R3)、右スティックの4方向。十字キー・左スティックは対象外)。
    /// </summary>
    public static readonly GamepadButton[] AssignablePhysicalButtons =
    {
        GamepadButton.A,
        GamepadButton.B,
        GamepadButton.X,
        GamepadButton.Y,
        GamepadButton.LeftShoulder,
        GamepadButton.RightShoulder,
        GamepadButton.LeftTrigger,
        GamepadButton.RightTrigger,
        GamepadButton.LeftThumbstickButton,
        GamepadButton.RightThumbstickButton,
        GamepadButton.Menu,
        GamepadButton.View,
        GamepadButton.RightStickUp,
        GamepadButton.RightStickDown,
        GamepadButton.RightStickLeft,
        GamepadButton.RightStickRight,
    };

    /// <summary>コントローラー設定画面に表示する、物理ボタンの日本語ラベル。</summary>
    public static string GetPhysicalButtonDisplayName(GamepadButton button) => button switch
    {
        GamepadButton.A => "A(下/南)",
        GamepadButton.B => "B(右/東)",
        GamepadButton.X => "X(左/西)",
        GamepadButton.Y => "Y(上/北)",
        GamepadButton.LeftShoulder => "L1(左肩)",
        GamepadButton.RightShoulder => "R1(右肩)",
        GamepadButton.LeftTrigger => "L2(左トリガー)",
        GamepadButton.RightTrigger => "R2(右トリガー)",
        GamepadButton.Menu => "MENU(旧START)",
        GamepadButton.View => "VIEW(旧SELECT)",
        GamepadButton.DPadUp => "十字キー上",
        GamepadButton.DPadDown => "十字キー下",
        GamepadButton.DPadLeft => "十字キー左",
        GamepadButton.DPadRight => "十字キー右",
        GamepadButton.LeftThumbstickButton => "L3(左スティック押込み)",
        GamepadButton.RightThumbstickButton => "R3(右スティック押込み)",
        GamepadButton.LeftStickUp => "左スティック上",
        GamepadButton.LeftStickDown => "左スティック下",
        GamepadButton.LeftStickLeft => "左スティック左",
        GamepadButton.LeftStickRight => "左スティック右",
        GamepadButton.RightStickUp => "右スティック上",
        GamepadButton.RightStickDown => "右スティック下",
        GamepadButton.RightStickLeft => "右スティック左",
        GamepadButton.RightStickRight => "右スティック右",
        _ => button.ToString(),
    };

    /// <summary>コントローラー図(ControllerSettingsPage)の丸ボタン内に収める短縮ラベル。</summary>
    public static string GetPhysicalButtonShortLabel(GamepadButton button) => button switch
    {
        GamepadButton.A => "A",
        GamepadButton.B => "B",
        GamepadButton.X => "X",
        GamepadButton.Y => "Y",
        GamepadButton.LeftShoulder => "L1",
        GamepadButton.RightShoulder => "R1",
        GamepadButton.LeftTrigger => "L2",
        GamepadButton.RightTrigger => "R2",
        GamepadButton.Menu => "MENU",
        GamepadButton.View => "VIEW",
        GamepadButton.DPadUp => "十字キー↑",
        GamepadButton.DPadDown => "十字キー↓",
        GamepadButton.DPadLeft => "十字キー←",
        GamepadButton.DPadRight => "十字キー→",
        GamepadButton.LeftStickUp => "左スティック↑",
        GamepadButton.LeftStickDown => "左スティック↓",
        GamepadButton.LeftStickLeft => "左スティック←",
        GamepadButton.LeftStickRight => "左スティック→",
        GamepadButton.RightStickUp => "右スティック↑",
        GamepadButton.RightStickDown => "右スティック↓",
        GamepadButton.RightStickLeft => "右スティック←",
        GamepadButton.RightStickRight => "右スティック→",
        GamepadButton.LeftThumbstickButton => "L3",
        GamepadButton.RightThumbstickButton => "R3",
        _ => button.ToString(),
    };

    /// <summary>コンソール1つ分の設定(変更した項目だけを持つ)。</summary>
    public sealed class ConsoleControllerConfig
    {
        /// <summary>RetroPadのキー(<see cref="AllRetroPadKeys"/>)→物理ボタン名。変更したキーだけ。</summary>
        public Dictionary<string, string> Buttons { get; set; } = new();

        /// <summary>ホットキー名(<see cref="HotkeyKeys"/>)→組み合わせ(<see cref="FormatCombo"/>の形式)。変更したものだけ。</summary>
        public Dictionary<string, string> Hotkeys { get; set; } = new();

        /// <summary>
        /// 連射の割り当て。RetroPadのキー → 物理ボタン名。その物理ボタンを押している間、そのキーを
        /// 1秒間に約15回「押す/離す」する。通常の割り当て(Buttons)とは別に持つ。
        /// </summary>
        public Dictionary<string, string> Turbo { get; set; } = new();

        /// <summary>キーボード入力をコアへ渡すか。変更していなければnull(controllerProfiles.jsonの既定に従う)。</summary>
        public bool? Keyboard { get; set; }

        /// <summary>ポート1(2P側)の装置のId(controllerProfiles.jsonの"port1")。変更していなければnull(既定に従う)。</summary>
        public string? Port1 { get; set; }
    }

    /// <summary>ゲーム中のホットキーの組み合わせ(0〜2ボタン。空なら無効)。</summary>
    public sealed class HotkeyBindings
    {
        public GamepadButton[] Menu { get; set; } = GetDefaultHotkey(HotkeyMenu);
        public GamepadButton[] Rewind { get; set; } = GetDefaultHotkey(HotkeyRewind);
        public GamepadButton[] FastForward { get; set; } = GetDefaultHotkey(HotkeyFastForward);
        public GamepadButton[] FastForwardReverse { get; set; } = GetDefaultHotkey(HotkeyFastForwardReverse);
        public GamepadButton[] PointerRecenter { get; set; } = GetDefaultHotkey(HotkeyPointerRecenter);
    }

    /// <summary>data/controllers.jsonの中身。</summary>
    public sealed class ControllerMappingData
    {
        /// <summary>
        /// trueなら、ランチャー自身のメニュー操作で物理A/Bボタンの役割を入れ替える
        /// (物理EAST=B=決定、物理SOUTH=A=キャンセルにする)。既定はfalse。
        /// </summary>
        public bool SwapLauncherConfirmCancel { get; set; }

        /// <summary>
        /// ゲーム中の振動の強さ(0〜100の%。0=振動しない、100=コアが指定した強さのまま)。
        /// コントローラー設定の「基本設定」で変える。全コンソール共通。既定は100。
        /// </summary>
        public int RumbleLevel { get; set; } = 100;

        /// <summary>
        /// RetroPadのボタン名(A/B/X/Y/L/R/Start/Select)→物理ボタン名の、全コンソール共通の下地。
        /// 画面からは編集しない(以前のデフォルト設定を引き継ぐための値)。
        /// </summary>
        public Dictionary<string, string> DefaultMapping { get; set; } = CreateBuiltinDefaultMapping();

        /// <summary>コンソールID(Consoles.jsonのid)→そのコンソールの設定。</summary>
        public Dictionary<string, ConsoleControllerConfig> Consoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EmulationHostに元々ハードコードされていた既定の物理ボタン割り当てと同じ内容を作る
    /// (初期状態での挙動を、この設定画面を作る前と一致させるため)。
    /// </summary>
    private static Dictionary<string, string> CreateBuiltinDefaultMapping() => new()
    {
        ["A"] = nameof(GamepadButton.B),
        ["B"] = nameof(GamepadButton.A),
        ["X"] = nameof(GamepadButton.Y),
        ["Y"] = nameof(GamepadButton.X),
        ["L"] = nameof(GamepadButton.LeftShoulder),
        ["R"] = nameof(GamepadButton.RightShoulder),
        ["Start"] = nameof(GamepadButton.Menu),
        ["Select"] = nameof(GamepadButton.View),
    };

    private static string GetConfigPath()
        => Path.Combine(AppContext.BaseDirectory, "data", "controllers.json");

    /// <summary>旧ファイル名(ボタン割り当て以外も保存するようになったため改名した)。</summary>
    private static string GetLegacyConfigPath()
        => Path.Combine(AppContext.BaseDirectory, "data", "controller_mapping.json");

    /// <summary>
    /// 新しいファイルがまだ無く、旧名のファイルがあれば、新しい名前へコピーする(設定を引き継ぐ)。
    /// 旧ファイルは消さずに残す。
    /// </summary>
    private static void MigrateLegacyConfigIfNeeded()
    {
        try
        {
            string path = GetConfigPath();
            string legacy = GetLegacyConfigPath();
            if (!File.Exists(path) && File.Exists(legacy)) File.Copy(legacy, path);
        }
        catch
        {
            // 移行に失敗しても、既定値で動かす(次回また試す)。
        }
    }

    // MainWindowがボタン入力のたびに参照しても disk I/O が発生しないよう、読み込み結果を
    // プロセス内にキャッシュしておく。Save()の際にキャッシュも更新する。
    private static ControllerMappingData? _cache;

    /// <summary>
    /// ランチャー操作でA/Bを入れ替えて扱うか。「標準」(SwapLauncherConfirmCancel=false)では
    /// Aが決定・Bが戻る、「入れ替え」(true)ではBが決定・Aが戻る。
    /// </summary>
    private static bool IsLauncherAbReversed => Load().SwapLauncherConfirmCancel;

    /// <summary>
    /// ランチャー操作用に、「入れ替え」がONのときだけ物理A/Bを入れ替えた値を返す
    /// (ランチャー自身のメニュー操作と、ゲーム中断メニューの操作に使う。X/Y等はそのまま)。
    /// 標準(OFF)ではそのまま返す: A=決定、B=戻る。
    /// </summary>
    public static GamepadButton TranslateForLauncher(GamepadButton button)
    {
        if (!IsLauncherAbReversed) return button;

        return button switch
        {
            GamepadButton.A => GamepadButton.B,
            GamepadButton.B => GamepadButton.A,
            _ => button,
        };
    }

    /// <summary>操作ガイドに出すキー名("A"/"B")を、入れ替え設定に合わせて返す(それ以外はそのまま)。</summary>
    public static string TranslateKeyLabelForLauncher(string key)
    {
        if (!IsLauncherAbReversed) return key;
        return key switch { "A" => "B", "B" => "A", _ => key };
    }

    /// <summary>設定を読み込む(2回目以降はキャッシュを返す)。ファイルが無い/壊れている場合は既定値。</summary>
    public static ControllerMappingData Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            MigrateLegacyConfigIfNeeded();
            string path = GetConfigPath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                ControllerMappingData? loaded = JsonSerializer.Deserialize<ControllerMappingData>(json);
                if (loaded is not null)
                {
                    // 将来キーが増えた場合に備え、既定マッピングに無いキーが欠けていたら補う。
                    foreach (KeyValuePair<string, string> kv in CreateBuiltinDefaultMapping())
                    {
                        loaded.DefaultMapping.TryAdd(kv.Key, kv.Value);
                    }

                    // 大文字小文字を区別しない辞書へ作り直す(JSONから読むと通常の辞書になるため)。
                    loaded.Consoles = new Dictionary<string, ConsoleControllerConfig>(
                        loaded.Consoles ?? new Dictionary<string, ConsoleControllerConfig>(),
                        StringComparer.OrdinalIgnoreCase);

                    _cache = loaded;
                    if (MigrateLegacyScopes(loaded))
                    {
                        try { Save(loaded); } catch { /* 保存できなくても、メモリ上は移行済みで動く。 */ }
                    }

                    return _cache;
                }
            }
        }
        catch
        {
            // 壊れたjson等は既定値として扱う(起動自体は継続できるようにする)。
        }

        _cache = new ControllerMappingData();
        return _cache;
    }

    /// <summary>
    /// 以前の形式のスコープ"wii@nunchuk"(コア無し)の設定を、"wii@dolphin/nunchuk"(コア+形態)へ移す。
    /// 新しい方に既に設定があれば、それを優先して古い方は捨てる。何か移したらtrue。
    /// </summary>
    private static bool MigrateLegacyScopes(ControllerMappingData data)
    {
        bool changed = false;
        foreach (string key in data.Consoles.Keys.ToList())
        {
            string? migrated = ControllerProfiles.MigrateLegacyScope(key);
            if (migrated is null) continue;

            if (!data.Consoles.ContainsKey(migrated)) data.Consoles[migrated] = data.Consoles[key];
            data.Consoles.Remove(key);
            changed = true;
        }

        return changed;
    }

    // 編集セッション: 設定画面で「保存」を押すまで、変更はメモリ上(キャッシュ)だけに置き、ファイルへ書かない。
    private static bool _editing;

    /// <summary>編集セッションを始める。以降のSaveはメモリ上だけの変更になり、CommitEditで初めてファイルへ書く。</summary>
    public static void BeginEdit() => _editing = true;

    /// <summary>編集セッションの変更を、ファイルへ書き込んで確定する。</summary>
    public static void CommitEdit()
    {
        _editing = false;
        if (_cache is not null) Save(_cache);
    }

    /// <summary>編集セッションの変更を捨てる(ファイルの内容に戻す)。</summary>
    public static void CancelEdit()
    {
        _editing = false;
        _cache = null;
    }

    /// <summary>設定を保存し、プロセス内キャッシュも更新する。編集セッション中はキャッシュだけ更新する。</summary>
    public static void Save(ControllerMappingData data)
    {
        if (_editing)
        {
            _cache = data;
            return;
        }

        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(folder);

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetConfigPath(), json);
        }
        catch
        {
            // 保存に失敗しても、この起動セッション中はメモリ上の値で動作は継続できる。
        }

        _cache = data;
    }

    private static ConsoleControllerConfig GetOrCreateConsole(ControllerMappingData data, string consoleId)
    {
        if (!data.Consoles.TryGetValue(consoleId, out ConsoleControllerConfig? config))
        {
            config = new ConsoleControllerConfig();
            data.Consoles[consoleId] = config;
        }

        return config;
    }

    // ------------------------------------------------------------------
    // ゲーム別の設定(RomFile.Customの"controller"セクション)。コンソールの設定の上に、
    // そのゲームだけの差分を重ねる。形式:
    //   "controller": { "buttons": { "X": "West" }, "turbo": { "B": "X" }, "hotkeys": { "Menu": "Menu+View" } }
    // ・buttons: RetroPadのキー → 物理ボタン名(コンソール設定と同じ名前。"None"=割り当てなし)。変更したキーだけ。
    // ・turbo: RetroPadのキー → 連射にする物理ボタン名("None"=そのキーの連射を解除)。変更したキーだけ。
    // ・hotkeys: ホットキー名 → 組み合わせ(FormatComboの形式)。変更したものだけ。
    // いずれもコンソール設定との差分だけを持つ(コンソールの設定と同じになったら項目ごと消す)。
    // ------------------------------------------------------------------

    public const string GameSection = "controller";

    /// <summary>ゲーム別のコントローラ設定(読み込んだ結果)。</summary>
    public sealed class GameControllerConfig
    {
        public Dictionary<string, string> Buttons { get; set; } = new();
        public Dictionary<string, string> Turbo { get; set; } = new();
        public Dictionary<string, string> Hotkeys { get; set; } = new();

        /// <summary>このゲームだけのキーボード設定。コンソールの設定と同じならnull。</summary>
        public bool? Keyboard { get; set; }

        /// <summary>このゲームだけのポート1の装置のId。コンソールの設定と同じならnull。</summary>
        public string? Port1 { get; set; }

        /// <summary>コンソールの設定と違う項目の数(0なら標準)。</summary>
        public int Count => Buttons.Count + Turbo.Count + Hotkeys.Count + (Keyboard.HasValue ? 1 : 0) + (Port1 is null ? 0 : 1);
    }

    private static void ReadStringMap(JsonObject section, string name, Dictionary<string, string> into)
    {
        if (section[name] is not JsonObject obj) return;
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (kv.Value is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrEmpty(text))
            {
                into[kv.Key] = text;
            }
        }
    }

    /// <summary>RomFile.Custom(JSON文字列)から、ゲーム別のコントローラ設定を読む。無い・壊れている場合は空(差分なし)。</summary>
    public static GameControllerConfig ReadGameConfig(string? customJson, string? scopeId = null)
    {
        var config = new GameControllerConfig();
        if (string.IsNullOrWhiteSpace(customJson)) return config;

        try
        {
            if (JsonNode.Parse(customJson) is not JsonObject root || FindGameSection(root, scopeId) is not JsonObject section) return config;

            ReadStringMap(section, "buttons", config.Buttons);
            ReadStringMap(section, "turbo", config.Turbo);
            ReadStringMap(section, "hotkeys", config.Hotkeys);
            if (section["keyboard"] is JsonValue kv && kv.TryGetValue(out bool keyboard)) config.Keyboard = keyboard;
            if (section["port1"] is JsonValue pv && pv.TryGetValue(out string? port1) && !string.IsNullOrEmpty(port1)) config.Port1 = port1;
        }
        catch (Exception)
        {
            // 壊れた値は差分なしとして扱う(ゲームの起動を妨げない)。
        }

        return config;
    }

    /// <summary>
    /// 共通の下地 → コンソール別の設定 → ゲーム別の設定の順に重ねた、
    /// RetroPadのキー → 物理ボタン名の割り当て(コアのinput_remapは含まない)。
    /// </summary>
    private static Dictionary<string, string> LayerMapping(string consoleId, string? gameCustomJson)
    {
        ControllerMappingData data = Load();
        var merged = new Dictionary<string, string>(data.DefaultMapping);

        // 形態(プリセット)の既定の割り当て(例: ヌンチャクのZ=L1)。利用者の設定より弱い。
        if (ControllerProfiles.FindForm(consoleId) is { } presetForm)
        {
            foreach (KeyValuePair<string, string> kv in presetForm.DefaultButtons)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        // その機種で共通 → 形態別(consoleIdが"wii@nunchuk"のとき)の順に重ねる。
        foreach (string scope in ControllerProfiles.ScopeChain(consoleId))
        {
            if (!data.Consoles.TryGetValue(scope, out ConsoleControllerConfig? config)) continue;
            foreach (KeyValuePair<string, string> kv in config.Buttons)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        GameControllerConfig game = ReadGameConfig(gameCustomJson, consoleId);
        foreach (KeyValuePair<string, string> kv in game.Buttons)
        {
            merged[kv.Key] = kv.Value;
        }

        return merged;
    }

    /// <summary>
    /// 設定画面の表示用に、12キー(<see cref="AllRetroPadKeys"/>)すべての現在の物理ボタンを返す
    /// (共通の下地 + そのコンソールで変更した分 + <paramref name="gameCustomJson"/>(ゲーム別)の分)。
    /// </summary>
    public static Dictionary<string, GamepadButton> GetEffectiveButtons(string consoleId, string? gameCustomJson = null)
    {
        Dictionary<string, string> layered = LayerMapping(consoleId, gameCustomJson);

        var result = new Dictionary<string, GamepadButton>();
        foreach (string key in AllRetroPadKeys)
        {
            layered.TryGetValue(key, out string? name);
            result[key] = GamepadPhysicalButtonReader.ParseOrDefault(name, GetBuiltinPhysicalButton(key));
        }

        return result;
    }

    // ---- ゲーム別の設定を書き換える(RomFile.CustomのJsonObjectを直接更新。UpdateCustomAsyncのコールバック用) ----

    /// <summary>
    /// ゲーム別設定の置き場所。形態を持つコンソール("wii@nunchuk")は形態ごとに
    /// "controller": { "forms": { "nunchuk": { "buttons": ... } } } へ、それ以外は"controller"直下へ置く。
    /// 読むだけで作らない(無ければnull)。
    /// </summary>
    private static JsonObject? FindGameSection(JsonObject root, string? scopeId)
    {
        if (root[GameSection] is not JsonObject controller) return null;
        if (scopeId is null || !ControllerProfiles.IsFormScope(scopeId)) return controller;
        var forms = controller["forms"] as JsonObject;
        return forms?[FormIdOf(scopeId)] as JsonObject ?? forms?[LegacyFormIdOf(scopeId)] as JsonObject;
    }

    private static string FormIdOf(string scopeId) => scopeId[(scopeId.IndexOf(ControllerProfiles.ScopeSeparator) + 1)..];

    /// <summary>以前の保存形式(コア無し)での形態ID。"wii@dolphin/nunchuk" → "nunchuk"。</summary>
    private static string LegacyFormIdOf(string scopeId)
    {
        string id = FormIdOf(scopeId);
        return id[(id.IndexOf(ControllerProfiles.CoreFormSeparator) + 1)..];
    }

    private static JsonObject GetOrCreateGameSection(JsonObject custom, string? scopeId = null)
    {
        JsonObject controller;
        if (custom[GameSection] is JsonObject existing) controller = existing;
        else
        {
            controller = new JsonObject();
            custom[GameSection] = controller;
        }

        if (scopeId is null || !ControllerProfiles.IsFormScope(scopeId)) return controller;

        JsonObject forms;
        if (controller["forms"] is JsonObject existingForms) forms = existingForms;
        else
        {
            forms = new JsonObject();
            controller["forms"] = forms;
        }

        string formId = FormIdOf(scopeId);
        if (forms[formId] is JsonObject form) return form;

        // 以前の形式(コア無し)で保存済みなら、新しいキーへ移して引き継ぐ。
        string legacyId = LegacyFormIdOf(scopeId);
        if (legacyId != formId && forms[legacyId] is JsonObject legacy)
        {
            forms.Remove(legacyId);
            forms[formId] = legacy;
            return legacy;
        }

        var created = new JsonObject();
        forms[formId] = created;
        return created;
    }

    /// <summary>ゲーム別設定の、変更のある項目の数(形態ごとの分も含めた合計)。</summary>
    public static int CountGameConfig(string? customJson)
    {
        int total = ReadGameConfig(customJson).Count;
        if (string.IsNullOrWhiteSpace(customJson)) return total;
        try
        {
            if (JsonNode.Parse(customJson) is JsonObject root
                && root[GameSection] is JsonObject controller
                && controller["forms"] is JsonObject forms)
            {
                foreach (KeyValuePair<string, JsonNode?> kv in forms)
                {
                    if (kv.Value is not JsonObject form) continue;
                    foreach (string name in new[] { "buttons", "turbo", "hotkeys" })
                    {
                        if (form[name] is JsonObject map) total += map.Count;
                    }
                    if (form["keyboard"] is JsonValue) total++;
                }
            }
        }
        catch (Exception)
        {
        }

        return total;
    }

    /// <summary>ゲーム別に1つのキーの割り当てを設定する(<paramref name="physical"/> がnullならそのキーの個別設定を削除)。</summary>
    public static void SetGameButton(JsonObject custom, string retroPadKey, GamepadButton? physical)
    {
        JsonObject section = GetOrCreateGameSection(custom);
        JsonObject buttons = section["buttons"] as JsonObject ?? new JsonObject();
        section["buttons"] = buttons;

        if (physical is null) buttons.Remove(retroPadKey);
        else buttons[retroPadKey] = physical.Value.ToString();

        PruneGameSection(custom);
    }

    private static JsonObject GetOrCreateMap(JsonObject custom, string name, string? scopeId = null)
    {
        JsonObject section = GetOrCreateGameSection(custom, scopeId);
        JsonObject map = section[name] as JsonObject ?? new JsonObject();
        section[name] = map;
        return map;
    }

    /// <summary>
    /// ゲーム別に、物理ボタンの割り当て(<paramref name="physical"/>がnullなら割り当てなし)を設定する。
    /// コンソールの設定と同じになるなら個別設定を消す(差分だけを持つ)。
    /// </summary>
    public static void SetGameAssignment(JsonObject custom, string consoleId, string retroPadKey, GamepadButton? physical)
    {
        GetAssignments(consoleId).TryGetValue(retroPadKey, out GamepadButton? baseline);
        JsonObject map = GetOrCreateMap(custom, "buttons", consoleId);
        if (baseline == physical) map.Remove(retroPadKey);
        else map[retroPadKey] = physical?.ToString() ?? NoneName;
        PruneGameSection(custom, consoleId);
    }

    /// <summary>ゲーム別に連射の割り当てを設定する(nullなら連射なし)。コンソールの設定と同じなら個別設定を消す。</summary>
    public static void SetGameTurbo(JsonObject custom, string consoleId, string retroPadKey, GamepadButton? physical)
    {
        GamepadButton? baseline = GetTurbo(consoleId).TryGetValue(retroPadKey, out GamepadButton b) ? b : null;
        JsonObject map = GetOrCreateMap(custom, "turbo", consoleId);
        if (baseline == physical) map.Remove(retroPadKey);
        else map[retroPadKey] = physical?.ToString() ?? NoneName;
        PruneGameSection(custom, consoleId);
    }

    /// <summary>ゲーム別にホットキーの組み合わせを設定する(空なら無効)。コンソールの設定と同じなら個別設定を消す。</summary>
    public static void SetGameHotkey(JsonObject custom, string consoleId, string hotkeyKey, IReadOnlyList<GamepadButton> buttons)
    {
        GamepadButton[] baseline = GetHotkey(consoleId, hotkeyKey);
        JsonObject map = GetOrCreateMap(custom, "hotkeys", consoleId);
        bool same = baseline.Length == buttons.Count && baseline.All(x => buttons.Contains(x));
        if (same) map.Remove(hotkeyKey);
        else map[hotkeyKey] = FormatCombo(buttons);
        PruneGameSection(custom, consoleId);
    }

    /// <summary>ゲーム別に、キーボード入力をコアへ渡すかを設定する。コンソールの設定と同じなら個別設定を消す。</summary>
    public static void SetGameKeyboard(JsonObject custom, string consoleId, bool enabled)
    {
        bool baseline = IsKeyboardEnabled(consoleId);
        JsonObject section = GetOrCreateGameSection(custom, consoleId);
        if (enabled == baseline) section.Remove("keyboard");
        else section["keyboard"] = enabled;
        PruneGameSection(custom, consoleId);
    }

    /// <summary>
    /// キーボード入力をコアへ渡すか。controllerProfiles.jsonの"keyboard"に無いコンソールは常にfalse。
    /// 優先順位(弱→強): JSONの既定 → コンソール別の設定 → ゲーム別の設定。
    /// </summary>
    public static bool IsKeyboardEnabled(string scopeId, string? gameCustomJson = null)
    {
        KeyboardOption? option = ControllerProfiles.FindKeyboardOption(scopeId);
        if (option is null) return false;

        bool enabled = option.Default;
        ControllerMappingData data = Load();
        foreach (string scope in ControllerProfiles.ScopeChain(scopeId))
        {
            if (data.Consoles.TryGetValue(scope, out ConsoleControllerConfig? config) && config.Keyboard is bool saved)
            {
                enabled = saved;
            }
        }

        if (ReadGameConfig(gameCustomJson, scopeId).Keyboard is bool own) enabled = own;
        return enabled;
    }

    /// <summary>そのコンソールの、キーボード入力をコアへ渡す設定を変更して保存する(既定と同じならnullへ戻す)。</summary>
    public static void SetKeyboard(string scopeId, bool enabled)
    {
        ControllerMappingData data = Load();
        bool isDefault = ControllerProfiles.FindKeyboardOption(scopeId)?.Default == enabled;
        if (isDefault && !data.Consoles.ContainsKey(scopeId)) return;
        GetOrCreateConsole(data, scopeId).Keyboard = isDefault ? null : enabled;
        Save(data);
    }

    /// <summary>
    /// ポート1(2P側)の装置のId。controllerProfiles.jsonの"port1"に無いコンソールはnull。
    /// 優先順位(弱→強): JSONの既定 → コンソール別の設定 → ゲーム別の設定。保存値が一覧に無いIdなら既定に戻す。
    /// </summary>
    public static string? GetPort1DeviceId(string scopeId, string? gameCustomJson = null)
    {
        Port1Option? option = ControllerProfiles.FindPort1Option(scopeId);
        if (option is null) return null;

        string id = option.Default;
        ControllerMappingData data = Load();
        foreach (string scope in ControllerProfiles.ScopeChain(scopeId))
        {
            if (data.Consoles.TryGetValue(scope, out ConsoleControllerConfig? config) && !string.IsNullOrEmpty(config.Port1))
            {
                id = config.Port1;
            }
        }

        string? own = ReadGameConfig(gameCustomJson, scopeId).Port1;
        if (!string.IsNullOrEmpty(own)) id = own;
        return option.FindDevice(id)?.Id;
    }

    /// <summary>そのコンソールの、ポート1の装置を変更して保存する(既定と同じならnullへ戻す)。</summary>
    public static void SetPort1Device(string scopeId, string deviceId)
    {
        Port1Option? option = ControllerProfiles.FindPort1Option(scopeId);
        if (option is null) return;

        ControllerMappingData data = Load();
        bool isDefault = string.Equals(option.Default, deviceId, StringComparison.OrdinalIgnoreCase);
        if (isDefault && !data.Consoles.ContainsKey(scopeId)) return;
        GetOrCreateConsole(data, scopeId).Port1 = isDefault ? null : deviceId;
        Save(data);
    }

    /// <summary>ゲーム別に、ポート1の装置を設定する。コンソールの設定と同じなら個別設定を消す。</summary>
    public static void SetGamePort1Device(JsonObject custom, string consoleId, string deviceId)
    {
        string? baseline = GetPort1DeviceId(consoleId);
        JsonObject section = GetOrCreateGameSection(custom, consoleId);
        if (string.Equals(baseline, deviceId, StringComparison.OrdinalIgnoreCase)) section.Remove("port1");
        else section["port1"] = deviceId;
        PruneGameSection(custom, consoleId);
    }

    /// <summary>このゲームのコントローラ設定をすべて削除する(コンソールの設定に戻す)。</summary>
    public static void ClearGameConfig(JsonObject custom, string? scopeId = null)
    {
        if (scopeId is null || !ControllerProfiles.IsFormScope(scopeId))
        {
            custom.Remove(GameSection);
            return;
        }

        if (FindGameSection(custom, scopeId) is JsonObject section)
        {
            section.Clear();
            PruneGameSection(custom, scopeId);
        }
    }

    /// <summary>中身が空になった"controller"セクション(空のbuttonsを含む)を取り除く。</summary>
    private static void PruneGameSection(JsonObject custom, string? scopeId = null)
    {
        if (custom[GameSection] is not JsonObject controller) return;

        JsonObject? section = FindGameSection(custom, scopeId);
        if (section is not null)
        {
            foreach (string name in new[] { "buttons", "turbo", "hotkeys" })
            {
                if (section[name] is JsonObject obj && obj.Count == 0) section.Remove(name);
            }

            if (scopeId is not null && ControllerProfiles.IsFormScope(scopeId) && section.Count == 0
                && controller["forms"] is JsonObject forms)
            {
                forms.Remove(FormIdOf(scopeId));
                if (forms.Count == 0) controller.Remove("forms");
            }
        }

        if (controller.Count == 0) custom.Remove(GameSection);
    }

    /// <summary>そのコンソールの、1つのキーの割り当てを変更して保存する。</summary>
    public static void SetButton(string consoleId, string retroPadKey, GamepadButton physical)
    {
        ControllerMappingData data = Load();
        GetOrCreateConsole(data, consoleId).Buttons[retroPadKey] = physical.ToString();
        Save(data);
    }

    /// <summary>割り当てなし(そのキーはコアへ送らない)を表す保存値。</summary>
    public const string NoneName = "None";

    /// <summary>
    /// そのコンソールの、RetroPadの各キーに今ついている物理ボタン(共通の下地+コンソールの設定。ゲーム別は含まない)。
    /// 割り当てなし("None")のキーはnull。
    /// </summary>
    public static Dictionary<string, GamepadButton?> GetAssignments(string consoleId, string? gameCustomJson = null)
    {
        Dictionary<string, string> layered = LayerMapping(consoleId, gameCustomJson);
        var result = new Dictionary<string, GamepadButton?>();
        foreach (string key in AllMappingKeys)
        {
            layered.TryGetValue(key, out string? name);
            result[key] = string.Equals(name, NoneName, StringComparison.OrdinalIgnoreCase)
                ? null
                : GamepadPhysicalButtonReader.ParseOrDefault(name, GetBuiltinPhysicalButton(key));
        }

        return result;
    }

    /// <summary>そのコンソールの連射の割り当て(RetroPadのキー → 物理ボタン)。</summary>
    public static Dictionary<string, GamepadButton> GetTurbo(string consoleId, string? gameCustomJson = null)
    {
        var result = new Dictionary<string, GamepadButton>();
        ControllerMappingData data = Load();

        // 形態(プリセット)の標準の連射。
        if (ControllerProfiles.FindForm(consoleId) is { } form)
        {
            foreach (KeyValuePair<string, string> kv in form.Turbo)
            {
                if (Enum.TryParse(kv.Value, out GamepadButton physical)) result[kv.Key] = physical;
            }
        }

        foreach (string scope in ControllerProfiles.ScopeChain(consoleId))
        {
            if (!data.Consoles.TryGetValue(scope, out ConsoleControllerConfig? config)) continue;
            foreach (KeyValuePair<string, string> kv in config.Turbo)
            {
                // "None"は、標準の連射を利用者が解除したことを表す。
                if (string.Equals(kv.Value, NoneName, StringComparison.OrdinalIgnoreCase)) result.Remove(kv.Key);
                else if (Enum.TryParse(kv.Value, out GamepadButton physical)) result[kv.Key] = physical;
            }
        }

        // ゲーム別の差分("None"はそのキーの連射を解除)。
        foreach (KeyValuePair<string, string> kv in ReadGameConfig(gameCustomJson, consoleId).Turbo)
        {
            if (string.Equals(kv.Value, NoneName, StringComparison.OrdinalIgnoreCase)) result.Remove(kv.Key);
            else if (Enum.TryParse(kv.Value, out GamepadButton physical)) result[kv.Key] = physical;
        }

        return result;
    }

    /// <summary>ゲーム起動時にEmulationHostへ渡す連射の割り当て(RetroPadのキー → 物理ボタン名)。</summary>
    public static Dictionary<string, string> ResolveTurbo(string consoleId, string? gameCustomJson = null)
        => GetTurbo(consoleId, gameCustomJson).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    /// <summary>そのコンソールの1つのキーを、割り当てなし(コアへ送らない)にして保存する。</summary>
    public static void SetButtonNone(string consoleId, string retroPadKey)
    {
        ControllerMappingData data = Load();
        GetOrCreateConsole(data, consoleId).Buttons[retroPadKey] = NoneName;
        Save(data);
    }

    /// <summary>連射の割り当てを変更して保存する(physicalがnullならそのキーの連射を解除)。</summary>
    public static void SetTurbo(string consoleId, string retroPadKey, GamepadButton? physical)
    {
        ControllerMappingData data = Load();
        ConsoleControllerConfig config = GetOrCreateConsole(data, consoleId);
        if (physical is null)
        {
            // プリセットに標準の連射があるキーは、消すだけでは標準に戻ってしまうので、解除したことを"None"で残す。
            if (ControllerProfiles.FindForm(consoleId)?.Turbo.ContainsKey(retroPadKey) == true) config.Turbo[retroPadKey] = NoneName;
            else config.Turbo.Remove(retroPadKey);
        }
        else config.Turbo[retroPadKey] = physical.Value.ToString();
        Save(data);
    }

    /// <summary>そのコンソールのホットキーの組み合わせ(変更が無ければ既定)。</summary>
    public static GamepadButton[] GetHotkey(string consoleId, string hotkeyKey, string? gameCustomJson = null)
    {
        // その形態に無い機能のホットキーは使えない(無効)。
        if (!ControllerProfiles.IsHotkeyAvailable(consoleId, hotkeyKey)) return Array.Empty<GamepadButton>();

        ControllerMappingData data = Load();
        string? text = null;
        // 形態(プリセット)の既定があれば、全体の既定の代わりにそれを使う。
        if (ControllerProfiles.FindForm(consoleId) is { } form
            && form.Hotkeys.TryGetValue(hotkeyKey, out string? formDefault))
        {
            text = formDefault;
        }

        foreach (string scope in ControllerProfiles.ScopeChain(consoleId))
        {
            if (data.Consoles.TryGetValue(scope, out ConsoleControllerConfig? config)
                && config.Hotkeys.TryGetValue(hotkeyKey, out string? saved))
            {
                text = saved;
            }
        }
        if (ReadGameConfig(gameCustomJson, consoleId).Hotkeys.TryGetValue(hotkeyKey, out string? own)) text = own;
        return ParseCombo(text, GetDefaultHotkey(hotkeyKey));
    }

    /// <summary>そのコンソールのホットキーの組み合わせを変更して保存する(空なら無効)。</summary>
    public static void SetHotkey(string consoleId, string hotkeyKey, IReadOnlyList<GamepadButton> buttons)
    {
        ControllerMappingData data = Load();
        GetOrCreateConsole(data, consoleId).Hotkeys[hotkeyKey] = FormatCombo(buttons);
        Save(data);
    }

    /// <summary>そのコンソールの設定(ボタン割り当て・ホットキー)をすべて既定へ戻す。</summary>
    public static void ResetConsole(string consoleId)
    {
        ControllerMappingData data = Load();
        if (data.Consoles.Remove(consoleId))
        {
            Save(data);
        }
    }

    /// <summary>そのコンソールに、既定から変更した設定があるか。</summary>
    public static bool HasCustomSettings(string consoleId)
    {
        Load().Consoles.TryGetValue(consoleId, out ConsoleControllerConfig? config);
        return config is not null && (config.Buttons.Count > 0 || config.Hotkeys.Count > 0 || config.Turbo.Count > 0 || config.Keyboard.HasValue || !string.IsNullOrEmpty(config.Port1));
    }

    /// <summary>
    /// ゲーム起動時にEmulationHostへ渡すボタン割り当てを組み立てる。
    /// 優先順位(弱→強): 共通の下地 → コントローラー設定画面でそのコンソールに設定した値
    /// → このゲームだけの設定(<paramref name="gameCustomJson"/>。RomFile.Custom)。
    /// (コア特有の差し替え(旧input_remap)は廃止した。コアの癖もユーザーが設定で直す)
    /// </summary>
    public static Dictionary<string, string> ResolveMapping(
        string consoleId,
        string? gameCustomJson = null)
    {
        Dictionary<string, string> merged = LayerMapping(consoleId, gameCustomJson);

        // コアが使わないキーにも別の意味を持たせているプリセット(PCエンジンのL2=2/6ボタン切替など)は、
        // プリセットに書かれていないキーをコアへ渡さない。
        if (ControllerProfiles.FindForm(consoleId) is { BlockUnlisted: true } form)
        {
            foreach (string key in AllRetroPadKeys)
            {
                if (!form.Buttons.ContainsKey(key)) merged[key] = NoneName;
            }
        }

        return merged;
    }

    /// <summary>ゲーム起動時にEmulationHostへ渡すホットキーの組み合わせ。</summary>
    public static HotkeyBindings ResolveHotkeys(string consoleId, string? gameCustomJson = null) => new()
    {
        Menu = GetHotkey(consoleId, HotkeyMenu, gameCustomJson),
        Rewind = GetHotkey(consoleId, HotkeyRewind, gameCustomJson),
        FastForward = GetHotkey(consoleId, HotkeyFastForward, gameCustomJson),
        FastForwardReverse = Array.Empty<GamepadButton>(),
        PointerRecenter = GetHotkey(consoleId, HotkeyPointerRecenter, gameCustomJson),
    };
}
