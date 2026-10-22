using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmulatorLauncher.Input;

/// <summary>コアの「コントローラの形態(接続形態)」1つ。例: Dolphinの「Wiiリモコン+ヌンチャク」。</summary>
public sealed class ControllerForm
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>画面に出す名前。</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>retro_set_controller_port_deviceへ渡すデバイス値(コア側の定義)。</summary>
    [JsonPropertyName("device")] public uint Device { get; set; }

    /// <summary>以前の保存値の読み替え(例: 廃止した"classicpro"は"classic"として扱う)。</summary>
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// 形態を選ぶコアオプションの値(例 PCエンジンの"6 Buttons")。形態IDと値が違うときだけ書く(省略時はIDがそのまま値)。
    /// </summary>
    [JsonPropertyName("optionValue")] public string? OptionValue { get; set; }

    /// <summary>
    /// trueなら、<see cref="Buttons"/>に書かれていないRetroPadのキーは、コアへ渡さない(割り当てなし扱い)。
    /// コアが、使わないキーにも別の意味(例: PCエンジンのL2=2/6ボタンの切替)を持たせているときに使う。
    /// </summary>
    [JsonPropertyName("blockUnlisted")] public bool BlockUnlisted { get; set; }

    /// <summary>コアオプションへ書く値(OptionValueが無ければ形態ID)。</summary>
    [JsonIgnore] public string ValueForOption => string.IsNullOrEmpty(OptionValue) ? Id : OptionValue;

    /// <summary>MotionPlus付きのデバイス値(MotionPlusの無い形態はnull)。設定の「MotionPlus: あり」のときこちらを使う。</summary>
    [JsonPropertyName("mpDevice")] public uint? MotionPlusDevice { get; set; }

    /// <summary>
    /// RetroPadのキー("A","B","X","Y","L","R","L2","R2","L3","R3","Start","Select") →
    /// この形態でのボタンの意味。無いキーは、この形態では使われない。
    /// </summary>
    [JsonPropertyName("buttons")] public Dictionary<string, string> Buttons { get; set; } = new();

    [JsonPropertyName("note")] public string? Note { get; set; }

    /// <summary>
    /// この形態の既定の物理ボタン割り当て(RetroPadのキー → 物理ボタン名。例 {"Y":"LeftShoulder"}=「RetroPadのYをL1で押す」)。
    /// 全体の既定の割り当ての上に重なり、利用者が設定画面で変えた分はさらにその上に重なる。
    /// </summary>
    [JsonPropertyName("defaultButtons")] public Dictionary<string, string> DefaultButtons { get; set; } = new();

    /// <summary>
    /// この形態の連射の標準設定(RetroPadのキー → 物理ボタン名。例 {"B":"X"}=「RetroPadのB(PCエンジンのII)をXボタンで連射」)。
    /// 利用者が変えたり解除したりした分は、その上に重なる。
    /// </summary>
    [JsonPropertyName("turbo")] public Dictionary<string, string> Turbo { get; set; } = new();

    /// <summary>
    /// この形態でのホットキーの既定の組み合わせ(ホットキー名 → "RightThumbstickButton" や
    /// "LeftShoulder+RightShoulder"の形式。"None"は使わない)。書かれていないホットキーは全体の既定。
    /// ホットキーの組み合わせが、その形態でゲームが使うボタンと重ならないように決める。
    /// </summary>
    [JsonPropertyName("hotkeys")] public Dictionary<string, string> Hotkeys { get; set; } = new();
}

/// <summary>
/// 「キーボード入力をコアへ渡す」設定を、そのコンソールの設定画面に出すかの指定(controllerProfiles.jsonの"keyboard")。
/// 指定が無いコンソールには、設定自体を出さない。
/// </summary>
public sealed class KeyboardOption
{
    [JsonPropertyName("console")] public string ConsoleId { get; set; } = string.Empty;

    /// <summary>設定を変えていないときの既定(trueならキーボードをコアへ渡す)。</summary>
    [JsonPropertyName("default")] public bool Default { get; set; }

    /// <summary>
    /// キーボードを接続するポート番号(0始まり)。コアが「ポートにキーボードを挿す」方式のとき(flycast等)だけ書く。
    /// 省略時は、ポートのデバイスは変えず、コアが問い合わせたキー入力にだけ答える(px68k等)。
    /// </summary>
    [JsonPropertyName("port")] public int? Port { get; set; }
}

/// <summary>ポート1(2P側)に挿せる装置の1つ。</summary>
public sealed class Port1DeviceChoice
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>設定画面に出す名前。</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>retro_set_controller_port_deviceへ渡す値(コア側の定義。JOYPAD=1、MOUSE=2、NONE=0、サブクラスは (n&lt;&lt;8)|1)。</summary>
    [JsonPropertyName("device")] public uint Device { get; set; }
}

