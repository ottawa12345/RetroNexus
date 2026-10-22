using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// libretroコアの環境コールバック(RETRO_ENVIRONMENT_*)のうち、「コアオプション」
/// (SET_VARIABLES/GET_VARIABLE/GET_VARIABLE_UPDATE)、System/Save Directoryの申告、
/// および GET_LOG_INTERFACE を担当するクラス。<see cref="EmulationHost"/>の内部分割
/// (4回目)の一環として切り出した。
///
/// SET_HW_RENDER やSET_PIXEL_FORMAT、SET_GEOMETRY等はHWレンダリング/映像パイプライン
/// (EmulationHost.Video.cs)と密結合しているため、あえてここには含めず
/// EmulationHost.EnvironmentCallback側に残している(無理に分けると、GLコンテキストの
/// 生成・破棄という1つの責務が2つのクラスに分散してしまうため)。
/// 一方、ここに含めた「コアオプション」周りは映像/音声/入力のどの状態にも触れない
/// 自己完結した処理であり、今後コア個別のオプションUIを増やす際に触るのもほぼ
/// このクラスだけになる。
/// </summary>
public sealed class CoreEnvironmentSupport
{
    private readonly Func<string> _getSystemDirectory;
    private readonly Func<string> _getSaveDirectory;
    private readonly Action<string> _reportStatus;

    // GET_SYSTEM_DIRECTORY / GET_SAVE_DIRECTORY 用にキャッシュしたネイティブ文字列。
    private nint _systemDirPtr;
    private nint _saveDirPtr;

    // SET_VARIABLES で受け取ったコアオプション(key -> 詳細情報)。
    // GET_VARIABLE から汎用的に参照する。コア固有のハードコードはしない。
    private sealed class CoreVariableInfo
    {
        public required string Key;
        public required string Description;
        public required List<string> Choices;
        public required string CurrentValue;
    }

    private readonly object _variablesLock = new();
    private readonly Dictionary<string, CoreVariableInfo> _variables = new();

    // Start()（_core.Init()の呼び出し。SET_VARIABLESはこの中で申告される）より前に
    // UIスレッドからSetInitialOverrideで積んでおく「起動直後から使いたい値」。
    // RequestSetCoreVariable/_pendingChangesはSET_VARIABLES申告"後"にしか効かない
    // (HandleSetVariablesがCurrentValueを毎回choices[0]で上書きしてしまうため、
    // それより後に積んだ変更しか反映されない)のに対し、こちらはHandleSetVariables
    // 自体にchoices[0]の代わりに使わせる値であり、"起動時から効かせたい"設定
    // (内部解像度のように、コアがretro_load_game内で最初にGET_VARIABLEを読んだ
    // 時点の値がそのままフレームバッファの確保サイズ等に影響してしまうもの)に使う。
    // Start()呼び出し前(=専用スレッド開始前)にのみ書き込む前提のため、専用の
    // ロックは設けていない(スレッド生成自体がメモリバリアになる)。
    private readonly Dictionary<string, string> _initialOverrides = new();

    // GET_VARIABLE で書き戻すネイティブ文字列のキャッシュ(key -> 確保済みポインタ)。
    // ApplyPendingChanges/GET_VARIABLEはどちらもエミュレーションスレッドのループ内でしか
    // 呼ばれないため、こちらはロック不要。
    private readonly Dictionary<string, nint> _valuePtrs = new();

    // UIスレッドから「このコアオプションをこの値にしたい」という要求を受け取るキュー。
    // 実際の反映はエミュレーションスレッドのループ内でのみ行う。
    private readonly ConcurrentQueue<(string Key, string Value)> _pendingChanges = new();

    // GET_VARIABLE_UPDATE で一度だけ true を返すためのフラグ(エミュレーションスレッドのみが触る)。
    private bool _optionsUpdated;

    private RetroLogPrintfCallback? _logPrintfCallback;

    public CoreEnvironmentSupport(Func<string> getSystemDirectory, Func<string> getSaveDirectory, Action<string> reportStatus)
    {
        _getSystemDirectory = getSystemDirectory;
        _getSaveDirectory = getSaveDirectory;
        _reportStatus = reportStatus;
    }

