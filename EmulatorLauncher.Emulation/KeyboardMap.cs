namespace EmulatorLauncher.Emulation;

/// <summary>
/// libretroのキーコード(RETROK_*。SDL1.2由来の値)から、SDL3のスキャンコード(USB HIDの値)への対応表。
/// 「キーボード入力をコアへ渡す」設定がONのとき、コアがRETRO_DEVICE_KEYBOARDで問い合わせたキーが
/// 押されているかを、SDL_GetKeyboardStateの配列から引くために使う。
/// Shiftが要る記号(RETROK_EXCLAIM等)は物理キーが別に無いので対象にしない。
/// </summary>
internal static class KeyboardMap
{
    /// <summary>システム設定パネルを開くキー(F1)。ゲームには渡さない。</summary>
    public const int SystemPanelScancode = 58;

    /// <summary>中断メニューを開くキー(F12)。ONの間もゲームには渡さない。</summary>
    public const int MenuScancode = 69;

    /// <summary>早送りの倍率を切り替えるキー(F11)。ONの間もゲームには渡さない。</summary>
    public const int FastForwardScancode = 68;

    private static readonly Dictionary<uint, int> Table = Build();

    // イベント通知用: スキャンコードごとに代表のRETROK_*を1つだけ持つ(同じ物理キーが2回通知されないように)。
    private static readonly Dictionary<int, uint> ByScancode = BuildByScancode();

    private static Dictionary<int, uint> BuildByScancode()
    {
        var result = new Dictionary<int, uint>();
        foreach (KeyValuePair<uint, int> kv in Table)
        {
            if (kv.Key == 293 || kv.Key == 292) continue; // F12=メニュー、F11=早送り
            result.TryAdd(kv.Value, kv.Key);
        }
        return result;
    }

    /// <summary>イベント通知の対象になる(スキャンコード, RETROK_*)の一覧。</summary>
    public static IEnumerable<KeyValuePair<int, uint>> EventKeys => ByScancode;

    /// <summary>RETROK_*に対応する文字(ASCIIの印字文字だけ。Shiftで英字は大文字)。無ければ0。</summary>
    public static uint CharacterOf(uint retrok, bool shift)
    {
        if (retrok >= 97 && retrok <= 122) return shift ? retrok - 32 : retrok;
        if (retrok == 8 || retrok == 9 || retrok == 13) return retrok;
        return retrok >= 32 && retrok <= 126 ? retrok : 0;
    }

    /// <summary>RETROK_*に対応するSDLスキャンコード。無ければ-1。F1(パネル)・F11(早送り)・F12(メニュー)はランチャー用なのでコアへ渡さない。</summary>
    public static int ToScancode(uint retrok)
        => retrok != 282 && retrok != 292 && retrok != 293 && Table.TryGetValue(retrok, out int code) ? code : -1;

    private static Dictionary<uint, int> Build()
    {
        var map = new Dictionary<uint, int>
        {
            [8] = 42,    // BACKSPACE
            [9] = 43,    // TAB
            [13] = 40,   // RETURN
            [19] = 72,   // PAUSE
            [27] = 41,   // ESCAPE
            [32] = 44,   // SPACE
            [39] = 52,   // QUOTE(')
            [44] = 54,   // COMMA
            [45] = 45,   // MINUS
            [46] = 55,   // PERIOD
            [47] = 56,   // SLASH
            [59] = 51,   // SEMICOLON
            [61] = 46,   // EQUALS
            [91] = 47,   // LEFTBRACKET
            [92] = 49,   // BACKSLASH
            [93] = 48,   // RIGHTBRACKET
            [96] = 53,   // BACKQUOTE
            [127] = 76,  // DELETE
            [266] = 99,  // KP_PERIOD
            [267] = 84,  // KP_DIVIDE
            [268] = 85,  // KP_MULTIPLY
            [269] = 86,  // KP_MINUS
            [270] = 87,  // KP_PLUS
            [271] = 88,  // KP_ENTER
            [272] = 103, // KP_EQUALS
            [273] = 82,  // UP
            [274] = 81,  // DOWN
            [275] = 79,  // RIGHT
            [276] = 80,  // LEFT
            [277] = 73,  // INSERT
            [278] = 74,  // HOME
            [279] = 77,  // END
            [280] = 75,  // PAGEUP
            [281] = 78,  // PAGEDOWN
            [300] = 83,  // NUMLOCK
            [301] = 57,  // CAPSLOCK
            [302] = 71,  // SCROLLOCK
            [303] = 229, // RSHIFT
            [304] = 225, // LSHIFT
            [305] = 228, // RCTRL
            [306] = 224, // LCTRL
            [307] = 230, // RALT
            [308] = 226, // LALT
            [309] = 231, // RMETA
            [310] = 227, // LMETA
            [311] = 227, // LSUPER
            [312] = 231, // RSUPER
            [315] = 117, // HELP
            [316] = 70,  // PRINT
            [317] = 154, // SYSREQ
            [318] = 72,  // BREAK
            [319] = 118, // MENU
            [322] = 122, // UNDO
        };

        for (uint i = 0; i < 26; i++) map[97 + i] = 4 + (int)i;            // a-z
        for (uint i = 1; i <= 9; i++) map[48 + i] = 29 + (int)i;            // 1-9
        map[48] = 39;                                                       // 0
        for (uint i = 0; i < 12; i++) map[282 + i] = 58 + (int)i;           // F1-F12
        for (uint i = 0; i < 3; i++) map[294 + i] = 104 + (int)i;           // F13-F15
        for (uint i = 1; i <= 9; i++) map[256 + i] = 88 + (int)i;           // KP1-9
        map[256] = 98;                                                      // KP0
        return map;
    }
}