/// <summary>
/// 「ポート1(2P側)の装置」を、そのコンソールの設定画面で選べるようにする指定(controllerProfiles.jsonの"port1")。
/// 指定が無いコンソールには、設定自体を出さない。
/// </summary>
public sealed class Port1Option
{
    [JsonPropertyName("console")] public string ConsoleId { get; set; } = string.Empty;

    /// <summary>設定を変えていないときの装置のId。</summary>
    [JsonPropertyName("default")] public string Default { get; set; } = string.Empty;

    [JsonPropertyName("devices")] public List<Port1DeviceChoice> Devices { get; set; } = new();

    /// <summary>Idから装置を探す。見つからなければ既定、それも無ければ先頭。</summary>
    public Port1DeviceChoice? FindDevice(string? id)
        => Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? Devices.FirstOrDefault(d => string.Equals(d.Id, Default, StringComparison.OrdinalIgnoreCase))
           ?? Devices.FirstOrDefault();
}

/// <summary>コンソール(=コア)ごとの形態の一覧。</summary>
public sealed class ConsoleControllerProfile
{
    [JsonPropertyName("core")] public string Core { get; set; } = string.Empty;
    [JsonPropertyName("console")] public string ConsoleId { get; set; } = string.Empty;

    /// <summary>設定画面に出すコア名(例 "Dolphin")。</summary>
    [JsonPropertyName("coreLabel")] public string CoreLabel { get; set; } = string.Empty;

    /// <summary>設定画面に出すコンソール名(例 "Wii"、"GameCube")。同じコアが複数のコンソールを担当するときの区別用。</summary>
    [JsonPropertyName("consoleLabel")] public string ConsoleLabel { get; set; } = string.Empty;

    /// <summary>設定の置き場所(スコープ)に入れるコアの短い名前。"dolphin_libretro.dll" → "dolphin"。</summary>
    [JsonIgnore]
    public string CoreKey
    {
        get
        {
            string name = Path.GetFileNameWithoutExtension(Core);
            const string suffix = "_libretro";
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
        }
    }

    /// <summary>形態を選ぶ擬似コアオプションのキー(cores/libretro.jsonのoptions)。</summary>
    [JsonPropertyName("optionKey")] public string OptionKey { get; set; } = string.Empty;

    [JsonPropertyName("default")] public string Default { get; set; } = string.Empty;
    [JsonPropertyName("forms")] public List<ControllerForm> Forms { get; set; } = new();
}

/// <summary>
/// controllerProfiles.json(アプリ同梱の静的データ)の読み込みと、形態の「設定の置き場所(スコープ)」の扱い。
///
/// スコープ: ボタン割り当て等は今までコンソールID(例 "wii")ごとに保存している。コアがプリセット(形態)を
/// 持つコンソールは、同じ入れ物に "wii@dolphin/nunchuk"(コンソール@コア/形態)のようなIDで保存する。
/// 同じコンソールでも、コアが違えばボタンの意味が違うので、設定はコアごとに別になる。
/// 起動時の優先順位は 共通の下地 → "wii"(旧:その機種で共通) → "wii@dolphin/nunchuk"(コア+形態別) → ゲーム別。
/// 以前の形式 "wii@nunchuk"(コア無し)は、読み込み時に新しい形式へ移す。
/// </summary>
public static class ControllerProfiles
{
    public const char ScopeSeparator = '@';

    private static List<ConsoleControllerProfile>? _profiles;

    private static List<ConsoleControllerProfile> Profiles => _profiles ??= Load();

    private static List<KeyboardOption>? _keyboardOptions;

    private static List<KeyboardOption> KeyboardOptions => _keyboardOptions ??= LoadKeyboardOptions();