    /// <summary>コアが申告している「ビデオフィルター」系コアオプション1件分の情報。</summary>
    public sealed class VideoFilterOption
    {
        public required string Key { get; init; }
        public required string Description { get; init; }
        public required IReadOnlyList<string> Choices { get; init; }
        public required string CurrentValue { get; init; }
    }

    /// <summary>
    /// 現在ロード中のコアが SET_VARIABLES で申告したコアオプションのうち、
    /// キーまたは説明文に "filter" を含むもの(=ビデオフィルター系)だけを抽出して返す。
    /// コアがビデオフィルターに相当するオプションを持っていなければ空配列。
    /// UIスレッドから呼び出し可能。
    /// </summary>
    public IReadOnlyList<VideoFilterOption> GetVideoFilterOptions()
    {
        lock (_variablesLock)
        {
            return _variables.Values
                .Where(v => v.Key.Contains("filter", StringComparison.OrdinalIgnoreCase)
                         || v.Description.Contains("filter", StringComparison.OrdinalIgnoreCase))
                .Select(v => new VideoFilterOption
                {
                    Key = v.Key,
                    Description = v.Description,
                    Choices = v.Choices,
                    CurrentValue = v.CurrentValue,
                })
                .ToList();
        }
    }

    /// <summary>
    /// Start()（=コアのInit()/LoadGame()）より前に呼んでおく、「このコアオプションは
    /// 起動直後からこの値にしておきたい」という予約。HandleSetVariablesがコアの
    /// 申告(choices[0]がコア自身の既定値)を受け取る際、対応するキーがここにあれば
    /// (かつ申告された選択肢の中に実在すれば)その値をCurrentValueの初期値として使う。
    ///
    /// RequestSetCoreVariable/ApplyPendingChangesとの違い: あちらはSET_VARIABLES申告
    /// "後"にしか効かない(HandleSetVariablesが毎回choices[0]で上書きするため、それより
    /// 前に積んでも上書きされて消える)うえ、実際の適用はエミュレーションスレッドの
    /// ループが1回まわった後(=retro_load_game完了・retro_get_system_av_info取得・
    /// HWレンダリング用FBO確保が全部終わった後)になる。内部解像度のように、コアが
    /// retro_load_game内で最初にGET_VARIABLEを読んだ結果がそのままフレームバッファの
    /// サイズ計算(retro_get_system_av_infoのgeometry.max_width/max_height)に
    /// 影響するような設定では、それでは遅すぎる。こちらはHandleSetVariables自身に
    /// 初期値として使わせるため、コアが最初に読む時点から反映済みになる。
    /// </summary>
    public void SetInitialOverride(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
        _initialOverrides[key] = value;
    }

    /// <summary>SetInitialOverrideで予約された値(無ければnull)。コアのオプションではなくホスト側の設定を読むのにも使う。</summary>
    public string? GetInitialOverride(string key)
        => _initialOverrides.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 指定したコアオプション(ビデオフィルター等)の値を変更する。実際の反映は次に
    /// エミュレーションスレッドのループが回ったタイミング(<see cref="ApplyPendingChanges"/>)で
    /// 行われる。
    /// </summary>
    public void RequestSetCoreVariable(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        _pendingChanges.Enqueue((key, value));
    }

    /// <summary>RequestSetCoreVariable() の実処理。エミュレーションスレッドのループ内から呼ぶ。</summary>
    public void ApplyPendingChanges()
    {
        while (_pendingChanges.TryDequeue(out var change))
        {
            ApplyChangeInternal(change.Key, change.Value);
        }
    }

    /// <summary>コアが申告済みのオプションかどうか。UIスレッド/エミュレーションスレッドのどちらからでも可。</summary>
    public bool HasVariable(string key)
    {
        lock (_variablesLock) return _variables.ContainsKey(key);
    }

    /// <summary>オプションの現在値。未申告ならnull。</summary>
    public string? GetVariableValue(string key)
    {
        lock (_variablesLock) return _variables.TryGetValue(key, out var info) ? info.CurrentValue : null;
    }

    /// <summary>
    /// オプションの値を、ステータス表示なしで即座に変える(エミュレーションスレッド専用)。
    /// 「ステートセーブ画像を撮る間だけ画面レイアウトを一時的に変える」ような内部用途向けで、
    /// UI向けのRequestSetCoreVariableと違い、ステータス行を更新しない。
    /// </summary>
    public void SetVariableQuiet(string key, string value) => ApplyChangeInternal(key, value, quiet: true);

    private void ApplyChangeInternal(string key, string value, bool quiet = false)
    {
        string? description = null;

        lock (_variablesLock)
        {
            if (!_variables.TryGetValue(key, out var info)) return;
            if (info.CurrentValue == value) return;

            info.CurrentValue = value;
            description = info.Description;
        }

        // 次回 GET_VARIABLE が呼ばれた時に新しい値を返せるよう、キャッシュ済みのネイティブ
        // 文字列ポインタを破棄する。
        if (_valuePtrs.TryGetValue(key, out var oldPtr))
        {
            Marshal.FreeHGlobal(oldPtr);
            _valuePtrs.Remove(key);
        }

        _optionsUpdated = true;
        if (!quiet) _reportStatus($"{description}: {value}");
    }

    public bool HandleGetSystemDirectory(nint data)
    {
        if (data == nint.Zero) return false;

        if (_systemDirPtr == nint.Zero)
        {
            string dir = _getSystemDirectory();
            System.IO.Directory.CreateDirectory(dir);
            _systemDirPtr = Marshal.StringToHGlobalAnsi(dir);
        }
        Marshal.WriteIntPtr(data, _systemDirPtr);
        return true;
    }

    public bool HandleGetSaveDirectory(nint data)
    {
        if (data == nint.Zero) return false;

        if (_saveDirPtr == nint.Zero)
        {
            string dir = _getSaveDirectory();
            System.IO.Directory.CreateDirectory(dir);
            _saveDirPtr = Marshal.StringToHGlobalAnsi(dir);
        }
        Marshal.WriteIntPtr(data, _saveDirPtr);
        return true;
    }