    private static List<KeyboardOption> LoadKeyboardOptions()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "controllerProfiles.json");
            if (!File.Exists(path)) return new();

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("keyboard", out JsonElement arr)) return new();
            return arr.Deserialize<List<KeyboardOption>>() ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>そのコンソール("wii@dolphin/nunchuk"のような形態のスコープでも可)の、キーボード設定の指定。無ければnull(設定を出さない)。</summary>
    public static KeyboardOption? FindKeyboardOption(string consoleId)
    {
        if (string.IsNullOrEmpty(consoleId)) return null;
        string baseId = BaseConsoleId(consoleId);
        return KeyboardOptions.FirstOrDefault(k => string.Equals(k.ConsoleId, baseId, StringComparison.OrdinalIgnoreCase));
    }

    private static List<Port1Option>? _port1Options;

    private static List<Port1Option> Port1Options => _port1Options ??= LoadPort1Options();

    private static List<Port1Option> LoadPort1Options()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "controllerProfiles.json");
            if (!File.Exists(path)) return new();

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("port1", out JsonElement arr)) return new();
            return arr.Deserialize<List<Port1Option>>() ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>そのコンソール("wii@dolphin/nunchuk"のような形態のスコープでも可)の、ポート1の装置選択の指定。無ければnull(設定を出さない)。</summary>
    public static Port1Option? FindPort1Option(string consoleId)
    {
        if (string.IsNullOrEmpty(consoleId)) return null;
        string baseId = BaseConsoleId(consoleId);
        return Port1Options.FirstOrDefault(k => string.Equals(k.ConsoleId, baseId, StringComparison.OrdinalIgnoreCase) && k.Devices.Count > 0);
    }

    private static List<ConsoleControllerProfile> Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "controllerProfiles.json");
            if (!File.Exists(path)) return new();

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("profiles", out JsonElement arr)) return new();
            return arr.Deserialize<List<ConsoleControllerProfile>>() ?? new();
        }
        catch (Exception)
        {
            // 壊れていても起動は妨げない(形態の機能が使えないだけ)。
            return new();
        }
    }

    /// <summary>そのコンソールを担当するコアのプリセット一覧(コアごとに1つ)。無ければ空。</summary>
    public static IEnumerable<ConsoleControllerProfile> ProfilesOf(string consoleId)
        => string.IsNullOrEmpty(consoleId)
            ? Enumerable.Empty<ConsoleControllerProfile>()
            : Profiles.Where(p => string.Equals(p.ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase) && p.Forms.Count > 0);

    /// <summary>そのコンソールの最初のプリセット(コアを問わない旧形式の読み替えや、Wiiのようにコアが1つだけの用途向け)。無ければnull。</summary>
    public static ConsoleControllerProfile? FindByConsole(string consoleId) => ProfilesOf(consoleId).FirstOrDefault();

    /// <summary>コンソール+コア(dllのファイル名)のプリセット。そのコアがプリセットを持たなければnull(=パススルー)。</summary>
    public static ConsoleControllerProfile? FindProfile(string consoleId, string? coreFile)
        => string.IsNullOrEmpty(coreFile)
            ? null
            : ProfilesOf(consoleId).FirstOrDefault(p => string.Equals(p.Core, Path.GetFileName(coreFile), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 形態を持たない(=Wii以外の)コンソールに割り当てるプリセットのID。何も加工しない(パススルー):
    /// コアへ渡すボタンの絞り込みも、コア特有の差し替えも無く、物理入力は既定の割り当てでそのままRetroPadになる。
    /// </summary>
    public const string AnyPresetId = "any";

    /// <summary>そのスコープで使われるプリセットのID。形態が有効なら形態のID(例 "wii@nunchuk")、無ければ"any"。</summary>
    public static string ResolvePresetId(string scopeId) => FindForm(scopeId) is null ? AnyPresetId : scopeId;

    /// <summary>設定の置き場所のID。"wii@dolphin/nunchuk"(コンソール@コア/形態)。</summary>
    public static string MakeScope(ConsoleControllerProfile profile, string formId)
        => profile.ConsoleId + ScopeSeparator + profile.CoreKey + CoreFormSeparator + formId;

    public const char CoreFormSeparator = '/';

    /// <summary>"wii@nunchuk" → "wii"。形態でないIDはそのまま返す。</summary>
    public static string BaseConsoleId(string scopeId)
    {
        int i = scopeId.IndexOf(ScopeSeparator);
        return i < 0 ? scopeId : scopeId[..i];
    }

    public static bool IsFormScope(string scopeId) => scopeId.IndexOf(ScopeSeparator) >= 0;

    /// <summary>"wii@nunchuk"の形態の定義(無ければnull)。</summary>
    public static ControllerForm? FindForm(string scopeId)
    {
        ConsoleControllerProfile? profile = FindProfileOfScope(scopeId, out string formId);
        return profile is null ? null : FindForm(profile, formId);
    }

    /// <summary>スコープID("wii@dolphin/nunchuk")のプリセットと形態ID。旧形式"wii@nunchuk"(コア無し)はそのコンソールの最初のプリセット。</summary>
    public static ConsoleControllerProfile? FindProfileOfScope(string scopeId, out string formId)
    {
        formId = string.Empty;
        int i = scopeId.IndexOf(ScopeSeparator);
        if (i < 0) return null;

        string consoleId = scopeId[..i];
        string rest = scopeId[(i + 1)..];
        int j = rest.IndexOf(CoreFormSeparator);
        if (j < 0)
        {
            formId = rest;
            return FindByConsole(consoleId);
        }

        formId = rest[(j + 1)..];
        string coreKey = rest[..j];
        return ProfilesOf(consoleId).FirstOrDefault(p => string.Equals(p.CoreKey, coreKey, StringComparison.OrdinalIgnoreCase));
    }

    public static ControllerForm? FindForm(string consoleId, string? formId)
        => FindByConsole(consoleId) is { } profile ? FindForm(profile, formId) : null;

    public static ControllerForm? FindForm(ConsoleControllerProfile profile, string? formId)
    {
        if (string.IsNullOrEmpty(formId)) return null;

        // 以前の保存値"mp_nunchuk"などは、MotionPlusを分けなくなったので元の形態として扱う。
        if (formId.StartsWith("mp_", StringComparison.Ordinal)) formId = formId[3..];

        return profile.Forms.FirstOrDefault(f =>
            string.Equals(f.Id, formId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.OptionValue, formId, StringComparison.OrdinalIgnoreCase)
            || f.Aliases.Any(a => string.Equals(a, formId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 起動時に使う設定の置き場所。そのコアがプリセットを持ち、形態が有効ならスコープ(例 "wii@dolphin/nunchuk")、
    /// 無ければコンソールID(パススルー)。
    /// </summary>
    public static string ResolveScope(string consoleId, string? coreFile, string? formId)
    {
        ConsoleControllerProfile? profile = FindProfile(consoleId, coreFile);
        if (profile is null) return consoleId;
        ControllerForm? form = FindForm(profile, formId) ?? FindForm(profile, profile.Default);
        return form is null ? consoleId : MakeScope(profile, form.Id);
    }

    /// <summary>
    /// 以前の形式のスコープ"wii@nunchuk"(コア無し)を、いまの形式"wii@dolphin/nunchuk"へ読み替える。
    /// 読み替えられない(旧形式ではない/対応する形態が無い)場合はnull。
    /// </summary>
    public static string? MigrateLegacyScope(string scopeId)
    {
        int i = scopeId.IndexOf(ScopeSeparator);
        if (i < 0 || scopeId.IndexOf(CoreFormSeparator, i) >= 0) return null;

        ConsoleControllerProfile? profile = FindByConsole(scopeId[..i]);
        ControllerForm? form = profile is null ? null : FindForm(profile, scopeId[(i + 1)..]);
        return profile is null || form is null ? null : MakeScope(profile, form.Id);
    }

    /// <summary>設定の置き場所の重ね順(弱い方から)。"wii@nunchuk" → ["wii", "wii@nunchuk"]。</summary>
    public static IEnumerable<string> ScopeChain(string scopeId)
    {
        string baseId = BaseConsoleId(scopeId);
        if (!string.Equals(baseId, scopeId, StringComparison.Ordinal)) yield return baseId;
        yield return scopeId;
    }

    /// <summary>
    /// そのホットキーが、このスコープで使える機能か。形態がホットキーの一覧(hotkeys)を持っていれば、
    /// そこに書かれているものだけ。形態を持たない(any)場合や、一覧が無い形態は、すべて使える。
    /// </summary>
    public static bool IsHotkeyAvailable(string scopeId, string hotkeyKey)
        => hotkeyKey == ControllerMappingService.HotkeyPointerRecenter
            // 「ポインターを中央へ」はWii専用。形態の一覧に明記されているときだけ使える。
            ? FindForm(scopeId) is { } pointerForm && pointerForm.Hotkeys.ContainsKey(hotkeyKey)
            : FindForm(scopeId) is not { } form || form.Hotkeys.Count == 0 || form.Hotkeys.ContainsKey(hotkeyKey);

    /// <summary>
    /// 形態IDからデバイス値を引く。未知の値はnull。motionPlusがtrueで、その形態にMotionPlus付きの値があればそれを返す。
    /// 以前の保存値"mp_nunchuk"のように"mp_"で始まるIDは、MotionPlusあり+元の形態として扱う。
    /// </summary>
    public static uint? ResolveDevice(string consoleId, string? formId, bool motionPlus = false)
    {
        if (formId is not null && formId.StartsWith("mp_", StringComparison.Ordinal))
        {
            formId = formId[3..];
            motionPlus = true;
        }

        ControllerForm? form = FindForm(consoleId, formId);
        if (form is null) return null;
        return motionPlus && form.MotionPlusDevice is uint mp ? mp : form.Device;
    }
}