    /// <summary>
    /// コアが「これらのオプションキーが存在する」と申告してくる。
    /// data は retro_variable(key, value) の配列で、keyがNULLになるまで続く。
    /// value側は "説明文; デフォルト値|選択肢2|選択肢3" という形式の文字列。
    /// </summary>
    public bool HandleSetVariables(nint data)
    {
        if (data == nint.Zero) return true;

        int entrySize = Marshal.SizeOf<RetroVariableRaw>();
        nint p = data;

        lock (_variablesLock)
        {
            while (true)
            {
                var raw = Marshal.PtrToStructure<RetroVariableRaw>(p);
                if (raw.key == nint.Zero) break;

                string key = Marshal.PtrToStringAnsi(raw.key) ?? "";
                string desc = Marshal.PtrToStringAnsi(raw.value) ?? "";

                if (!string.IsNullOrEmpty(key))
                {
                    int semi = desc.IndexOf(';');
                    string description = semi >= 0 ? desc[..semi].Trim() : key;
                    string choicesRaw = semi >= 0 ? desc[(semi + 1)..].Trim() : desc;

                    var choices = choicesRaw.Split('|')
                        .Select(c => c.Trim())
                        .Where(c => c.Length > 0)
                        .ToList();
                    if (choices.Count == 0) choices.Add("");

                    // SetInitialOverrideで予約された値があり、かつコアが実際に申告した
                    // 選択肢の中に含まれていれば、コア自身の既定値(choices[0])の代わりに
                    // それを初期値として使う(バージョン差異等でchoicesに存在しない値が
                    // 予約されていた場合は、安全側に倒してコアの既定値を使う)。
                    string initialValue = choices[0];
                    bool hasOverride = _initialOverrides.TryGetValue(key, out string? overrideValue);
                    bool overrideAccepted = hasOverride && overrideValue is not null && choices.Contains(overrideValue);
                    if (overrideAccepted)
                    {
                        initialValue = overrideValue!;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[CoreEnvironmentSupport] SET_VARIABLES: key=\"{key}\" "
                        + $"choices=[{string.Join(",", choices)}] coreDefault=\"{choices[0]}\" "
                        + $"override={(hasOverride ? $"\"{overrideValue}\"" : "(none)")} "
                        + $"accepted={overrideAccepted} => CurrentValue=\"{initialValue}\"");


                    _variables[key] = new CoreVariableInfo
                    {
                        Key = key,
                        Description = description,
                        Choices = choices,
                        CurrentValue = initialValue,
                    };
                }

                p += entrySize;
            }
        }
        return true;
    }

    // retro_core_option_value は {const char* value; const char* label;}、
    // 1定義あたり最大 RETRO_NUM_CORE_OPTION_VALUES_MAX(=128) 個を固定長配列で持つ。
    private const int CoreOptionValuesMax = 128;
    private const int PtrSize = 8; // x64のみ対応

    /// <summary>
    /// SET_CORE_OPTIONS_V2(67)。data は retro_core_options_v2 { categories*, definitions* }。
    /// definitions は retro_core_option_v2_definition の配列(keyがNULLで終端):
    /// key, desc, desc_categorized, info, info_categorized, category_key, values[128], default_value。
    /// </summary>
    public bool HandleSetCoreOptionsV2(nint data)
    {
        if (data == nint.Zero) return true;
        nint definitions = Marshal.ReadIntPtr(data, PtrSize); // categoriesの次
        ParseCoreOptionDefinitions(definitions, textFields: 6);
        return true;
    }

    /// <summary>SET_CORE_OPTIONS_V2_INTL(68)。data は { us*(retro_core_options_v2), local* }。usだけ使う。</summary>
    public bool HandleSetCoreOptionsV2Intl(nint data)
    {
        if (data == nint.Zero) return true;
        nint us = Marshal.ReadIntPtr(data);
        return HandleSetCoreOptionsV2(us);
    }

    /// <summary>
    /// SET_CORE_OPTIONS(53)。data は retro_core_option_definition の配列(keyがNULLで終端):
    /// key, desc, info, values[128], default_value。
    /// </summary>
    public bool HandleSetCoreOptionsV1(nint data)
    {
        if (data == nint.Zero) return true;
        ParseCoreOptionDefinitions(data, textFields: 3);
        return true;
    }

    /// <summary>SET_CORE_OPTIONS_INTL(54)。data は { us*(定義配列), local* }。usだけ使う。</summary>
    public bool HandleSetCoreOptionsV1Intl(nint data)
    {
        if (data == nint.Zero) return true;
        nint us = Marshal.ReadIntPtr(data);
        return HandleSetCoreOptionsV1(us);
    }

    /// <summary>
    /// V1/V2共通の定義配列の読み取り。textFields は values[] の前にある文字列ポインタの数
    /// (V1=3: key,desc,info / V2=6: key,desc,desc_categorized,info,info_categorized,category_key)。
    /// 先頭2つが key, desc。values[] の後ろに default_value が1つ続く。
    /// </summary>
    private void ParseCoreOptionDefinitions(nint definitions, int textFields)
    {
        if (definitions == nint.Zero) return;

        int valuesOffset = textFields * PtrSize;
        int defaultOffset = valuesOffset + CoreOptionValuesMax * 2 * PtrSize;
        int stride = defaultOffset + PtrSize;

        lock (_variablesLock)
        {
            for (nint p = definitions; ; p += stride)
            {
                nint keyPtr = Marshal.ReadIntPtr(p);
                if (keyPtr == nint.Zero) break;

                string key = Marshal.PtrToStringAnsi(keyPtr) ?? "";
                nint descPtr = Marshal.ReadIntPtr(p, PtrSize);
                string description = (descPtr != nint.Zero ? Marshal.PtrToStringAnsi(descPtr) : null) ?? key;
                if (string.IsNullOrEmpty(key)) continue;

                var choices = new List<string>();
                for (int i = 0; i < CoreOptionValuesMax; i++)
                {
                    nint valPtr = Marshal.ReadIntPtr(p, valuesOffset + i * 2 * PtrSize);
                    if (valPtr == nint.Zero) break;
                    string v = Marshal.PtrToStringAnsi(valPtr) ?? "";
                    if (v.Length > 0) choices.Add(v);
                }

                nint defPtr = Marshal.ReadIntPtr(p, defaultOffset);
                string? coreDefault = defPtr != nint.Zero ? Marshal.PtrToStringAnsi(defPtr) : null;

                // valuesが空(自由入力型)の定義は、default_valueだけを選択肢として持たせる。
                if (choices.Count == 0) choices.Add(coreDefault ?? "");
                if (coreDefault is null || !choices.Contains(coreDefault)) coreDefault = choices[0];

                // SetInitialOverrideの予約値は、実在する選択肢のときだけコアの既定値より優先する。
                bool hasOverride = _initialOverrides.TryGetValue(key, out string? overrideValue);
                bool overrideAccepted = hasOverride && overrideValue is not null && choices.Contains(overrideValue);
                string initialValue = overrideAccepted ? overrideValue! : coreDefault;

                System.Diagnostics.Debug.WriteLine(
                    $"[CoreEnvironmentSupport] SET_CORE_OPTIONS: key=\"{key}\" "
                    + $"choices=[{string.Join(",", choices)}] coreDefault=\"{coreDefault}\" "
                    + $"override={(hasOverride ? $"\"{overrideValue}\"" : "(none)")} "
                    + $"accepted={overrideAccepted} => CurrentValue=\"{initialValue}\"");

                _variables[key] = new CoreVariableInfo
                {
                    Key = key,
                    Description = description,
                    Choices = choices,
                    CurrentValue = initialValue,
                };
            }
        }
    }

    /// <summary>
    /// SET_VARIABLESで保存しておいた値(または後からUIが変更した値)を返す。
    /// 未知のキーはfalseを返し、コア内部のデフォルトにフォールバックさせる(これも正常な応答)。
    /// </summary>
    public bool HandleGetVariable(nint data)
    {
        if (data == nint.Zero) return false;

        nint keyPtr = Marshal.ReadIntPtr(data);
        string key = Marshal.PtrToStringAnsi(keyPtr) ?? "";

        string value;
        lock (_variablesLock)
        {
            if (!_variables.TryGetValue(key, out var info))
            {
                System.Diagnostics.Debug.WriteLine($"[CoreEnvironmentSupport] GET_VARIABLE: key=\"{key}\" => 未申告のキーのため false を返却");
                return false;
            }
            value = info.CurrentValue;
        }

        //System.Diagnostics.Debug.WriteLine($"[CoreEnvironmentSupport] GET_VARIABLE: key=\"{key}\" => \"{value}\"");


        if (!_valuePtrs.TryGetValue(key, out var valuePtr))
        {
            valuePtr = Marshal.StringToHGlobalAnsi(value);
            _valuePtrs[key] = valuePtr;
        }
        Marshal.WriteIntPtr(data, nint.Size, valuePtr);
        return true;
    }

    /// <summary>
    /// UIから RequestSetCoreVariable() でオプションが変更された直後、1回だけ true を返す。
    /// これにより、対応しているコアは「オプションが変わった」ことに気づいて
    /// GET_VARIABLE を呼び直してくれる(ビデオフィルター切り替え等に必要)。
    /// </summary>
    public bool HandleGetVariableUpdate(nint data)
    {
        if (data == nint.Zero) return false;
        Marshal.WriteByte(data, (byte)(_optionsUpdated ? 1 : 0));
        _optionsUpdated = false;
        return true;
    }

    public bool HandleGetLogInterface(nint data)
    {
        if (data == nint.Zero) return false;

        _logPrintfCallback ??= CoreLogPrintf;
        var cb = new RetroLogCallbackRaw
        {
            log = Marshal.GetFunctionPointerForDelegate(_logPrintfCallback)
        };
        Marshal.StructureToPtr(cb, data, false);
        return true;
    }

    /// <summary>コアがログ関数を呼んだ回数(全レベル)。巻き戻しの遅さの原因調査に使う。</summary>
    internal static long LogCallCount;

    private void CoreLogPrintf(int level, nint fmt, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6)
    {
        System.Threading.Interlocked.Increment(ref LogCallCount);

        // ロード中以外の DEBUG/INFO は、どこにも書き出さない(AppLogが捨てる)ので、
        // 文字列の組み立てや Debug.WriteLine(Visual Studio上では非常に遅い)も行わない。
        // コアによっては、ステートの保存のような処理の最中に大量のログを出すため。
        if (level < 2 && !EmulatorLauncher.Services.AppLog.CoreVerbose) return;

        // 可変引数(printf形式)を、Windows x64の呼び出し規約(可変引数も通常の引数と同じく
        // レジスタ/スタックに整数として並ぶ)を利用して最大6個まで展開する簡易ロガー。
        // 実際の引数がそれより少なくても、余分な引数はフォーマットで参照されない限り読まれない。
        string msg;
        try
        {
            msg = FormatPrintf(fmt, new[] { a1, a2, a3, a4, a5, a6 }).TrimEnd('\r', '\n');
        }
        catch
        {
            msg = Marshal.PtrToStringAnsi(fmt) ?? "";
        }
        System.Diagnostics.Debug.WriteLine($"[core log {level}] {msg}");
        EmulatorLauncher.Services.AppLog.Core(level, msg);
    }

    /// <summary>%s %d %i %u %x %X %c %p %f %% と長さ修飾子(l/ll/z/h)・幅・フラグだけを解釈する簡易printf。</summary>
    private static string FormatPrintf(nint fmtPtr, nint[] args)
    {
        string fmt = Marshal.PtrToStringAnsi(fmtPtr) ?? "";
        var sb = new System.Text.StringBuilder();
        int argIndex = 0;

        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%') { sb.Append(c); continue; }
            if (++i >= fmt.Length) break;
            if (fmt[i] == '%') { sb.Append('%'); continue; }

            string flags = "";
            while (i < fmt.Length && "-+ #0".IndexOf(fmt[i]) >= 0) flags += fmt[i++];
            int width = 0;
            while (i < fmt.Length && char.IsDigit(fmt[i])) width = width * 10 + (fmt[i++] - '0');
            int precision = -1;
            if (i < fmt.Length && fmt[i] == '.')
            {
                i++; precision = 0;
                while (i < fmt.Length && char.IsDigit(fmt[i])) precision = precision * 10 + (fmt[i++] - '0');
            }
            int longCount = 0; bool isShort = false;
            while (i < fmt.Length && "lzhjtqL".IndexOf(fmt[i]) >= 0)
            {
                if (fmt[i] == 'h') isShort = true; else longCount++;
                i++;
            }
            if (i >= fmt.Length) break;

            char conv = fmt[i];
            nint raw = argIndex < args.Length ? args[argIndex] : nint.Zero;
            argIndex++;
            string piece;
            switch (conv)
            {
                case 's':
                    piece = raw == nint.Zero ? "(null)" : (Marshal.PtrToStringAnsi(raw) ?? "");
                    if (precision >= 0 && piece.Length > precision) piece = piece.Substring(0, precision);
                    break;
                case 'd':
                case 'i':
                    piece = (longCount > 0 ? (long)raw : isShort ? (short)raw : (int)raw).ToString();
                    break;
                case 'u':
                    piece = (longCount > 0 ? (ulong)raw : isShort ? (ushort)raw : (uint)raw).ToString();
                    break;
                case 'x':
                    piece = (longCount > 0 ? ((ulong)raw).ToString("x") : ((uint)raw).ToString("x"));
                    break;
                case 'X':
                    piece = (longCount > 0 ? ((ulong)raw).ToString("X") : ((uint)raw).ToString("X"));
                    break;
                case 'c':
                    piece = ((char)(byte)raw).ToString();
                    break;
                case 'p':
                    piece = "0x" + ((ulong)raw).ToString("x");
                    break;
                case 'f':
                case 'g':
                case 'e':
                    // Win64では可変引数のdoubleは整数レジスタ側にもビット列として複製される。
                    piece = BitConverter.Int64BitsToDouble((long)raw).ToString(precision >= 0 ? "F" + precision : "G6");
                    break;
                default:
                    piece = "%" + conv;
                    break;
            }

            if (piece.Length < width)
                piece = flags.Contains('-') ? piece.PadRight(width)
                      : (flags.Contains('0') && conv != 's' ? piece.PadLeft(width, '0') : piece.PadLeft(width));
            sb.Append(piece);
        }
        return sb.ToString();
    }

    /// <summary>エミュレーション終了時の後始末(ネイティブ確保分の解放)。</summary>
    public void Dispose()
    {
        if (_systemDirPtr != nint.Zero) Marshal.FreeHGlobal(_systemDirPtr);
        if (_saveDirPtr != nint.Zero) Marshal.FreeHGlobal(_saveDirPtr);
        _systemDirPtr = nint.Zero;
        _saveDirPtr = nint.Zero;

        foreach (var ptr in _valuePtrs.Values)
        {
            if (ptr != nint.Zero) Marshal.FreeHGlobal(ptr);
        }
        _valuePtrs.Clear();

        lock (_variablesLock)
        {
            _variables.Clear();
        }
    }
}
