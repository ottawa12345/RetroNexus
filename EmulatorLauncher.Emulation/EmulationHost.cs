using EmulatorLauncher.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using EmulatorLauncher.Input;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// libretroコアを専用スレッド上でロードし、SDL3で映像・音声・入力を処理しながら
/// retro_run() を回し続けるホストクラス。
///
/// 設計上の注意(delegateのライフタイム):
/// libretroコールバック(video_refresh等)は、コア内部に「関数ポインタ」として
/// 保持され続け、フレーム毎に何度も呼び返される。疑いの余地を無くすため、
/// 本実装ではコールバックを「明示的なインスタンスメソッド」として実装し、
/// 状態はすべてフィールドに保持する。delegateインスタンス自体もフィールドに
/// 保持し、GCされないことを保証する。
///
/// ウィンドウ埋め込みについて:
/// WinUI3のメインウィンドウに"所有(owned)"された独立したトップレベルウィンドウ
/// (WS_POPUP)として描画用ウィンドウを作り、位置・サイズをXAML側のプレースホルダに
/// 追従させる。WS_CHILDで埋め込むと、WinUI3のDirectComposition合成層に隠れてしまう
/// (Airspace問題)ため採用していない。
/// </summary>
public sealed partial class EmulationHost
{
    private readonly nint _parentHwnd;
    private readonly string _corePath;
    private readonly string _romPath;

    /// <summary>コアがSET_SUPPORT_NO_GAME(ゲーム無し起動に対応)をtrueで申告したか。</summary>
    private volatile bool _coreSupportsNoGame;

    private Thread? _thread;
    private volatile nint _childHwnd;
    private volatile bool _running;

    private readonly object _boundsLock = new();
    private (int X, int Y, int W, int H) _pendingBounds = (0, 0, 640, 480);
    private bool _boundsDirty = true;
    private (int X, int Y, int W, int H) _currentBounds = (0, 0, 640, 480);

    // --- 一時停止(メニュー表示)関連 ---
    // UIスレッドから Pause()/Resume() で書き換えられ、エミュレーションスレッドの
    // ループが毎周チェックする。実際の副作用(音声停止・描画ウィンドウ非表示など)は
    // ループ側でまとめて行うことで、エミュレーションスレッド以外からSDL/コアの
    // 状態を直接触らないようにしている。
    private volatile bool _pauseRequested;
    private bool _pausedInternal;

    // リセットも同様に「要求フラグを立てるだけ」にして、実際の retro_reset() 呼び出しは
    // 必ずエミュレーションスレッドのループ内で行う。
    private volatile bool _resetRequested;

    /// <summary>現在一時停止中かどうか(エミュレーションスレッドが最後に反映した状態)。</summary>
    public bool IsPaused => _pausedInternal;

    // --- エミュレーションスレッド内でのみ使用するランタイム状態 ---
    private nint _sdlWindow;
    private nint _glContext;

    // ================= SWレンダリング用: SDL_Renderer経由の描画 =================
    // raw GL(手動glViewport/コンテキスト管理)でのウィンドウリサイズ追従が
    // どうしても安定しなかったため、SWレンダリングのコア(大多数)は
    // SDL自身がリサイズを内部で処理してくれるSDL_Rendererへ切り替えている。
    // (HWレンダリングのコアは、FBOへの直接描画が必要なためraw GLのまま)
    private nint _renderer;
    private nint _sdlTexture;
    private uint _sdlTexW;
    private uint _sdlTexH;
    // SDL_Rendererに最後に設定した論理解像度(SDL_SetRenderLogicalPresentation)。
    // 回転描画時は幅と高さを入れ替えて設定する必要があるため、テクスチャの
    // 実サイズ(_sdlTexW/H)とは別に、実際に設定した論理解像度を覚えておく
    // (変化した時だけ呼び直すためのキャッシュ。RenderSwFrameViaSdlRenderer参照)。
    private int _sdlLogicalW = -1;
    private int _sdlLogicalH = -1;
    private LibretroCore? _core;
    private RetroPixelFormat _pixelFormat = RetroPixelFormat.XRGB1555;

    /// <summary>
    /// コアがRETRO_ENVIRONMENT_SET_ROTATIONで申告してきた回転量(0〜3)。
    /// libretroの定義そのまま: 0=回転なし、1=反時計回りに90度、2=180度、3=反時計回りに270度。
    /// アーケード系コア(FBNeo等)の縦画面(TATE)ゲームのように、コアが生のフレームバッファは
    /// 元の向き(横長)のまま渡し、実際の表示回転をフロントエンド側に委ねてくることがあるため
    /// 必要になる。特定コア名に依存せず、SET_ROTATIONを送ってくる全コアに対して汎用的に扱う。
    /// </summary>
    private uint _coreRotationQuarterTurns;

    // --- 音声・入力・コアオプション周りは、内部分割(4回目)で実クラスへ切り出した ---
    // (partial classのファイル分割ではなく、独立したインスタンスとして保持する)。
    private readonly AudioProcessor _audio;
    private readonly GamepadInputMapper _input;
    private readonly CoreEnvironmentSupport _coreEnv;
    private readonly CoreMotionRumble _motionRumble = new();

    // ================= 早送り(R+ZR / L+ZL同時押しでサイクル切り替え) =================
    // 1倍(通常)を起点に、R+ZRを押すたびに 2倍 → 3倍 → 1倍 … と順方向に、
    // L+ZLを押すたびに 3倍 → 2倍 → 1倍 … と逆方向に巡回する(どちらも1倍を
    // 挟んで循環する点は同じで、進む向きだけが逆)。
    // 検出自体はGamepadInputMapper.DetectFastForwardCombo(R+ZR、順方向)/
    // DetectFastForwardReverseCombo(L+ZL、逆方向)の中で行う(いずれもコアが毎フレーム
    // 呼ぶretro_input_poll経由)。それぞれFastForwardComboPressed/
    // FastForwardReverseComboPressedイベント経由でCycleFastForwardMultiplier(reverse)を
    // 呼ぶ(下記コンストラクタ参照)。
    private static readonly int[] FastForwardMultipliers = { 1, 2, 3 };
    // 書き込みはエミュレーションスレッド(CycleFastForwardMultiplier)からのみ行うが、
    // 公開プロパティ(FastForwardMultiplier)経由でUIスレッドから読まれる可能性があるため、
    // 他の同種フィールド(_pauseRequested等)に合わせてvolatileにしておく。
    private volatile int _fastForwardIndex;

    /// <summary>現在の早送り倍率(1が通常速度)。</summary>
    public int FastForwardMultiplier => FastForwardMultipliers[_fastForwardIndex];

    /// <summary>
    /// 早送り倍率が切り替わるたびに発生する(新しい倍率を引数で渡す)。UI側で
    /// 「2倍」「3倍」のような表示をする場合に使う想定。呼び出しはエミュレーション
    /// スレッドから行われるため、UI側は必ずDispatcherQueue経由でディスパッチすること。
    /// </summary>
    public event EventHandler<int>? FastForwardMultiplierChanged;

    // --- ソフトウェア描画パス用のGLテクスチャ(旧SDL_Texture相当) ---
    private uint _swTexture;
    private int _swTexW;
    private int _swTexH;
    // フォーマット変換したRGB8ピクセルの使い回しバッファ(毎フレームのアロケーションを避ける)
    private byte[] _swRgbBuffer = Array.Empty<byte>();

    // --- HWレンダリング(OpenGL, N64コア等)関連 ---
    // コアが RETRO_ENVIRONMENT_SET_HW_RENDER で「ソフトではなくGLで直接描画したい」と
    // 申告してきた場合に有効になる。コアは retro_run() の中で、
    // get_current_framebuffer が返すFBOへ自分でGL描画を行い、video_refresh には
    // 実データの代わりに RetroHwFrameBuffer.Valid を渡してくる。
    private bool _isHwRendered;

    /// <summary>
    /// 現在ロード中のコアがHWレンダリング(SET_HW_RENDER)を要求しているかどうか。
    /// HWレンダリング中は、生ピクセルバッファが取れないため巻き戻し機能は使えず、
    /// オートセーブも(この実装では)無効化している。UIスレッドから参照可能。
    /// </summary>
    public bool IsHwRendered => _isHwRendered;
    private bool _hwDepth;
    private bool _hwStencil;
    /// <summary>コアが申告した retro_hw_render_callback.bottom_left_origin。falseのコア(DeSmuME等)は
    /// 画像を上から下の並びでFBOに描くため、画面へ転送する際に上下反転が必要になる(N64/Dolphin/melonDSはtrue)。</summary>
    private bool _hwBottomLeftOrigin = true;
    private nint _hwContextResetPtr;
    private nint _hwContextDestroyPtr;
    private RetroHwGetCurrentFramebufferCallback? _hwGetCurrentFramebufferCallback;
    private RetroHwGetProcAddressCallback? _hwGetProcAddressCallback;
    private uint _hwFbo;
    private uint _hwColorTex;
    private uint _hwDepthStencilRbo;
    private int _hwFboWidth;
    private int _hwFboHeight;

    // --- サムネイル読み戻し用の小さなFBO(HWレンダリング時のスナップショット用) ---
    private uint _hwThumbFbo;
    private uint _hwThumbTex;
    private int _hwThumbFboWidth;
    private int _hwThumbFboHeight;
    private byte[] _hwThumbReadBuf = Array.Empty<byte>();
    public double CoreReportedAspectRatio { get; private set; } = 4.0 / 3.0;

    // ==================================================================
    // コアオプション変更時に「強制的に実行したい処理」を、cores/libretro.json
    // 側の各オプション定義(LibretroCoreOptionInfo.Actions)からアクション名で
    // 指定できるようにする汎用の仕組み。
    //
    // 背景: 一部のコア/オプションは、値を変えてもlibretro本来の通知手段
    // (RETRO_ENVIRONMENT_SET_GEOMETRY等)を呼ばず、フロントエンドが気づけない
    // ことがある(実機検証で確認した例: melonDSのmelonds_screen_layoutを
    // Top/Bottom等へ切り替えても、SET_GEOMETRYは一切来ず、次のvideo_refresh
    // から黙って違うwidth/heightで描画してくるだけ)。これを「melonDSだから」
    // という専用コードでEmulationHost側に埋め込むのではなく、「このオプションが
    // 変わったらこのアクションを実行する」という宣言をJSON設定側に持たせ、
    // EmulationHost側は名前からアクションを引くだけの汎用ディスパッチャに留める。
    //
    // 実行本体はエミュレーションスレッドのループ内(RunLoop→
    // ApplyPendingCoreOptionActions)でのみ行う。UIスレッドからは
    // RequestCoreOptionAction() でアクション名をキューへ積むだけでよい。
    //
    // 新しい「既知の癖」に対応する際は、
    //   (1) ApplyPendingCoreOptionActions() のswitchへ新しいcase(アクション名)を
    //       1つ追加する
    //   (2) 該当オプションのcores/libretro.json側 "actions" 配列へその名前を書く
    // の2手順だけで済み、EmulationLauncher側に特定コア名を書く箇所は増えない。
    // ==================================================================
    private readonly ConcurrentQueue<string> _pendingCoreOptionActions = new();

    /// <summary>
    /// UIスレッドから、コアオプション変更に紐付くアクションを実行するよう要求する。
    /// GamePlayPage.CycleCoreOption/SettingsPage側が、変更したオプション定義の
    /// Actionsを見て、要素ごとにこれを呼ぶ想定。実際の実行はエミュレーション
    /// スレッドのループが次に回ったタイミング(ApplyPendingCoreOptionActions)。
    /// </summary>
    public void RequestCoreOptionAction(string actionName)
    {
        if (string.IsNullOrEmpty(actionName)) return;
        _pendingCoreOptionActions.Enqueue(actionName);
    }

    /// <summary>RequestCoreOptionAction() の実処理。エミュレーションスレッドのループ内から呼ぶ。
    /// 未知のアクション名は警告ログのみで無視する(将来JSON側に書いたが対応する
    /// caseをまだ実装していない、といった状態でもアプリが落ちないようにするため)。</summary>
    private void ApplyPendingCoreOptionActions()
    {
        while (_pendingCoreOptionActions.TryDequeue(out var actionName))
        {
            switch (actionName)
            {
                case "resyncGeometryOnFrameSizeChange":
                    _geometryResyncPending = true;
                    _geometryResyncFramesWaited = 0;
                    Debug.WriteLine(
                        "[EmulationHost] コアオプションアクション実行: resyncGeometryOnFrameSizeChange "
                        + "(以後、映像フレームの実サイズが変わるまで監視し、変わった時点でジオメトリ/アスペクト比を再計算します)");
                    break;

                default:
                    Debug.WriteLine($"[EmulationHost] コアオプションアクション: 未知のアクション名 \"{actionName}\" は無視します。");
                    break;
            }
        }
    }

    // ==================================================================
    // "resyncGeometryOnFrameSizeChange" アクションの実処理本体。
    //
    // 当初「アクション要求の次の1フレームだけを見て確定させる」実装にしていたが、
    // 実機検証の結果、メニューから復帰した直後の数フレームはまだ古い形状の
    // ままで、コアが実際にGET_VARIABLE_UPDATE経由で変更後の値を読み直して
    // 新しい形状になるまでには1フレーム以上のラグがあることが判明した
    // (「次の1フレーム」を鵜呑みにすると、古い形状のままCoreReportedAspectRatioを
    // 「更新」したことにしてしまい、以後は形状が変わっても差分が0のため何も
    // 起きなくなる、という不具合が実際に発生した)。
    //
    // そのため、アクション要求後は「実際に届いたフレームのwidth/heightが、
    // 基準値(起動時/直近のSET_GEOMETRY時点でのbase_width/height)から
    // 変化するまで」毎フレーム監視を続け、変化を検知した時点で初めて
    // ジオメトリを再計算して監視を終了する、という方式にしている。
    // コアが実際に値を再適用しない・レイアウトの選択肢を変えても形状が
    // 変わらない等の理由で永遠に変化が来ないケースに備え、一定フレーム数で
    // 諦める安全弁(_geometryResyncMaxFramesToWait)も設けてある。
    //
    // なお「フレームの生ピクセル比」をそのままアスペクト比に採用すると、
    // 意図的に非正方形ピクセル補正をしているコア(例: SNES等、base_widthと
    // aspect_ratioの比が違う機種)の補正が消えてしまう。そこで「起動時/
    // 直近のSET_GEOMETRY時点でのアスペクト比 と 生ピクセル比の比率」
    // (=補正係数)を覚えておき、生ピクセル比が変わった分だけ
    // CoreReportedAspectRatioにも同じ比率で反映する
    // (UpdateGeometryPixelCorrectionFactor/ResyncGeometryFromFrameSize参照)。
    // ==================================================================
    private bool _geometryResyncPending;
    private int _geometryResyncFramesWaited;
    private const int _geometryResyncMaxFramesToWait = 180; // 約3秒(60fps換算)で諦める
    private double _geometryPixelCorrectionFactor = 1.0;
    private int _geometryReferenceWidth;
    private int _geometryReferenceHeight;

    // 直近のHWフレームの解像度(メニュー表示時/ステートセーブ時に、
    // video_refresh以外の場所からも読み戻せるよう覚えておく)
    private int _lastHwFrameWidth;
    private int _lastHwFrameHeight;

    // デバッグ調査用: VideoRefreshCallbackが直前にログしたフレームサイズ/描画先サイズ
    // (スパム防止のため、変化した時だけログを出す)。
    private int _lastLoggedFrameW = int.MinValue;
    private int _lastLoggedFrameH = int.MinValue;
    private int _lastLoggedWinW = int.MinValue;
    private int _lastLoggedWinH = int.MinValue;

    // --- メニュー背景用サムネイル ---
    // メニュー表示時、SDLの描画ウィンドウ自体を隠す(Airspace対策)関係上、
    // 「今の画面をそのままぼかして見せる」ことができない。代わりに、毎フレームの
    // 描画データから小さく縮小した静止画をCPU側で作り続けておき、一時停止した
    // 瞬間に「最新のもの」をUI側(WinUI3のImage)へ渡して背景として表示する。
    // 縮小・簡易ブラーはこのサイズなら毎フレームやっても負荷はごくわずか。
    //
    // 実際のゲーム画面と同じ位置・同じ見た目(レターボックス)で表示できるよう、
    // 元映像のアスペクト比をそのまま保って縮小する(固定4:3にはしない)。
    // UI側でこのサムネイルを Stretch="Uniform" で表示すれば、SDL側の
    // SDL_LOGICAL_PRESENTATION_LETTERBOX と同じ配置になる。
    private const int ThumbnailMaxDim = 320; // 長辺をこのサイズに収める(ステートセーブ画像=詳細画面の表示にも使うため、やや大きめ)
    private readonly object _thumbLock = new();
    private byte[] _thumbBgra = Array.Empty<byte>();
    private byte[] _thumbWorkBuf = Array.Empty<byte>(); // 縮小処理の使い回し用(毎フレームのアロケーション回避)
    private byte[] _blurTempBuf = Array.Empty<byte>(); // BoxBlur3X3の作業用バッファの使い回し(同上)
    private byte[] _thumbRotateBuf = Array.Empty<byte>(); // RotateBgraBuffer(UpdateThumbnail用)の使い回し(同上。SET_ROTATIONのコアのみ影響)
    private byte[] _publishThumbRotateBuf = Array.Empty<byte>(); // RotateBgraBuffer(PublishThumbnail/HWサムネイル用)の使い回し。_thumbRotateBufとは
    // 別フィールドにしてあるのは、呼び出し元が違うと元データのサイズ/タイミングも異なり、
    // 同じ配列を使い回すと一方の結果をもう一方が上書きしてしまいかねないため
    // (詳細はRotateBgraBufferのXMLコメント参照)。
    private int _thumbWidth;
    private int _thumbHeight;
    private bool _thumbValid;

    // サムネイル生成(UpdateThumbnail。縮小+ブラーを含む)は、以前はSWレンダリングの
    // コアで毎フレーム無条件に呼んでいたが、これがエミュレーションスレッド
    // (retro_run()の実行と音声フレームタイミング制御を兼ねる)上での継続的な
    // GC圧・CPU負荷になっていた(詳細はBoxBlur3X3/RotateBgraBufferのコメント参照)。
    // ゲームプレイ中は完全にスキップし、実際に必要になる2つのタイミングだけ
    // ピンポイントで発生させる:
    //   1. 一時停止(メニュー表示)する直前の最後の1フレーム
    //      → メニュー背景(_thumbBgra)・ステートセーブのスクリーンショット
    //        (_rawThumbBgra、どのみち手動セーブは一時停止中にしか行えない)用。
    //   2. オートセーブが発生するフレーム(タイマーで事前に予測して立てる。
    //      巻き戻し用フルフレームキャプチャ(_captureFullFrameRequested)と同じ手法)
    //      → オートセーブのスクリーンショット用。ゲームプレイ中に発生するため、
    //        ここだけは唯一「一時停止と無関係にゲームプレイ中に発生する」ケース。
    // VideoRefreshCallback側でこのフラグを見て、trueの時だけUpdateThumbnailを呼ぶ
    // (呼んだら即falseに戻す。_captureFullFrameRequestedと同じ「都度消費」方式)。
    // このフィールドはエミュレーションスレッドの中だけで読み書きする
    // (UIスレッドから直接立てることはない。UIスレッドはPause()経由で
    //  _pauseRequestedを立てるだけで、実際の要求はRunLoop自身が行う)。
    //
    // 既知の軽微な制限: VideoRefreshCallbackはdata==nint.Zero(「直前フレームの複製」、
    // 一部のコアが変化の無いフレームを間引く際に送ってくる)の場合、何もせず即returnする
    // ため、狙った「一時停止直前の1フレーム」がたまたまこれに当たると、その回はメニュー
    // 背景/セーブ画面のスクリーンショットが1世代分古いまま(前回一時停止した時のもの)に
    // なる。頻度・実害(見た目が一瞬前のフレームというだけ)ともごく小さいため、
    // リトライ機構は入れていない。気になるようであれば、_pausePendingThumbnailCapture
    // 側でUpdateThumbnail実行後かどうか(=_thumbnailCaptureRequestedがfalseに戻ったか)を
    // 数フレームだけ確認してから本当に一時停止する、という形に拡張できる。
    // 同一スレッド内だけで読み書きする想定だが、既存の_captureFullFrameRequestedと
    // 同じ流儀でvolatileにしておく。
    private volatile bool _thumbnailCaptureRequested;

    // 上の「一時停止直前の1フレーム」を強制的に走らせるための状態。一時停止への
    // 切り替わりを検知したループの1回目でこれを立ててこのフレームの実行だけ許可し、
    // 実際に_pausedInternalへ切り替えるのは次のループにする(下のRunLoop参照)。
    private bool _pausePendingThumbnailCapture;

    // --- HWレンダリングのコア(DS/3DS)でステートセーブ画像を「上画面のみ」で撮るための仕組み ---
    // 一時停止の直前に、コアの画面レイアウトを一時的に「1画面のみ(上)」へ切り替えて数フレーム進め、
    // そのフレームを_stateThumbBgraへ保存してから元のレイアウトへ戻す(StepHwStateThumbnailCapture)。
    // その間の映像はウィンドウへ表示しない(_suppressPresent)。
    private int _hwThumbPhase;
    private int _hwThumbFrames;
    private List<(string Key, string Value)>? _hwThumbSaved;
    private volatile bool _suppressPresent;
    private byte[] _stateThumbBgra = Array.Empty<byte>();
    private int _stateThumbW, _stateThumbH;
    private bool _stateThumbValid;

    // ステートセーブのスクリーンショット用に、ブラーをかける前の同サイズの画像も
    // 保持しておく(セーブ操作自体がエミュレーションスレッド内でしか発生しないため、
    // ロック無しで読み書きして問題ない=同一スレッド内で完結する)。
    private byte[] _rawThumbBgra = Array.Empty<byte>();
    private int _rawThumbWidth;
    private int _rawThumbHeight;
    private bool _rawThumbValid;


    public event EventHandler? GeometryChanged;

    /// <summary>
    /// aspect_ratioが明示されている場合は、コアが既に(回転後の向きを踏まえて)正しい値を
    /// 申告してきているものとして、そのまま採用する。base_width/heightからのフォールバック
    /// 計算のみ、SET_ROTATIONで90/270度回転が申告されていれば縦横を入れ替える
    /// (base_width/heightは回転前=コアのネイティブな向きの生ピクセル比のことが多いため)。
    /// </summary>
    private double ComputeAspectRatio(in RetroGameGeometry geometry)
    {
        if (geometry.aspect_ratio > 0f)
            return geometry.aspect_ratio;

        if (geometry.base_width > 0 && geometry.base_height > 0)
        {
            double rawAspect = (double)geometry.base_width / geometry.base_height;
            bool rotatedQuarter = _coreRotationQuarterTurns == 1 || _coreRotationQuarterTurns == 3;
            return rotatedQuarter ? 1.0 / rawAspect : rawAspect;
        }

        return 4.0 / 3.0;
    }

    /// <summary>
    /// avInfo取得直後、およびSET_GEOMETRYを受け取った直後に呼ぶ。その時点の
    /// 「アスペクト比 と 生ピクセル比(base_width/base_height)」の関係を
    /// _geometryPixelCorrectionFactorとして覚え直す(SET_GEOMETRYを送って
    /// こないコアのフレームサイズ変化フォールバック検知で使う。上のコメント参照)。
    /// </summary>
    private void UpdateGeometryPixelCorrectionFactor(in RetroGameGeometry geometry, double aspect)
    {
        if (geometry.base_width > 0 && geometry.base_height > 0)
        {
            _geometryReferenceWidth = (int)geometry.base_width;
            _geometryReferenceHeight = (int)geometry.base_height;
            double rawPixelAspect = (double)geometry.base_width / geometry.base_height;
            _geometryPixelCorrectionFactor = rawPixelAspect > 0 ? aspect / rawPixelAspect : 1.0;
        }
        else
        {
            // base_width/heightが無い(0)場合は生ピクセル比較ができないため、
            // フォールバック検知自体を無効化しておく(誤爆で正しいアスペクト比を
            // 壊すよりは、何もしない方が安全)。
            _geometryReferenceWidth = 0;
            _geometryReferenceHeight = 0;
            _geometryPixelCorrectionFactor = 1.0;
        }
    }

    /// <summary>
    /// "resyncGeometryOnFrameSizeChange" アクションが、実際にフレーム形状の変化を
    /// 検知した時点で呼ぶ実処理。届いたフレームのwidth/heightから、
    /// _geometryPixelCorrectionFactorを使って推定アスペクト比を計算し、
    /// CoreReportedAspectRatioを更新してGeometryChangedを発火する。
    /// </summary>
    private void ResyncGeometryFromFrameSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        // 基準(起動時/直近のSET_GEOMETRY)が無い場合は補正係数1.0(生ピクセル比を
        // そのまま採用)として扱う。全く情報が無いよりは、それでも今回の
        // フレーム形状に合わせられる方が実用上は安全なため。
        double impliedAspect = _geometryPixelCorrectionFactor * ((double)width / height);

        Debug.WriteLine(
            $"[EmulationHost] resyncGeometryOnFrameSizeChange: フレーム形状の変化を検知 frame={width}x{height} "
            + $"(reference={_geometryReferenceWidth}x{_geometryReferenceHeight}, "
            + $"correctionFactor={_geometryPixelCorrectionFactor:F4}) "
            + $"=> impliedAspect={impliedAspect:F4} (旧CoreReportedAspectRatio={CoreReportedAspectRatio:F4})");

        CoreReportedAspectRatio = impliedAspect;
        _geometryReferenceWidth = width;
        _geometryReferenceHeight = height;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }


    // RETRO_DEVICE_ANALOGのindex/id定数、およびretro_input_state_t/retro_input_poll_tの
    // 実処理はGamepadInputMapper(_input)へ移動した。


    /// <summary>
    /// 中断メニュー「設定」内の「画質」(アスペクト比と同じくボタンクリックで切り替え)。
    /// UIスレッドから変更可能で、次に描画されるフレームから即座に反映される
    /// (RenderSwFrameViaSdlRendererが毎フレーム参照する)。SWレンダリングのコアにのみ意味を持つ
    /// (HWレンダリング中は生ピクセルバッファが取れないため、そもそもUI側で選択肢自体を出さない)。
    /// </summary>
    public PictureQualityMode PictureQuality
    {
        get => _pictureQuality;
        set => _pictureQuality = value;
    }
    private volatile PictureQualityMode _pictureQuality = PictureQualityMode.None;

    // ================= 画質: 独自実装のスケーラー(HighQuality3x / Hq2x) =================
    // Scale3x(AdvMAME3x系、ビット完全一致でコピーのみ)とHQ2x風(RGB知覚距離でブレンド)の
    // 2種類を切り替えて使う。どちらもエミュレーションスレッド内でのみ触るためロック不要。
    private byte[] _hqScaleBuffer = Array.Empty<byte>();
    private int _hqScaleSrcW;
    private int _hqScaleSrcH;
    private int _hqScaleBpp;
    private int _hqScaleFactor;


    // Win32.TimeBeginPeriod(1) が成功したかどうか。成功時のみ終了時にTimeEndPeriodで戻す。
    private bool _timerPeriodActive;
    private nint _mmcssHandle;

    // GET_SYSTEM_DIRECTORY / GET_SAVE_DIRECTORY 用。
    // ROMごとのフォルダではなく、実行ファイル基準+コアごとのディレクトリを使う
    //   system/<コアファイル名>/ … BIOS・追加ROM等(PCエンジンやMAME系はここに置く運用が前提)
    //   saves/<コアファイル名>/  … コア自身が書くセーブ(メモリーカード等)と .srm/.rtc
    // 同じ機種でもコアによってファイル形式が異なり得るため、ステートセーブと同様にコアごとに分けている。
    // (実際のキャッシュ・GET_*ハンドリングは _coreEnv = CoreEnvironmentSupport に委譲している)

    /// <summary>コアごとのフォルダ名(コアファイル名から拡張子を除いたもの。例: fceumm_libretro)。</summary>
    private string GetCoreFolderName() => SanitizeForPath(Path.GetFileNameWithoutExtension(_corePath));

    /// <summary>BIOS等を置くコア専用のSystem Directory(system/&lt;コアファイル名&gt;)。</summary>
    private string GetCoreSystemDirectory() => Path.Combine(AppContext.BaseDirectory, "system", GetCoreFolderName());

    /// <summary>セーブファイルを置くコア専用のSave Directory(saves/&lt;コアファイル名&gt;)。</summary>
    private string GetCoreSaveDirectory() => Path.Combine(AppContext.BaseDirectory, "saves", GetCoreFolderName());

    // コアに登録するdelegateを明示的にフィールド保持する(GC対策・可読性のため)
    private RetroEnvironmentCallback? _environmentCallback;
    private RetroVideoRefreshCallback? _videoRefreshCallback;
    private RetroAudioSampleBatchCallback? _audioSampleBatchCallback;
    private RetroInputPollCallback? _inputPollCallback;
    private RetroInputStateCallback? _inputStateCallback;

    /// <summary>状態メッセージ通知(呼び出しはエミュレーションスレッドから行われる)</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// コアの読み込みが完了し、SET_VARIABLES によるコアオプション申告も出揃った直後に発生する。
    /// UI側はこのタイミングで GetVideoFilterOptions() を呼び直せば、コアが対応している
    /// ビデオフィルター等の一覧を確実に取得できる。呼び出しはエミュレーションスレッドから行われる。
    /// </summary>
    public event EventHandler? CoreReady;

    // ================= メニュー操作用ゲームパッド入力(SDL3統一) =================
    // ゲーム実行中(ポーズしていない間)に ZL+ZR(SDL上はL2/R2)を同時押しすると中断メニューを開く。
    // さらに、そのまま長押しすると(短押しでの中断メニューの代わりに)巻き戻し画面を開く。
    // これは MainWindow/GamePlayPage 側のUIヒント("ZL+ZR 中断メニュー / 長押しで巻き戻し")と
    // 対応する。検出自体は GamepadInputMapper.DetectMenuToggleCombo (コアが毎フレーム呼ぶ
    // retro_input_poll 経由) の中で行い、下記2イベントへ転送する
    // (実際のしきい値・状態フィールドはGamepadInputMapper側に移動した)。

    /// <summary>
    /// ゲーム実行中に中断メニューを開く操作(ZL+ZR同時押し→長押し閾値に達する前に離す)が
    /// 検出されたときに発生する。呼び出しはエミュレーションスレッドから行われるため、
    /// UI側は必ずDispatcherQueue経由でUIスレッドへディスパッチしてから処理すること。
    /// </summary>
    public event EventHandler? MenuToggleRequested;

    /// <summary>ゲーム実行中にタッチパッドが押されたとき(システム設定パネルを開く操作)。エミュレーションスレッドから発生する。</summary>
    public event EventHandler? SystemPanelRequested;

    /// <summary>
    /// ゲーム実行中に巻き戻し画面を開く操作(ZL+ZR同時長押し)が検出されたときに発生する。
    /// MenuToggleRequestedと同様、呼び出しはエミュレーションスレッドから行われる。
    /// </summary>
    public event EventHandler? RewindToggleRequested;

    // 一時停止中(_pausedInternal == true)は、EmulationHostのスレッドが唯一の
    // SDLイベントポンプ所有者であり続けるため、中断メニュー(GamePlayPage側のUI)の
    // ナビゲーション入力もこのスレッドが代わりに読み取ってUI側へ転送する。
    // SdlGamepadService(メニュー専用サービス)とアルゴリズムを共有するため
    // GamepadEdgeTracker を再利用する。
    // 一時停止中の中断メニュー操作は、1P・2Pどちらのコントローラーでも行える(枠ごとに追跡する)。
    private readonly GamepadEdgeTracker[] _pausedMenuTrackers = { new(), new() };

    /// <summary>一時停止中の中断メニューでボタンが押された瞬間、および十字キー長押し中のオートリピート。</summary>
    public event Action<GamepadButton>? PausedMenuButtonPressed;

    /// <summary>一時停止中の中断メニューでボタンが長押し閾値を超えたときに一度だけ発生。</summary>
    public event Action<GamepadButton>? PausedMenuButtonLongPressed;

    /// <summary>一時停止中の中断メニューで十字キーを押し続けた際のオートリピート。</summary>
    public event Action<GamepadButton>? PausedMenuButtonRepeated;

    public EmulationHost(nint parentHwnd, string corePath, string romPath)
    {
        _parentHwnd = parentHwnd;
        _corePath = corePath;
        _romPath = romPath;

        _audio = new AudioProcessor(ReportStatus);

        _input = new GamepadInputMapper(ReportStatus);
        _input.MenuToggleRequested += (_, e) => MenuToggleRequested?.Invoke(this, e);
        _input.SystemPanelRequested += (_, e) => SystemPanelRequested?.Invoke(this, e);
        _input.RewindToggleRequested += (_, e) => RewindToggleRequested?.Invoke(this, e);
        _input.FastForwardComboPressed += (_, _) => CycleFastForwardMultiplier(reverse: false);
        _input.FastForwardReverseComboPressed += (_, _) => CycleFastForwardMultiplier(reverse: true);

        _coreEnv = new CoreEnvironmentSupport(GetCoreSystemDirectory, GetCoreSaveDirectory, ReportStatus);

        foreach (GamepadEdgeTracker tracker in _pausedMenuTrackers)
        {
            tracker.ButtonPressed += b => PausedMenuButtonPressed?.Invoke(b);
            tracker.ButtonLongPressed += b => PausedMenuButtonLongPressed?.Invoke(b);
            tracker.ButtonRepeated += b => PausedMenuButtonRepeated?.Invoke(b);
        }
    }

    /// <summary>
    /// RetroPadのボタン名("A"/"B"/"X"/"Y"/"L"/"R"/"Start"/"Select") → 物理ボタン名
    /// (GamepadButtonの名前の文字列)の対応表。GamePlayPageが起動時に
    /// ControllerMappingService.ResolveMapping(コントローラー設定画面の「デフォルト設定」に
    /// コア固有の「コア設定」を重ねてマージしたもの)を設定する。
    /// キーが欠けている場合は、コントローラー設定画面を導入する前からの既定挙動
    /// (Nintendo配列)にフォールバックする。実体はGamepadInputMapper(_input)側にある。
    /// </summary>
    public Dictionary<string, string> ButtonMapping
    {
        get => _input.ButtonMapping;
        set => _input.ButtonMapping = value;
    }

    /// <summary>
    /// ゲーム中の呼び出し操作(中断メニュー/巻き戻し/早送り)の組み合わせ。GamePlayPageが
    /// そのコンソールの設定(ControllerMappingService.ResolveHotkeys)を、Start()より前に設定する。
    /// </summary>
    public ControllerMappingService.HotkeyBindings Hotkeys
    {
        get => _input.Hotkeys;
        set => _input.Hotkeys = value;
    }

    /// <summary>連射の割り当て(RetroPadのキー → 物理ボタン名)。GamePlayPageが起動時に設定する。</summary>
    public Dictionary<string, string> TurboMapping
    {
        get => _input.TurboMapping;
        set => _input.TurboMapping = value ?? new Dictionary<string, string>();
    }

    public void Start()
    {
        if (_thread is not null) return;

        _running = true;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "EmulationThread",
            // GCのバックグラウンドスレッドや他プロセスにこのスレッドが横取りされる
            // 頻度を下げる(音声のジッタ対策。根本対策はMMCSS登録側だが、
            // 通常優先度スレッドとの取り合いに対する保険として合わせて上げておく)。
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>エミュレーションを停止し、スレッドの終了(≒後始末の完了)を待つ。</summary>
    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    /// <summary>
    /// メニュー表示などのためにエミュレーションを一時停止する。
    /// retro_run() の呼び出しを止めるだけでなく、音声出力の停止と
    /// (Airspace問題を避けるための)描画ウィンドウの非表示までまとめて行う。
    /// UIスレッドから呼ばれる想定だが、実際の反映はエミュレーションスレッド側で行われる。
    /// </summary>
    public void Pause()
    {
        _pauseRequested = true;

        // 反映までの1フレーム分(最大約16ms)の間、古い映像が一瞬見えてしまうのを
        // 避けるため、描画ウィンドウの非表示だけは呼び出し元スレッドから即座に行う。
        // ShowWindow はウィンドウを作成したスレッド以外から呼んでも問題ない。
        if (_childHwnd != nint.Zero)
            Win32.ShowWindow(_childHwnd, Win32.SW_HIDE);
    }

    /// <summary>一時停止を解除し、エミュレーションを再開する。</summary>
    public void Resume()
    {
        _input.RequestPointerRecenter(); // 中断メニューを開いている間にずれたジャイロのポインターを、中央から再開させる
        _pauseRequested = false;
    }

    /// <summary>
    /// 早送り倍率を次の段階へ巡回させる(reverse=falseなら 1倍 → 2倍 → 3倍 → 1倍 …、
    /// reverse=trueなら逆方向に 1倍 → 3倍 → 2倍 → 1倍 …)。
    /// GamepadInputMapper.FastForwardComboPressed(R+ZR同時押し、reverse=false)/
    /// FastForwardReverseComboPressed(L+ZL同時押し、reverse=true)のエッジ検出経由で
    /// 呼ばれるため、常にエミュレーションスレッド上での呼び出しになる
    /// (InputPollCallbackがcore.Run()経由でしか呼ばれないのと同じ理由で、
    ///  ポーズ中はそもそも呼ばれない)。
    ///
    /// 実際のフレームペース(何倍速で core.Run() を回すか)への反映はRunLoop側で
    /// FastForwardMultiplierを毎フレーム参照して行う。音声はAudioProcessor側で
    /// ピッチシフト再生(倍速再生)する(詳細はAudioProcessor.SetSpeedMultiplierの
    /// コメント参照。倍率の最大値が3である現状は、ミュートへ切り替わる閾値
    /// (AudioProcessor.MaxAudibleSpeedMultiplier)には実質到達しない)。
    /// </summary>
    private void CycleFastForwardMultiplier(bool reverse)
    {
        int length = FastForwardMultipliers.Length;
        _fastForwardIndex = reverse
            ? (_fastForwardIndex - 1 + length) % length
            : (_fastForwardIndex + 1) % length;
        int multiplier = FastForwardMultiplier;

        _audio.SetSpeedMultiplier(multiplier);

        string statusText = multiplier == 1
            ? "早送り解除(通常速度)"
            : multiplier > AudioProcessor.MaxAudibleSpeedMultiplier
                ? $"早送り: {multiplier}倍(音声ミュート)"
                : $"早送り: {multiplier}倍";
        ReportStatus(statusText);
        FastForwardMultiplierChanged?.Invoke(this, multiplier);
    }

    /// <summary>
    /// メニュー背景用に、直近フレームを縮小・簡易ブラー済みのBGRA8ピクセル列として取得する。
    /// フレームが1枚も描画されていない場合は false を返す。UIスレッドから呼ばれる想定。
    /// </summary>
    public bool TryGetPausedThumbnail(out byte[] bgraPixels, out int width, out int height)
    {
        lock (_thumbLock)
        {
            if (!_thumbValid)
            {
                bgraPixels = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }

            bgraPixels = (byte[])_thumbBgra.Clone();
            width = _thumbWidth;
            height = _thumbHeight;
            return true;
        }
    }

    /// <summary>
    /// 次にエミュレーションスレッドのループが回ったタイミングで retro_reset() を呼び出す。
    /// </summary>
    public void RequestReset() => _resetRequested = true;


    // ================= ビデオフィルター(コアオプション) =================
    // 実体はすべてCoreEnvironmentSupport(_coreEnv)に移動した。ここでは公開APIの
    // 互換性のためのフォワーディングのみを残す。

    /// <summary>
    /// 現在ロード中のコアが SET_VARIABLES で申告したコアオプションのうち、
    /// キーまたは説明文に "filter" を含むもの(=ビデオフィルター系)だけを抽出して返す。
    /// コアがビデオフィルターに相当するオプションを持っていなければ空配列。
    /// UIスレッドから呼び出し可能。
    /// </summary>
    public IReadOnlyList<CoreEnvironmentSupport.VideoFilterOption> GetVideoFilterOptions()
        => _coreEnv.GetVideoFilterOptions();

    /// <summary>
    /// 指定したコアオプション(ビデオフィルター等)の値を変更する。実際の反映は次に
    /// エミュレーションスレッドのループが回ったタイミングで行われる。対応していないコアは
    /// 値が変わったことに気づかない場合がある(libretroのコアオプションAPIの仕様上の制約)。
    /// </summary>
    public void RequestSetCoreVariable(string key, string value)
    {
        if (key == ControllerFormKey) return;

        if (key == WiiPointerKey)
        {
            ApplyWiiPointerMode(value, initial: false);
            return;
        }

        // ホスト側の擬似オプション(コアには渡さない): Wiiのコントローラの種類。
        if (key == WiiControllerKey)
        {
            _wiiFormValue = value;
            QueueWiiPortDevice();
            return;
        }

        // ホスト側の擬似オプション(コアには渡さない): WiiのMotionPlus(あり/なし)。
        if (key == WiiMotionPlusKey)
        {
            _wiiMotionPlus = !string.Equals(value, "off", StringComparison.Ordinal);
            QueueWiiPortDevice();
            return;
        }

        _coreEnv.RequestSetCoreVariable(key, value);
    }

    /// <summary>cores/libretro.jsonのwii用の擬似オプション(コアのオプションではなく、このホストが処理する)。</summary>
    public const string WiiControllerKey = "retronexus_wii_controller";

    /// <summary>
    /// Wii以外の機種で、コントローラの種類(形態)を選ぶ擬似オプションのキー(コアには渡さない)。
    /// 起動時のデバイスはControllerPortDevice、実行中の切り替えはRequestControllerPortDeviceで行う(再起動しない)。
    /// </summary>
    public const string ControllerFormKey = "retronexus_controller";

    /// <summary>
    /// 必ずStart()より前に設定する。選ばれた形態のデバイス値(controllerProfiles.jsonのdevice)。
    /// 未設定(null)なら通常のジョイパッド。Wiiは別の経路(WiiControllerKey)で決まる。
    /// </summary>
    public uint? ControllerPortDevice { get; set; }

    /// <summary>
    /// trueなら、キーボードをコアへ渡す(RETRO_DEVICE_KEYBOARD)。パッド代用のキー割り当ては止まり、F12が中断メニューになる。
    /// 必ずStart()より前に設定する。
    /// </summary>
    public bool KeyboardPassthrough { get => _input.KeyboardPassthrough; set => _input.KeyboardPassthrough = value; }

    /// <summary>
    /// キーボード直通のとき、キーボードを挿すポート番号(0始まり)。コアが「ポートにキーボードを挿す」方式(flycast等)のときだけ指定する。
    /// null=ポートのデバイスは変えない。
    /// </summary>
    public int? KeyboardPort { get; set; }

    private uint? _port1Device;

    /// <summary>
    /// ポート1(2P側)に挿す装置(RETRO_DEVICE_*。nullなら従来どおり、1Pと同じ種類のパッド)。
    /// MOUSEのときは、マウスの問い合わせにポート1で答える。コアの起動前(Start前)に設定する。
    /// </summary>
    public uint? Port1Device
    {
        get => _port1Device;
        set
        {
            _port1Device = value;
            _input.MousePort = value == RetroDevice.MOUSE ? 1u : 0u;
        }
    }

    /// <summary>true=Wiiのライトガン(ジャイロ照準+ボタンはJOYPAD)。false=ドリームキャスト/NAOMIのライトガン。実行中も変えられる。</summary>
    public bool LightgunWii { get => _input.LightgunWii; set => _input.LightgunWii = value; }

    /// <summary>Wii以外のライトガンの照準方法(0=右スティックで動かす 1=右スティックの位置へ直接 2=ジャイロ)。実行中も変えられる。</summary>
    public int LightgunAimMode { get => _input.LightgunAimMode; set => _input.LightgunAimMode = value; }

    /// <summary>
    /// 実行中にコントローラの種類(Wii以外)を切り替える。コアは再起動しない。
    /// エミュレーションスレッドの次のループでretro_set_controller_port_deviceを呼ぶ。
    /// 反映されるかはコア/ゲーム次第(null/0は通常のジョイパッド)。
    /// </summary>
    public void RequestControllerPortDevice(uint? device)
    {
        uint d = device is uint v && v != 0 ? v : RetroDevice.JOYPAD;
        ControllerPortDevice = device;
        _pendingPortDevice = (long)d;
    }

    /// <summary>cores/libretro.jsonのwii用の擬似オプション「MotionPlus」(on/off。既定はon)。</summary>
    public const string WiiMotionPlusKey = "retronexus_wii_motionplus";

    private long _pendingPortDevice = -1;
    private string? _wiiFormValue;
    private volatile bool _wiiMotionPlus = true;

    /// <summary>いまの形態とMotionPlusの設定から、コントローラのデバイスを決めて反映を予約する。</summary>
    private void QueueWiiPortDevice()
    {
        string? form = _wiiFormValue ?? _coreEnv.GetInitialOverride(WiiControllerKey);
        if (ResolveWiiControllerDevice(form, _wiiMotionPlus) is uint device) _pendingPortDevice = (long)device;
    }

    /// <summary>
    /// "classic"/"wiimote"/"nunchuk" → Dolphinコアのデバイス種別(libretro/dolphin Input.cppの
    /// RETRO_DEVICE_WIIMOTE_*。(n&lt;&lt;8)|JOYPAD)。未知の値/未指定はnull。
    /// </summary>
    private static uint? ResolveWiiControllerDevice(string? value, bool motionPlus)
        // controllerProfiles.json(コアのソースから起こした形態の一覧)を優先。読めないときだけ下の固定表。
        => ControllerProfiles.ResolveDevice("wii", value, motionPlus) ?? ResolveWiiControllerDeviceFallback(value);

    private static uint? ResolveWiiControllerDeviceFallback(string? value) => value switch
    {
        "wiimote" => RetroDevice.JOYPAD,                       // WiiMote(縦持ち)
        "nunchuk" => (3u << 8) | RetroDevice.JOYPAD,           // WiiMote + Nunchuk
        "classic" => (4u << 8) | RetroDevice.JOYPAD,           // WiiMote + Classic Controller
        _ => null,
    };

    /// <summary>実行中にコントローラの種類が変えられていたら、エミュレーションスレッドで反映する。</summary>
    private void ApplyPendingPortDevice()
    {
        long pending = Interlocked.Exchange(ref _pendingPortDevice, -1);
        if (pending < 0 || _core is null) return;
        _core.SetControllerPortDevice(0, (uint)pending);
        _core.SetControllerPortDevice(1, Port2Device((uint)pending));
        if (Port1Device is uint port1) _core.SetControllerPortDevice(1, port1);
    }

    // 自動作成(またはユーザー指定)のプロファイルを使っているか。使っていれば、ジャイロ/加速度の値は常にコアへ渡す
    // (ポインターはマウス/スティックのままで、振る/捻るだけがジャイロ。ジャイロ照準IMUIRはプロファイルで切ってある)。
    private volatile bool _wiiProfileActive;

    private const string AutoMarker = "# RetroNexus generated";
    private const string AutoProfileNames = "RetroNexus_Auto_";

    /// <summary>
    /// Dolphinコア用: 「ジャイロ」を選んだときだけ、そのゲームの1Pのリモコン割り当てを、アプリが作るプロファイルに置き換える
    /// (ヌンチャク形態でもジャイロ/加速度を使え、ジャイロ照準の再センタリングも効くようにするため)。
    /// コアの保存設定読み込み(dolphin_save_load_settings)は、プロファイルを使うゲームだけ有効にする
    /// (有効にすると、指定の無いゲームでコア既定の割り当てが使われなくなる経路があるため)。
    /// ユーザーが自分で GameSettings\ID.ini に WiimoteProfile を書いたゲームは、そちらを優先する。
    /// </summary>
    private void PrepareDolphinProfiles()
    {
        if (!Path.GetFileName(_corePath).Contains("dolphin_libretro", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            string user = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_corePath) ?? "", "..", "saves", "dolphin_libretro", "User"));
            string config = Path.Combine(user, "Config");
            string profiles = Path.Combine(config, "Profiles", "Wiimote");
            string? gameId = ReadDiscGameId(_romPath);
            if (gameId is null) return;
            string gameIni = Path.Combine(user, "GameSettings", gameId + ".ini");
            bool exists = File.Exists(gameIni);
            string text = exists ? File.ReadAllText(gameIni) : "";
            bool ours = exists && text.StartsWith(AutoMarker, StringComparison.Ordinal);
            bool imu = string.Equals(_wiiPointerMode, "imu", StringComparison.Ordinal);

            if (exists && !ours)
            {
                // ユーザー自身が指定したプロファイルを優先する。
                if (!text.Contains("WiimoteProfile", StringComparison.OrdinalIgnoreCase)) return;
                EnableProfiles(config, profiles, imu, "ユーザー指定");
                _wiiProfileActive = true;
                return;
            }

            // アプリが作るプロファイルは「ジャイロ」のときだけ。それ以外は、前回作った設定を片付けてコア既定へ戻す。
            string? form = _coreEnv.GetInitialOverride(WiiControllerKey);
            string? name = imu ? AutoProfileFor(form) : null;
            if (name is null)
            {
                if (ours) File.Delete(gameIni);
                return;
            }

            Directory.CreateDirectory(profiles);
            Directory.CreateDirectory(Path.GetDirectoryName(gameIni)!);
            File.WriteAllText(Path.Combine(profiles, name + ".ini"), BuildAutoProfile(string.Equals(form, "nunchuk", StringComparison.Ordinal), _wiiMotionPlus, imu));
            File.WriteAllText(gameIni, AutoMarker + "\n[Controls]\nWiimoteProfile1 = " + name + "\n");
            EnableProfiles(config, profiles, imu, "自動作成: " + name);
            _wiiProfileActive = true;
        }
        catch (Exception ex)
        {
            EmulatorLauncher.Services.AppLog.Warn("Emu", "Wiiリモコンのプロファイル準備に失敗: " + ex.Message);
        }
    }

    private static string? AutoProfileFor(string? form) => form switch
    {
        null or "" or "wiimote" => AutoProfileNames + "Wiimote",
        "nunchuk" => AutoProfileNames + "Nunchuk",
        _ => null,
    };

    private void EnableProfiles(string config, string profiles, bool imu, string source)
    {
        _coreEnv.SetInitialOverride("dolphin_save_load_settings", "enabled");
        string imuir = imu ? "True" : "False";
        foreach (string file in Directory.GetFiles(profiles, "*.ini", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("IMUIR/Enabled", StringComparison.Ordinal) && !lines[i].EndsWith(imuir, StringComparison.Ordinal))
                {
                    lines[i] = "IMUIR/Enabled = " + imuir;
                    changed = true;
                }
            }
            if (changed) File.WriteAllLines(file, lines);
        }
        string stale = Path.Combine(config, "WiimoteNew.ini");
        if (File.Exists(stale)) File.Delete(stale);
        EmulatorLauncher.Services.AppLog.Info("Emu", $"Wiiリモコンのプロファイルを有効にしました ({source}) / ポインタ操作={_wiiPointerMode} / ジャイロ照準(IMUIR)={imuir}");
    }

    /// <summary>「ジャイロ」用のリモコン割り当て(Dolphinのプロファイルini)。ヌンチャク無し/有りの2種。</summary>
    private static string BuildAutoProfile(bool nunchuk, bool motionPlus, bool imuPointer)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[Profile]\n");
        sb.Append("Buttons/A = A | `Libretro/0/Mouse:Left`\n");
        sb.Append("Buttons/B = B | `Libretro/0/Mouse:Right`\n");
        sb.Append(nunchuk ? "Buttons/1 = Start\nButtons/2 = Select\nButtons/- = L\nButtons/+ = R\n"
                          : "Buttons/1 = X\nButtons/2 = Y\nButtons/- = Select\nButtons/+ = Start\n");
        sb.Append("Buttons/Home = R3\n");
        sb.Append("D-Pad/Up = Up\nD-Pad/Down = Down\nD-Pad/Left = Left\nD-Pad/Right = Right\n");
        sb.Append("Shake/X = R2 | `Libretro/0/Mouse:Middle`\nShake/Y = R2 | `Libretro/0/Mouse:Middle`\nShake/Z = R2 | `Libretro/0/Mouse:Middle`\n");
        sb.Append("IMUAccelerometer/Up = `Libretro/0/Sensor:AccelZ+`\nIMUAccelerometer/Down = `Libretro/0/Sensor:AccelZ-`\n");
        sb.Append("IMUAccelerometer/Left = `Libretro/0/Sensor:AccelX+`\nIMUAccelerometer/Right = `Libretro/0/Sensor:AccelX-`\n");
        sb.Append("IMUAccelerometer/Forward = `Libretro/0/Sensor:AccelY-`\nIMUAccelerometer/Backward = `Libretro/0/Sensor:AccelY+`\n");
        sb.Append("IMUGyroscope/Pitch Up = `Libretro/0/Sensor:GyroX-`\nIMUGyroscope/Pitch Down = `Libretro/0/Sensor:GyroX+`\n");
        sb.Append("IMUGyroscope/Roll Left = `Libretro/0/Sensor:GyroY+`\nIMUGyroscope/Roll Right = `Libretro/0/Sensor:GyroY-`\n");
        sb.Append("IMUGyroscope/Yaw Left = `Libretro/0/Sensor:GyroZ+`\nIMUGyroscope/Yaw Right = `Libretro/0/Sensor:GyroZ-`\n");
        sb.Append(nunchuk ? "Extension = Nunchuk\n" : "Extension = None\n");
        sb.Append("Extension/Attach MotionPlus = ").Append(motionPlus ? "True" : "False").Append('\n');
        if (nunchuk)
        {
            sb.Append("Nunchuk/Buttons/C = X\nNunchuk/Buttons/Z = Y\n");
            sb.Append("Nunchuk/Stick/Up = `Libretro/0/Analog:Y0-`\nNunchuk/Stick/Down = `Libretro/0/Analog:Y0+`\n");
            sb.Append("Nunchuk/Stick/Left = `Libretro/0/Analog:X0-`\nNunchuk/Stick/Right = `Libretro/0/Analog:X0+`\n");
            sb.Append("Nunchuk/Shake/X = L2 | `Libretro/0/Mouse:Middle`\nNunchuk/Shake/Y = L2 | `Libretro/0/Mouse:Middle`\nNunchuk/Shake/Z = L2 | `Libretro/0/Mouse:Middle`\n");
        }
        sb.Append("Rumble/Motor = Rumble\n");
        sb.Append(imuPointer ? "IMUIR/Enabled = True\n" : "IMUIR/Enabled = False\n").Append("IMUIR/Recenter = `Libretro/0/Mouse:Button4`\n");
        return sb.ToString();
    }

    /// <summary>Wii/GCのディスクイメージ(iso/gcm/wbfs/rvz/wia)から6文字のゲームIDを読む。読めなければnull。</summary>
    private static string? ReadDiscGameId(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            long offset = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".iso" or ".gcm" => 0,
                ".wbfs" => 0x200,
                ".rvz" or ".wia" => 0x58,
                _ => -1,
            };
            if (offset < 0) return null;
            using FileStream fs = File.OpenRead(path);
            fs.Seek(offset, SeekOrigin.Begin);
            byte[] buf = new byte[6];
            if (fs.Read(buf, 0, 6) != 6) return null;
            foreach (byte b in buf)
            {
                if (!((b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z'))) return null;
            }
            return System.Text.Encoding.ASCII.GetString(buf);
        }
        catch { return null; }
    }

    /// <summary>ライトガン形態のときは2Pを通常パッドのままにする(2つ目の照準が画面中央に出るのを防ぐ)。</summary>
    private static uint Port2Device(uint device) => (device & 0xFF) == 4 ? RetroDevice.JOYPAD : device;

    /// <summary>
    /// 必ずStart()より前に呼ぶこと。コアがSET_VARIABLESでこのキーを申告してきたとき、
    /// コア自身の既定値の代わりに使わせたい値を予約する(<see
    /// cref="CoreEnvironmentSupport.SetInitialOverride"/>参照)。内部解像度のように、
    /// コアがretro_load_game内で最初に読んだ値がフレームバッファのサイズ計算に影響する
    /// ような設定は、Start()後にRequestSetCoreVariableで変更しても手遅れ(その時点では
    /// 既にretro_load_game/retro_get_system_av_infoが完了しHWレンダリング用FBOも
    /// 確保済み)なため、こちらを使う。
    /// </summary>
    public void SetInitialCoreVariable(string key, string value)
    {
        // ホスト側の擬似オプション(コアには渡さない): コントローラの種類。デバイスはControllerPortDeviceで決まる。
        if (key == ControllerFormKey) return;

        if (key == WiiPointerKey)
        {
            ApplyWiiPointerMode(value, initial: true);
            return;
        }

        // ホスト側の擬似オプション: MotionPlus(コアには渡さない)。
        if (key == WiiMotionPlusKey)
        {
            _wiiMotionPlus = !string.Equals(value, "off", StringComparison.Ordinal);
            return;
        }

        _coreEnv.SetInitialOverride(key, value);
    }

    /// <summary>cores/libretro.jsonのwii用の擬似オプション「IRポインタの操作方法」(stick/mouse/gyro)。コアのdolphin_ir_mode(1=右スティック, 2=POINTER=マウス)へ変換する。</summary>
    public const string WiiPointerKey = "retronexus_wii_pointer";

    private volatile bool _wiiGyroPointer;
    private volatile string _wiiPointerMode = "stick";

    // Wiiのマウス/ジャイロ操作では画面上のポインターを使うので、ゲーム画面の上のマウスカーソルは隠す。
    // ゲーム中のマウスカーソルは常に隠す(ポインター操作のWii/ライトガンも、画面上の照準だけを見せる)。
    private volatile bool _hideCursorWanted = true;

    /// <summary>true の間はマウスカーソルを隠さない(DS/3DSのように、マウスでタッチ操作するコア用)。</summary>
    public volatile bool KeepCursorVisible;

    /// <summary>右スティックでカーソル(タッチ位置)を動かす(DS用)。</summary>
    public bool StickCursorEnabled { get => _input.StickCursorEnabled; set => _input.StickCursorEnabled = value; }
    private bool _cursorHidden, _cursorWasInside;
    private int _cursorRehideCounter;

    private void ApplyWiiPointerMode(string value, bool initial)
    {
        // ジャイロ操作の仕組み(GamepadInputMapper.GyroPointerEnabled)は残すが、Wiiでは安定しないので選択肢から外した。
        // 過去に保存された"gyro"は、マウスとして扱う。
        if (value == "gyro") value = "mouse";
        // "imu"=ジャイロ(Dolphin内蔵のジャイロ照準IMUIRを有効にする。プロファイルiniは起動時に書き換える)。
        // マウス位置がジャイロの照準に上乗せされて引っ張られないよう、POINTERは常に画面中央を返し、右スティックも無効にする。
        _wiiPointerMode = value;
        // 右スティックは「右スティック」操作のときだけコアへ渡す(それ以外はコア側で傾き入力に割り当てられ、ポインタが回ってしまう)。
        _input.IgnoreRightStickAnalog = value != "stick";
        _input.PointerCentered = value == "imu";
        _input.HoldPointerOutside = value == "mouse";
        // ジャイロの値は「ジャイロ」のときだけコアへ渡す(それ以外はジャイロ照準が動いて、マウス/スティック操作と二重になるため)。
        _motionRumble.MotionToCore = value == "imu";
        _wiiGyroPointer = value == "gyro";
        string coreValue = value == "stick" ? "1" : "2";
        if (initial) _coreEnv.SetInitialOverride("dolphin_ir_mode", coreValue);
        else _coreEnv.RequestSetCoreVariable("dolphin_ir_mode", coreValue);
        if (_wiiGyroPointer) _input.RequestPointerRecenter();
    }



    private static string SanitizeForPath(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }




    /// <summary>UIスレッド側から、描画ウィンドウを配置すべき矩形(スクリーン座標・物理ピクセル)を通知する。</summary>
    public void UpdateBounds(int x, int y, int w, int h)
    {
        lock (_boundsLock)
        {
            // 値が実際に変わっていない場合は何もしない。GamePlayPage側は
            // 定期ポーリング(自己修正用の保険)でも毎回このメソッドを呼ぶため、
            // 変化が無いのに毎回 _boundsDirty を立ててしまうと、デバッグ枠の
            // 色が常に変わり続けたり、無駄なSetWindowPos等が頻発したりする。
            if (!_boundsDirty && _pendingBounds.X == x && _pendingBounds.Y == y
                && _pendingBounds.W == w && _pendingBounds.H == h
                && _currentBounds.X == x && _currentBounds.Y == y
                && _currentBounds.W == w && _currentBounds.H == h)
            {
                return;
            }

            _pendingBounds = (x, y, w, h);
            _boundsDirty = true;
        }
    }

    /// <summary>
    /// 状態メッセージをUIへ通知する。StatusChangedはUI側で「最新の1件だけ」
    /// 表示している(履歴を持たない)ため、途中の状態が上書きされて消えてしまう。
    /// デバッグ用にDebugコンソール(Visual Studioの「出力」ウィンドウ等)には
    /// 全件タイムスタンプ付きで出しておくことで、経過を後から追えるようにする。
    /// </summary>
    private void ReportStatus(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [EmulationHost] {message}");

        // 動作ログ(data\log)にも残す(不具合の報告を受けたとき、経過を後から追えるようにする)。
        EmulatorLauncher.Services.AppLog.Info("Emu", message);
        StatusChanged?.Invoke(this, message);
    }

    private void RunLoop()
    {
        try
        {
            ReportStatus("SDL初期化中...");

            // ジョイスティック/ゲームパッドのバックエンド選択に関わるヒント
            // (SDL_HINT_JOYSTICK_WGI 等)は、ゲームパッドサブシステムの初期化より前に
            // 設定されていなければ効果がない。このサブシステムはアプリ起動時
            // (App.xaml.cs の SdlGamepadInit.InitializeOnce)に一度だけ初期化されるため、
            // それらのヒントも同じタイミングでまとめて設定済みである。ここでは
            // ゲーム実行のたびに再初期化する音声サブシステムに関するヒントだけを扱う。

            // オーディオのハードウェアバッファを大きめに確保しておく(既定値はレイテンシ優先で
            // 小さいため、フレームタイミングの僅かなジッタでアンダーラン=プチプチ音が出やすい)。
            // 2048フレーム ≈ 46ms@44.1kHz 程度の余裕を持たせる。
            Sdl.SDL_SetHint(Sdl.SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES, AudioProcessor.DeviceSampleFrames.ToString());

            // Windowsの既定のスレッドタイマー精度(≈15.6ms)のままだと、後段の
            // フレームタイミングループの Thread.Sleep(1) が実際には10ms以上ブレて
            // しまい、これがオーディオのアンダーラン(プチプチ音)の主因になりやすい。
            // 1ms粒度に上げておく(プロセス終了時/finally で必ず元に戻す)。
            _timerPeriodActive = Win32.TimeBeginPeriod(1) == 0;

            // MMCSSの"Pro Audio"タスクとしてこのスレッドを登録する。timeBeginPeriod(1)は
            // あくまでSleep等の「時間粒度」を細かくするだけで、スレッド自体が他の
            // 通常優先度スレッド(.NETのGC、WinUI/XAMLのコンポジタ、他プロセス等)に
            // 横取りされること自体は防げない。MMCSS登録は、このスレッドを一時的に
            // 高優先度クラスへ引き上げつつ、暴走時は自動的に抑制もしてくれる
            // Windows標準の仕組みで、プロオーディオ用途で広く使われている。
            // 失敗しても致命的ではない(単に今まで通りの保護レベルに留まるだけ)ため、
            // 戻り値0(失敗)は無視して続行する。
            uint mmcssTaskIndex = 0;
            _mmcssHandle = Win32.AvSetMmThreadCharacteristics("Pro Audio", ref mmcssTaskIndex);

            // SDL_INIT_GAMEPAD はアプリ起動時(App.xaml.cs)に一度だけ初期化され、
            // メニュー画面のナビゲーション入力(SdlGamepadService)とプロセス終了まで
            // 共有され続ける。ここでは映像/音声サブシステムだけを追加初期化する
            // (SDL_InitSubSystemは参照カウント方式のため、ゲームパッドの初期化状態には
            //  影響しない)。
            if (!Sdl.SDL_InitSubSystem(Sdl.SDL_INIT_VIDEO | Sdl.SDL_INIT_AUDIO))
                throw new InvalidOperationException("SDL_InitSubSystem に失敗しました: " + Sdl.SDL_GetErrorString());

            // SDL3では既存HWNDのラップはプロパティ経由で行う…はずだったが、実機検証の
            // 結果、外部HWNDをラップする方式(SDL_PROP_WINDOW_CREATE_WIN32_HWND_POINTER)
            // には次の2つの不具合があることが確認された(いずれもSDLを介さない生の
            // Win32.SetWindowPos/GetClientRectレベルでも再現したため、原因はSDL側が
            // ラップ対象の外部HWNDを内部的に特別扱いしていることにあると考えられる):
            //   1. 作成直後のクライアント領域サイズが要求値と食い違う(境界線分と
            //      思われる一定のオフセットが常に加算される)
            //   2. 作成後に(SDLを介さず直接)SetWindowPosでリサイズしても、
            //      最初の1回だけ何らかのサイズが確定した後は、以後まったく
            //      反映されなくなる
            // そのため、外部HWNDをラップするのではなく、SDLに直接ウィンドウを
            // 作らせ、作成後にそのWin32 HWNDを取得して「所有者(owner)」関係だけ
            // こちらで設定する方式に切り替える。これならリサイズは
            // SDL_SetWindowPosition/SDL_SetWindowSize というSDL自身のAPIで
            // 行えるため、外部ラップ特有の不具合をそもそも避けられる。
            //
            // HWレンダリング(N64コア等がGLで直接描画)にも対応できるよう、
            // ウィンドウ作成時点で SDL_WINDOW_OPENGL フラグを立てておく
            // (SW/HWどちらのコアでも同じ1本のGLコンテキストで描画する)。
            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_DOUBLEBUFFER, 1);
            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_DEPTH_SIZE, 24);
            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_STENCIL_SIZE, 8);

            uint winProps = Sdl.SDL_CreateProperties();
            Sdl.SDL_SetNumberProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_X_NUMBER, _pendingBounds.X);
            Sdl.SDL_SetNumberProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_Y_NUMBER, _pendingBounds.Y);
            Sdl.SDL_SetNumberProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, _pendingBounds.W);
            Sdl.SDL_SetNumberProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, _pendingBounds.H);
            Sdl.SDL_SetBooleanProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_BORDERLESS_BOOLEAN, true);
            Sdl.SDL_SetBooleanProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_RESIZABLE_BOOLEAN, true);
            Sdl.SDL_SetBooleanProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_HIDDEN_BOOLEAN, true);
            Sdl.SDL_SetBooleanProperty(winProps, Sdl.SDL_PROP_WINDOW_CREATE_OPENGL_BOOLEAN, true);
            _sdlWindow = Sdl.SDL_CreateWindowWithProperties(winProps);
            Sdl.SDL_DestroyProperties(winProps);

            if (_sdlWindow == nint.Zero)
                throw new InvalidOperationException("SDL_CreateWindowWithProperties に失敗しました: " + Sdl.SDL_GetErrorString());

            // SDLが作成した実際のネイティブウィンドウ(HWND)を取得する。
            // _childHwndは「SDLが所有するウィンドウの実体」を指す参照として、
            // デバッグ枠のZオーダー制御やPause時のShowWindow等、既存のWin32レベルの
            // 処理でそのまま使い続ける(サイズ変更操作だけはSDL自身のAPIへ移行する)。
            uint sdlWinProps = Sdl.SDL_GetWindowProperties(_sdlWindow);
            _childHwnd = Sdl.SDL_GetPointerProperty(sdlWinProps, Sdl.SDL_PROP_WINDOW_WIN32_HWND_POINTER, nint.Zero);
            if (_childHwnd == nint.Zero)
                throw new InvalidOperationException("SDLウィンドウからWin32 HWNDの取得に失敗しました。");
            _input.GameWindowHandle = _childHwnd; // マウス入力(下画面タッチ)の座標計算用

            // MainWindow(_parentHwnd)の所有ウィンドウ(owned window)として扱われるよう、
            // GWLP_HWNDPARENTを設定する(タスクバーへの単独表示防止、Z順の連動等のため)。
            Win32.SetWindowLongPtr(_childHwnd, Win32.GWLP_HWNDPARENT, _parentHwnd);

            // 破棄時などに縮小して流れていくDWMのアニメーションが見えないよう、無効にしておく。
            Win32.DisableWindowTransitions(_childHwnd);

            Win32.ShowWindow(_childHwnd, Win32.SW_SHOWNA);

            _glContext = Sdl.SDL_GL_CreateContext(_sdlWindow);
            if (_glContext == nint.Zero)
                throw new InvalidOperationException("SDL_GL_CreateContext に失敗しました: " + Sdl.SDL_GetErrorString());

            if (!Sdl.SDL_GL_MakeCurrent(_sdlWindow, _glContext))
                throw new InvalidOperationException("SDL_GL_MakeCurrent に失敗しました: " + Sdl.SDL_GetErrorString());

            Sdl.SDL_GL_SetSwapInterval(0); // vsync

            // FBO関連の関数はopengl32.dllに無いため、コンテキストがカレントになった
            // 直後に SDL_GL_GetProcAddress 経由で解決しておく(SW/HW両方の描画で使う)。
            Gl.Load(Sdl.SDL_GL_GetProcAddress);

            // 最初に見つかったゲームパッド(PS5/DualSenseを含む)を使用する。
            // SDL_EVENT_GAMEPAD_ADDED は「そのゲームパッドサブシステムの中で最初に
            // イベントをポンプした者」だけが受け取れる一度きりの通知であり、後から
            // 別の場所でポンプを始めても再送されない。このアプリではゲームパッド
            // サブシステム(SDL_INIT_GAMEPAD)をアプリ起動時に一度だけ初期化して
            // SdlGamepadService(メニュー操作)とEmulationHost(ゲーム操作)で使い回して
            // いるため、EmulationHost起動時点ではすでに接続済みだったコントローラの
            // ADDEDイベントは SdlGamepadService 側で消費済みになっている。
            // そのため、まずイベントを一度ポンプ&処理して「起動後に新しく挿された」
            // コントローラを拾いつつ、それでも見つからなければ SDL_GetGamepads() で
            // 能動的に列挙して「すでに接続済み」だったコントローラも検出する。
            Sdl.SDL_PumpEvents();
            while (Sdl.SDL_PollEvent(out var initialEv))
            {
                if (initialEv.type == Sdl.SDL_EVENT_QUIT) { _running = false; continue; }
                _input.HandleDeviceEvent(initialEv);
            }

            if (_input.Gamepad == nint.Zero)
            {
                _input.OpenFirstAvailable();
            }

            // 動作ログ: コアのロード中は、コア自身が出す INFO/DEBUG も残す(ゲームが起動しない原因を追うため)。
            EmulatorLauncher.Services.AppLog.BeginCoreSession();
            EmulatorLauncher.Services.AppLog.Info("Emu", $"コア: {_corePath}");
            EmulatorLauncher.Services.AppLog.Info("Emu", string.IsNullOrEmpty(_romPath) ? "ゲーム: (本体のみ起動)" : $"ゲーム: {_romPath}");
            ReportStatus("コアをロード中...");
            _core = new LibretroCore(_corePath);
            // MAME 2003-Plus はスティックのアナログ値を方向に変換する処理が左・上で効かないため、十字キーとして返す。
            _input.StickAlwaysAsDpad = Path.GetFileName(_corePath).Contains("mame2003", StringComparison.OrdinalIgnoreCase);
            PrepareDolphinProfiles();
            _motionRumble.MotionToCore = _wiiPointerMode == "imu";
            _motionRumble.WiiRemoteAxes = Path.GetFileName(_corePath).Contains("dolphin_libretro", StringComparison.OrdinalIgnoreCase);

            // delegateをフィールドへ生成・保持してからコアへ登録する
            // (音声/入力は実処理がAudioProcessor/GamepadInputMapperへ移動したため、
            //  そのインスタンスメソッドを直接指す)
            _environmentCallback = EnvironmentCallback;
            _videoRefreshCallback = VideoRefreshCallback;
            _audioSampleBatchCallback = _audio.SampleBatch;
            _inputPollCallback = InputPollCallback;
            _inputStateCallback = _input.InputState;

            // 呼び出し順序はlibretroの仕様どおり retro_set_environment → retro_init → その他のset_* → retro_load_game。
            // 仕様上、環境コールバック以外(映像/音声/入力)は最初のretro_runまでに設定されていればよく、
            // retro_init より前に呼ぶことは保証されていない。mesenのように、retro_init で作る内部オブジェクトへ
            // コールバックを渡すコアは、init前にset_video_refreshを呼ぶと落ちる(ExecutionEngineException)。
            // 切り分け用に各呼び出しをログへ残す。
            Debug.WriteLine($"[Core] ロード: {Path.GetFileName(_corePath)} / API version {_core.ApiVersion}");
            Debug.WriteLine("[Core] retro_set_environment");
            _core.SetEnvironment(_environmentCallback);

            Debug.WriteLine("[Core] retro_init");
            _core.Init();

            Debug.WriteLine("[Core] retro_set_video_refresh / audio / input");
            _core.SetVideoRefresh(_videoRefreshCallback);
            _core.SetAudioSampleBatch(_audioSampleBatchCallback);
            _core.SetInputPoll(_inputPollCallback);
            _core.SetInputState(_inputStateCallback);

            Debug.WriteLine("[Core] retro_load_game");
            bool noGameBoot = string.IsNullOrEmpty(_romPath);
            if (noGameBoot)
                Debug.WriteLine($"[Core] ゲーム無し起動(コアのno-game対応申告: {_coreSupportsNoGame})");
            if (!_core.LoadGame(_romPath))
            {
                if (noGameBoot)
                    throw new InvalidOperationException(_coreSupportsNoGame
                        ? "本体(BIOS)の起動に失敗しました。BIOSファイルが system フォルダに無い可能性があります。"
                        : "このコアはゲーム無しの本体起動に対応していません。");
                throw new InvalidOperationException("retro_load_game に失敗しました。ROMファイルまたはコアが対応していない可能性があります。");
            }

            // RetroArchも retro_load_game 後に必ず呼んでいる。
            // これを呼ばないと、コア内部の入力関連の状態が未初期化のままになる可能性がある。
            // Wii(Dolphin)だけは、設定「コントローラの種類」で選んだデバイス
            // (クラシック/Wiiリモコン/リモコン+ヌンチャク)を使う。それ以外は通常のジョイパッド。
            uint portDevice = ResolveWiiControllerDevice(_coreEnv.GetInitialOverride(WiiControllerKey), _wiiMotionPlus)
                              ?? ControllerPortDevice
                              ?? RetroDevice.JOYPAD;
            _core.SetControllerPortDevice(0, portDevice);
            // 2P(ポート1)。1ポートしか持たないコアでは無視される。
            _core.SetControllerPortDevice(1, Port2Device(portDevice));
            // 設定で選んだポート1の装置(FM TOWNSのマウス等)があれば、それを優先する。
            if (Port1Device is uint port1Device) _core.SetControllerPortDevice(1, port1Device);
            if (KeyboardPassthrough && KeyboardPort is int keyboardPort && keyboardPort >= 0)
                _core.SetControllerPortDevice((uint)keyboardPort, RetroDevice.KEYBOARD);

            // バッテリーバックアップ(ゲーム内セーブ)を最初の retro_run より前に読み込む
            LoadPersistentMemory();

            // 複数枚組を「2枚目から」など途中のディスクから始めるときは、最初のretro_runより前に入れ替えておく。
            ApplyInitialDisk();

            // この時点で SET_VARIABLES によるコアオプションの申告は出揃っている。
            // UI側(設定画面のビデオフィルター一覧)が最新の状態を取得できるよう通知する。
            var avInfo = _core.GetSystemAvInfo();
            CoreReportedAspectRatio = ComputeAspectRatio(avInfo.geometry);
            UpdateGeometryPixelCorrectionFactor(avInfo.geometry, CoreReportedAspectRatio);
            EmulatorLauncher.Services.AppLog.EndCoreLoading(); // 以降、コアのログは WARN/ERROR だけ残す
            CoreReady?.Invoke(this, EventArgs.Empty);

            if (_isHwRendered)
            {
                int fboW = avInfo.geometry.max_width > 0 ? (int)avInfo.geometry.max_width : (int)avInfo.geometry.base_width;
                int fboH = avInfo.geometry.max_height > 0 ? (int)avInfo.geometry.max_height : (int)avInfo.geometry.base_height;

                // コアが申告するmax_width/max_heightは、あくまで「起動時点の画面レイアウト
                // 設定での」最大値であることがあり、実行中に画面レイアウトを変える(例:
                // 横長⇔縦長)コア/オプションでは、そちらの向きの実際の最大値を反映して
                // いないことがある(melonDS-DS実機検証で確認)。正方形(縦横の大きい方に
                // 揃える)で確保しておけば、向きが入れ替わるだけの変化には大抵そのまま
                // 収まる。それでも足りない場合はEnsureHwFboCapacity(VideoRefreshCallback)が
                // 実行中に追加で拡張する。特定コア名のチェックは行っていない汎用の余裕確保。
                int fboSquare = Math.Max(fboW, fboH);
                fboW = fboSquare;
                fboH = fboSquare;

                CreateHwFbo(fboW, fboH);

                if (_hwContextResetPtr != nint.Zero)
                {
                    var contextReset = Marshal.GetDelegateForFunctionPointer<RetroHwContextResetCallback>(_hwContextResetPtr);
                    contextReset();
                }

                ReportStatus($"HWレンダリング用FBOを作成しました ({fboW}x{fboH}, depth={_hwDepth}, stencil={_hwStencil})");
            }
            else
            {
                SwitchToSdlRendererForSwRendering();
            }

            _audio.Open(avInfo.timing.sample_rate);

            double fps = avInfo.timing.fps > 0 ? avInfo.timing.fps : 60.0;
            string coreName = _core.GetSystemInfo().LibraryName;
            string padInfo = _input.Describe();
            ReportStatus($"実行中: {coreName} ({fps:F1} fps) / {padInfo}");

            double frameTimeMs = 1000.0 / fps;
            var sw = Stopwatch.StartNew();
            double nextFrame = 0;
            double lastZOrderReassertMs = -1000;
            // 音声で刻むペース補正: コアによっては、1回のretro_runでゲーム内の1フレームより長い時間(画面2枚分など)を
            // 進める(例: Flycastは「画面が1枚描き終わるまで」を1回で進めるので、30fps描画のゲームでは1回=約33ms)。
            // その音声は、コアが出す音声の長さ(フレーム数÷サンプルレート)から分かる。RetroArchは音声同期で自然に
            // 合うが、ここは申告fps固定で呼ぶため2倍速になる。そこで、約60回ごとに「1回あたりの音声の長さ÷申告の1フレーム」を測り、
            // 1.4〜4.5倍のときだけ、その倍率でフレーム間隔を伸ばす(通常のコアは1倍前後なので変わらない)。
            double pacingScale = 1.0;
            int pacingWindowRuns = 0;
            long pacingWindowAudioStart = 0;
            double coreSampleRate = avInfo.timing.sample_rate;

            while (_running)
            {
                double now = sw.Elapsed.TotalMilliseconds;
                // --- 一時停止/再開の切り替わりを検知し、副作用をまとめて反映する ---
                // (この判定は、下のZオーダー再アサート処理より必ず先に行う。
                //  後にすると、一時停止中でも250ms毎の再アサートでSWP_SHOWWINDOWが
                //  発行され、隠したはずの描画ウィンドウがメニューの上に出てきてしまう)
                // 一時停止の要求が取り消された(撮影シーケンスの途中でメニューを閉じた等)場合は、
                // 一時的に変えたレイアウトを元へ戻し、映像表示も再開する。
                if (!_pauseRequested && _hwThumbPhase != 0)
                    AbortHwStateThumbnailCapture();

                if (_pauseRequested != _pausedInternal)
                {
                    // SWレンダリングのコアは、一時停止してしまった後から映像を読み戻す手段が無い
                    // (libretroが渡してくる映像バッファはvideo_refreshコールバックの外では
                    //  有効性が保証されない。HWレンダリングのようにFBOへ残っていて後から
                    //  glReadPixelsできる、という構造ではない)。そのため、メニュー背景用の
                    // サムネイルは「一時停止する直前の最後の1フレーム」でしか捕まえられない。
                    // 一時停止への切り替わりを検知したこのタイミングでは、実はまだ直前の
                    // core.Run()は既に終わっており、そのフレームのvideo_refreshも通り過ぎた後
                    // ―つまり、ここでいきなり_pausedInternalを立てて止めてしまうと、
                    // サムネイル生成のスキップと組み合わさって「捕まえるチャンスが無いまま
                    // 一時停止してしまう」ことになる。
                    // そこで、SWレンダリングの場合に限り、この検知の最初の1回だけは
                    // 実際の一時停止を1ループ分遅らせ、代わりに「このフレームだけサムネイルを
                    // 撮る」フラグを立てて、下の通常のフレーム実行(core.Run())を強制的に
                    // もう1回だけ挟む。次のループでもう一度この分岐に入った時に、今度こそ
                    // 本当に一時停止状態へ切り替える(_pausePendingThumbnailCaptureで
                    // 「もう1回分のフレームは消費済み」を憶えておき、無限に遅延しないようにする)。
                    // HWレンダリングのコアは、従来通りCaptureHwThumbnailでFBOから直接読み戻せる
                    // ため、この仕組みは不要(このifには入らず即座に一時停止する)。
                    if (_pauseRequested && _isHwRendered && StepHwStateThumbnailCapture(ref nextFrame, now))
                    {
                        // ステートセーブ画像の撮影シーケンス中。次のフレームを実行して続きを行う。
                    }
                    else if (_pauseRequested && !_isHwRendered && !_pausePendingThumbnailCapture)
                    {
                        _thumbnailCaptureRequested = true;
                        _pausePendingThumbnailCapture = true;
                        nextFrame = now; // このフレームをすぐ実行させる(通常のフレーム待ちを待たない)
                    }
                    else
                    {
                        _pausedInternal = _pauseRequested;
                        _pausePendingThumbnailCapture = false;

                        if (_pausedInternal)
                        {
                            if (_isHwRendered && _hwFbo != 0)
                                CaptureHwThumbnail(_lastHwFrameWidth, _lastHwFrameHeight);

                            if (_childHwnd != nint.Zero) Win32.ShowWindow(_childHwnd, Win32.SW_HIDE);
                            _audio.Pause();

                            // 中断メニューを開いたタイミングでもゲーム内セーブを書き出しておく
                            // (終了処理を経ずにプロセスが落ちた場合の保険。変化が無ければ何もしない)
                            SavePersistentMemory();

                            ReportStatus("一時停止中(メニュー表示)");

                            // 前回のポーズ時の押しっぱなし状態(長押し/リピートタイマー)を
                            // 引きずらないよう、メニューを開くたびにトラッカーをリセットする。
                            foreach (GamepadEdgeTracker tracker in _pausedMenuTrackers) tracker.Reset();
                            _input.ResetMenuCombo();
                            _input.ResetFastForwardCombo();
                            _input.ResetFastForwardReverseCombo();
                        }
                        else
                        {
                            _hwThumbPhase = 0;
                            _stateThumbValid = false;
                            if (_childHwnd != nint.Zero) Win32.ShowWindow(_childHwnd, Win32.SW_SHOWNA);
                            _audio.ResumeAfterPause();

                            // 再開直後、一時停止していた分を「早送り」で取り戻そうとしないよう
                            // フレームタイミングを現在時刻に合わせ直す
                            nextFrame = now;

                            // 再表示直後は次のループで確実にZオーダーが再アサートされるようにする
                            lastZOrderReassertMs = now - 1000;

                            ReportStatus("再開しました");
                        }
                    }
                }

                // --- リセット要求の処理(一時停止中でも実行して良い) ---
                if (_resetRequested)
                {
                    _resetRequested = false;
                    try
                    {
                        _core?.Reset();
                        nextFrame = now;
                        ClearRewindBuffer();
                        _autoSaveElapsedMs = 0;
                        ReportStatus("リセットしました");
                    }
                    catch (Exception ex)
                    {
                        ReportStatus($"リセット失敗: {ex.Message}");
                    }
                }

                // --- ディスク交換要求の処理(一時停止中でも実行して良い。挿入側は下のTickDiskSwitchで) ---
                ProcessDiskSwitchRequest();

                // --- ステートセーブ/ロード要求の処理(一時停止中でも実行して良い) ---
                if (_pendingSaveSlot != 0)
                {
                    int slot = _pendingSaveSlot;
                    _pendingSaveSlot = 0;
                    SaveStateInternal(slot);
                }

                if (_pendingLoadSlot != 0)
                {
                    int slot = _pendingLoadSlot;
                    _pendingLoadSlot = 0;
                    LoadStateInternal(slot);
                    ClearRewindBuffer(); // どこでもセーブのロードでタイムラインが不連続になるため
                    _autoSaveElapsedMs = 0; // ロード直後からまた5分後にオートセーブされるようにする
                    nextFrame = now; // ロード直後に早送りにならないようにする
                }

                if (_pendingSaveRestart)
                {
                    _pendingSaveRestart = false;
                    SaveRestartInternal();
                }

                // 再起動後のロードは、コアが数フレーム回って落ち着いてから行う(最初のretro_run前は
                // retro_unserializeに失敗するコアがあるため)。
                if (_pendingLoadRestart && _framesRunTotal >= _restartNextAttemptFrame)
                {
                    if (LoadRestartInternal()) _pendingLoadRestart = false;
                    ClearRewindBuffer();
                    _autoSaveElapsedMs = 0;
                    nextFrame = now;
                }

                if (_pendingSaveSuspend)
                {
                    _pendingSaveSuspend = false;
                    SaveSuspendInternal();
                }

                if (_pendingLoadSuspend && _framesRunTotal >= _suspendNextAttemptFrame)
                {
                    if (LoadSuspendInternal()) _pendingLoadSuspend = false;
                    ClearRewindBuffer();
                    _autoSaveElapsedMs = 0;
                    nextFrame = now;
                }

                if (_pendingLoadAutoSave)
                {
                    _pendingLoadAutoSave = false;
                    LoadAutoSaveInternal();
                    ClearRewindBuffer(); // オートセーブのロードでタイムラインが不連続になるため
                    _autoSaveElapsedMs = 0;
                    nextFrame = now; // ロード直後に早送りにならないようにする
                }

                // --- 巻き戻し復元要求の処理(一時停止中でも実行して良い) ---
                if (_pendingRewindIndex >= 0)
                {
                    int rewindIndex = _pendingRewindIndex;
                    _pendingRewindIndex = -1;
                    RewindToInternal(rewindIndex);
                    nextFrame = now; // 復元直後に早送りにならないようにする
                }

                // --- ビデオフィルター等、コアオプション変更要求の処理(一時停止中でも実行して良い) ---
                _coreEnv.ApplyPendingChanges();

                // --- コアオプション変更に紐付く「強制アクション」の処理(同上) ---
                ApplyPendingCoreOptionActions();

                if (_pausedInternal)
                {
                    // 一時停止中は、描画ウィンドウの位置/表示状態を扱う
                    // ApplyPendingBoundsIfAny を一切呼ばない(呼ぶとSWP_SHOWWINDOWで
                    // 強制的に再表示されてしまう)。ウィンドウの終了要求やコントローラの
                    // 抜き差しにだけ反応できるよう、最低限のイベントポンプだけ続ける。
                    Sdl.SDL_PumpEvents();
                    while (Sdl.SDL_PollEvent(out var pausedEv))
                    {
                        if (pausedEv.type == Sdl.SDL_EVENT_QUIT) { _running = false; continue; }
                        _input.HandleDeviceEvent(pausedEv);
                    }

                    // 一時停止中は、このスレッドが唯一のSDLイベントポンプ所有者であり続ける。
                    // GamePlayPage側の中断メニュー(SdlGamepadServiceは停止中)は、
                    // 代わりにこのスレッドが読み取るボタン入力をPausedMenuButton*イベント
                    // 経由で受け取ってナビゲーションを行う。
                    for (int slot = 0; slot < _pausedMenuTrackers.Length; slot++)
                    {
                        _pausedMenuTrackers[slot].Poll(GamepadSlots.GetHandle(slot), DateTime.UtcNow);
                    }

                    // キーボード(矢印キー/Enter/Esc)でも、同じメニュー操作ができる。
                    _input.PollPausedKeyboard(DateTime.UtcNow, b => PausedMenuButtonPressed?.Invoke(b));

                    Thread.Sleep(16);
                    continue;
                }

                // Zオーダーの再アサートは「境界変更時」+「念のため250ms毎」に限定し、
                // 毎ループ(最大1kHz程度)呼び続けることによるオーバーヘッドを避ける。
                bool forceZOrder = now - lastZOrderReassertMs >= 250;
                if (forceZOrder) lastZOrderReassertMs = now;
                ApplyPendingBoundsIfAny(forceZOrder, now);

                if (now >= nextFrame)
                {
                    // このフレームの実行後に巻き戻しキャプチャを行う予定なら、video_refresh側で
                    // オリジナル解像度の変換をしてもらうようフラグを立てておく(毎フレーム変換すると
                    // 高解像度コアで負荷になるため、実際にキャプチャする回だけ行う)。
                    // HWレンダリング中は生ピクセルバッファが取れないため、設定に関わらず無効。
                    if (_rewindEnabled && !_rewindTooSlow && !_isHwRendered && _rewindElapsedSinceCaptureMs + frameTimeMs >= RewindCaptureIntervalMs)
                        _captureFullFrameRequested = true;

                    // オートセーブは、ゲームプレイ中に(一時停止とは無関係に)タイマーで発生する
                    // 唯一のケースなので、同じ「事前予測して都度消費」の手法でこのフレームだけ
                    // サムネイル生成を許可しておく(スクリーンショット用の_rawThumbBgraを
                    // AutoSaveInternal実行時点で新鮮な状態にしておくため)。
                    if (_autoSaveEnabled && !_isHwRendered && _autoSaveElapsedMs + frameTimeMs >= AutoSaveIntervalMs)
                        _thumbnailCaptureRequested = true;

                    _inputPolledThisFrame = false;
                    _core.Run();
                    _framesRunTotal++;

                    if (++pacingWindowRuns >= 60 && coreSampleRate > 0)
                    {
                        long audioNow = _audio.FramesReceived;
                        double audioMs = (audioNow - pacingWindowAudioStart) * 1000.0 / coreSampleRate;
                        double ratio = audioMs / (pacingWindowRuns * frameTimeMs);
                        double newScale = ratio >= 1.4 && ratio <= 4.5 ? Math.Round(ratio, 2) : 1.0;
                        if (Math.Abs(newScale - pacingScale) > 0.05)
                        {
                            EmulatorLauncher.Services.AppLog.Info("Emu",
                                $"[ペース補正] コアが1回のretro_runで申告の{ratio:F2}倍の時間(音声)を進めているため、フレーム間隔の倍率を {pacingScale:F2} → {newScale:F2} にしました");
                            pacingScale = newScale;
                        }
                        pacingWindowRuns = 0;
                        pacingWindowAudioStart = audioNow;
                    }

                    TickDiskSwitch();
                    StepDeviceOptionRefresh();
                    ApplyPendingPortDevice();

                    // FBNeoのROM不足エラー画面のように、コアがretro_input_pollを呼ばない状態では
                    // メニュー呼び出し(ZL+ZR)や早送りコンボ、ウィンドウ終了イベントが処理されなくなる。
                    // そのフレームで一度も呼ばれなかった場合は、ホスト側で代わりにポーリングする。
                    if (!_inputPolledThisFrame)
                        InputPollCallback();

                    // 理論上は不要だが、delegateフィールドおよびcoreインスタンスの
                    // 生存区間について一切の疑いを残さないための明示的な保証。
                    GC.KeepAlive(_core);
                    GC.KeepAlive(_environmentCallback);
                    GC.KeepAlive(_videoRefreshCallback);
                    GC.KeepAlive(_audioSampleBatchCallback);
                    GC.KeepAlive(_inputPollCallback);
                    GC.KeepAlive(_inputStateCallback);

                    // 早送り中(倍率>1)は、次のcore.Run()までの間隔をその倍率分だけ縮める
                    // ことで実現する(1コアフレーム=frameTimeMsという「1回のcore.Run()が
                    // 進めるゲーム内時間」自体は変えず、それを呼ぶ頻度を上げる形)。
                    // 音声は早送り中AudioProcessor側でミュートしている(CycleFastForwardMultiplier
                    // 参照)ため、キュー残量を見た動的レート制御(AdjustFrameTime)は意味を
                    // 持たない(というよりAdjustFrameTime自身もミュート中は素通しになる)。
                    // そのため等倍の時だけ音声同期のレート制御を使い、早送り中は倍率通りの
                    // 間隔で機械的に進める。
                    int fastForwardMultiplier = FastForwardMultiplier;
                    double scaledFrameTimeMs = frameTimeMs * pacingScale;
                    double pacingFrameTimeMs = scaledFrameTimeMs / fastForwardMultiplier;

                    double frameTimeMsAdjusted = fastForwardMultiplier == 1
                        ? _audio.AdjustFrameTime(scaledFrameTimeMs)
                        : pacingFrameTimeMs;

                    nextFrame += frameTimeMsAdjusted;

                    // 大きく遅延した場合はタイミングを再同期(フレームスキップ相当)。
                    // しきい値はpacingFrameTimeMs基準(=早送り中はその分倍率で縮めた間隔)で
                    // 「5フレーム分」に統一しておく(frameTimeMs基準のままだと、早送り中は
                    // 相対的にずっと緩い=遅れに気づきにくいしきい値になってしまうため)。
                    if (now - nextFrame > pacingFrameTimeMs * 5)
                        nextFrame = now;

                    // 巻き戻し用スナップショットを約1秒に1回だけ積む(設定でOFFの間、および
                    // HWレンダリング中(生ピクセルバッファが取れない)は何もしない)。
                    if (_rewindEnabled && !_rewindTooSlow && !_isHwRendered)
                    {
                        _rewindElapsedSinceCaptureMs += frameTimeMs;
                        if (_rewindElapsedSinceCaptureMs >= RewindCaptureIntervalMs)
                        {
                            _rewindElapsedSinceCaptureMs -= RewindCaptureIntervalMs;
                            CaptureRewindFrame();
                        }
                    }
                    else
                    {
                        _rewindElapsedSinceCaptureMs = 0;
                    }

                    // オートセーブを約5分に1回だけ行う(設定でOFFの間、およびHWレンダリング中は無効)。
                    if (_autoSaveEnabled && !_isHwRendered)
                    {
                        _autoSaveElapsedMs += frameTimeMs;
                        if (_autoSaveElapsedMs >= AutoSaveIntervalMs)
                        {
                            _autoSaveElapsedMs -= AutoSaveIntervalMs;
                            AutoSaveInternal();
                        }
                    }
                    else
                    {
                        _autoSaveElapsedMs = 0;
                    }
                }
                else
                {
                    // 粗い待ちはThread.Sleep(1)(timeBeginPeriod(1)込みで概ね1ms程度)、
                    // 残り時間が短くなったら誤差の少ないビジースピンに切り替える。
                    // Sleep一本だと目覚めが数ms単位でブレやすく、それがオーディオコールバックの
                    // 呼び出し間隔のジッタ(≒プチプチ音)に直結するため、着地精度を優先する。
                    double remaining = nextFrame - now;
                    if (remaining > 2.0)
                        Thread.Sleep(1);
                    else if (remaining > 0)
                        Thread.SpinWait(500);
                }
            }
        }
        catch (Exception ex)
        {
            // UI側には要約だけ、Debugコンソールにはスタックトレースまで出す
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [EmulationHost] エラー詳細: {ex}");
            EmulatorLauncher.Services.AppLog.Error("Emu", "エミュレーション中にエラーが発生しました", ex);
            ReportStatus($"エラー: {ex.Message}");
        }
        finally
        {
            // ゲーム内セーブ(SRAM)の書き出しは、retro_unload_game/コンテキスト破棄より前に行う
            // (アンロード後はコアのメモリが解放されるため)。LoadPersistentMemory まで
            // 到達していなければ書き戻し対象が空なので、何も書かない。
            try { SavePersistentMemory(); } catch { /* 後始末なので握りつぶす */ }
            _persistentMemoryReady.Clear();
            _persistentMemoryLastSynced.Clear();

            // HWレンダリングのコアには、GLコンテキストを壊す前に自分のGLリソースを
            // 解放する機会を与える(コンテキストがまだカレントな間に呼ぶ必要がある)。
            if (_isHwRendered && _hwContextDestroyPtr != nint.Zero)
            {
                try
                {
                    var contextDestroy = Marshal.GetDelegateForFunctionPointer<RetroHwContextResetCallback>(_hwContextDestroyPtr);
                    contextDestroy();
                }
                catch { /* 後始末なので握りつぶす */ }
            }

            try { _motionRumble.StopAll(); } catch { }
            try { _core?.UnloadGame(); } catch { /* 後始末なので握りつぶす */ }
            try { _core?.Deinit(); } catch { }
            _core?.Dispose();
            _core = null;

            if (_timerPeriodActive)
            {
                Win32.TimeEndPeriod(1);
                _timerPeriodActive = false;
            }

            if (_mmcssHandle != nint.Zero)
            {
                Win32.AvRevertMmThreadCharacteristics(_mmcssHandle);
                _mmcssHandle = nint.Zero;
            }

            _audio.Close();
            _input.Close();

            // GL関連の後始末(コンテキストがまだカレントなうちに解放する)
            DestroyHwFbo();
            if (_swTexture != 0) { Gl.glDeleteTextures(1, ref _swTexture); _swTexture = 0; }
            _swTexW = 0;
            _swTexH = 0;

            // SDL_Renderer関連の後始末(SWレンダリングのコアで使っていた場合)
            if (_sdlTexture != nint.Zero) { Sdl.SDL_DestroyTexture(_sdlTexture); _sdlTexture = nint.Zero; }
            _sdlTexW = 0;
            _sdlTexH = 0;
            _sdlLogicalW = -1;
            _sdlLogicalH = -1;
            _coreRotationQuarterTurns = 0;
            if (_renderer != nint.Zero) { Sdl.SDL_DestroyRenderer(_renderer); _renderer = nint.Zero; }
            _hqScaleBuffer = Array.Empty<byte>();
            _hqScaleSrcW = 0;
            _hqScaleSrcH = 0;
            _hqScaleBpp = 0;
            _hqScaleFactor = 0;

            if (_glContext != nint.Zero) Sdl.SDL_GL_DestroyContext(_glContext);

            // 表示したまま破棄すると、Windowsの閉じるアニメーション(縮小して消える)が見えてしまう。
            // 先に非表示にしてから破棄する。
            if (_childHwnd != nint.Zero) Win32.ShowWindow(_childHwnd, Win32.SW_HIDE);

            // _childHwnd は今やSDLが作成・所有するウィンドウの実体そのものであり、
            // SDL_DestroyWindow が内部でHWNDの破棄まで行うため、
            // Win32.DestroyWindow(_childHwnd) を別途呼ぶ必要は無い
            // (それどころか、既に破棄済みのハンドルを二重に破棄することになり危険)。
            if (_sdlWindow != nint.Zero) Sdl.SDL_DestroyWindow(_sdlWindow);
            _childHwnd = nint.Zero;

            _coreEnv.Dispose();

            // SDL_Quit() ではなく SDL_QuitSubSystem を使い、アプリ全体で共有している
            // ゲームパッド(SDL_INIT_GAMEPAD)サブシステムは終了させずに残す
            // (メニュー画面へ戻った後もコントローラでの操作を継続できるようにするため)。
            Sdl.SDL_QuitSubSystem(Sdl.SDL_INIT_VIDEO | Sdl.SDL_INIT_AUDIO);

            _glContext = nint.Zero;
            _sdlWindow = nint.Zero;
            _childHwnd = nint.Zero;
            _isHwRendered = false;
            _hwBottomLeftOrigin = true;
            _hwContextResetPtr = nint.Zero;
            _hwContextDestroyPtr = nint.Zero;
            _hwGetCurrentFramebufferCallback = null;
            _hwGetProcAddressCallback = null;

            EmulatorLauncher.Services.AppLog.EndCoreLoading();
            EmulatorLauncher.Services.AppLog.EndCoreSession();
            ReportStatus("停止しました");
        }
    }

    // SDLイベントのうち、デバイス接続・切断に関わるもの、および最初のゲームパッド検出
    // (OpenFirstAvailable)は GamepadInputMapper(_input) へ移動した。SDL_EVENT_QUIT の
    // 検知だけはEmulationHost自身のスレッド終了ロジックなので、各イベントポンプの
    // 呼び出し元(RunLoop / InputPollCallback)側でそれぞれ直接チェックしている。

    // ------------------------------------------------------------
    // libretroコールバック(インスタンスメソッド。delegateはフィールドに保持)
    // ------------------------------------------------------------

    /// <summary>
    /// RETRO_ENVIRONMENT_SET_VARIABLES / GET_VARIABLE は、コア固有のキー名を
    /// ハードコードするのではなく、コアが申告してきた内容をそのまま辞書に
    /// 保存して汎用的に answer する。こうすることで genesis-plus-gx や
    /// mgba、mednafen_pce、mame2003-plus 等、事前に個別対応していない
    /// コアでも「宣言されたデフォルト値」がそのまま通るようになる。
    /// </summary>
    private bool EnvironmentCallback(uint cmd, nint data)
    {
        switch (cmd)
        {
            case RetroEnvironment.SET_ROTATION:
                if (data == nint.Zero) return false;

                // data は uint のポインタ (0: 回転なし, 1: 反時計回りに90度, 2: 180度, 3: 反時計回りに270度)
                uint rotation = (uint)Marshal.ReadInt32(data) & 3;

                if (rotation != _coreRotationQuarterTurns)
                {
                    Debug.WriteLine($"[EmulationHost] SET_ROTATION : {_coreRotationQuarterTurns} => {rotation} (90度単位、反時計回り)");
                    _coreRotationQuarterTurns = rotation;
                }

                return true;

            case RetroEnvironment.SET_PIXEL_FORMAT:
                if (data == nint.Zero) return false;
                _pixelFormat = (RetroPixelFormat)Marshal.ReadInt32(data);
                return true;

            // N64コア(mupen64plus-next, parallel-n64等)のように、ソフトウェアではなく
            // OpenGLで直接描画したいコアが、その旨とコンテキストの要求仕様を申告してくる。
            // フロントエンド側は get_current_framebuffer / get_proc_address を埋めて
            // 同じ構造体へ書き戻し、対応可能なら true を返す。
            case RetroEnvironment.SET_HW_RENDER:
                {
                    if (data == nint.Zero) return false;

                    var hw = Marshal.PtrToStructure<RetroHwRenderCallback>(data);
                    var contextType = (RetroHwContextType)hw.context_type;

                    // Vulkan/Direct3D系は未対応(OpenGL系のみ対応)。
                    if (contextType != RetroHwContextType.OPENGL
                        && contextType != RetroHwContextType.OPENGL_CORE
                        && contextType != RetroHwContextType.OPENGLES2
                        && contextType != RetroHwContextType.OPENGLES3)
                    {
                        ReportStatus($"HWレンダリング非対応: {contextType} (OpenGL系のみ対応)");
                        return false;
                    }

                    // 既存のGLコンテキストは「バージョン/プロファイル未指定」で作られたものなので、
                    // コアが要求する context_type/version に合わせて作り直す。
                    // これをやらないと、Core Profile限定関数へのポインタが不正な状態のまま
                    // get_proc_address 経由でコアに渡ってしまい、コアがそれを叩いた瞬間に
                    // ドライバ〜プロセス全体が未定義動作に陥る。
                    //
                    // このcaseはretro_load_game中の1回目だけでなく、コアによっては
                    // retro_run()の最中(ゲーム実行中)に「もう一度」SET_HW_RENDERを
                    // 送ってくることがある(melonDS-DS実機検証で確認: 起動時のHWレンダリング
                    // コアオプション申告とは別に、実際にretro_runが回り始めてから改めて
                    // OPENGL_CORE 3.1を要求し直してきた)。RecreateGlContextは古いGLコンテキストを
                    // 破棄して新しいものを作るため、2回目以降の呼び出しでは、それ以前に
                    // CreateHwFbo()で作っていた_hwFbo/_hwColorTex等のハンドルが「もう存在しない
                    // 古いコンテキストのもの」として無効化されてしまう。それを放置したまま
                    // コアがget_current_framebuffer経由で古いハンドル(_hwFbo)へ描画しようと
                    // すると、GPUドライバ〜プロセス全体が未定義動作に陥り、
                    // System.ExecutionEngineException のような致命的なクラッシュとして
                    // 表面化する(実機検証で再現したクラッシュはこれで説明がつく)。
                    // そのため、2回目以降の呼び出しでは、コンテキスト再作成の直後に
                    // FBOも必ず作り直し、コア側にもcontext_resetで「GLリソースを作り直して」と
                    // 伝え直す(1回目はStart()側が同じ処理をLoadGame完了後に行うため、
                    // ここでは行わない)。
                    bool isRenegotiation = _hwFbo != 0;

                    if (!RecreateGlContext(contextType, hw.version_major, hw.version_minor))
                    {
                        ReportStatus($"要求されたGLコンテキストの作成に失敗しました: {contextType} {hw.version_major}.{hw.version_minor}");
                        return false;
                    }

                    // コンテキストを作り直したので、GL関数ポインタも解決し直す。
                    Gl.Load(Sdl.SDL_GL_GetProcAddress);

                    _isHwRendered = true;
                    _hwDepth = hw.depth;
                    _hwStencil = hw.stencil;
                    _hwBottomLeftOrigin = hw.bottom_left_origin;
                    _hwContextResetPtr = hw.context_reset;
                    _hwContextDestroyPtr = hw.context_destroy;

                    // delegateはコアに関数ポインタとして握られ続けるため、GCされないよう
                    // フィールドへ保持してから関数ポインタ化する。
                    _hwGetCurrentFramebufferCallback = HwGetCurrentFramebuffer;
                    _hwGetProcAddressCallback = HwGetProcAddress;
                    hw.get_current_framebuffer = Marshal.GetFunctionPointerForDelegate(_hwGetCurrentFramebufferCallback);
                    hw.get_proc_address = Marshal.GetFunctionPointerForDelegate(_hwGetProcAddressCallback);

                    Marshal.StructureToPtr(hw, data, false);

                    if (isRenegotiation)
                    {
                        // 直前のCreateHwFbo呼び出し時のサイズをそのまま踏襲する
                        // (このタイミングではretro_get_system_av_infoを呼び直せないため、
                        // 前回確保した実サイズを維持するのが妥当)。
                        int fboW = _hwFboWidth > 0 ? _hwFboWidth : 640;
                        int fboH = _hwFboHeight > 0 ? _hwFboHeight : 480;
                        CreateHwFbo(fboW, fboH);

                        if (_hwContextResetPtr != nint.Zero)
                        {
                            var contextReset = Marshal.GetDelegateForFunctionPointer<RetroHwContextResetCallback>(_hwContextResetPtr);
                            contextReset();
                        }

                        ReportStatus(
                            $"HWレンダリング: retro_run中に再要求されたためコンテキスト/FBOを再作成しました "
                            + $"({fboW}x{fboH}, depth={_hwDepth}, stencil={_hwStencil})");
                    }

                    ReportStatus($"HWレンダリング要求を受理しました: {contextType} {hw.version_major}.{hw.version_minor}");
                    return true;
                }

            case RetroEnvironment.GET_CAN_DUPE:
                if (data == nint.Zero) return false;
                Marshal.WriteByte(data, 1);
                return true;

            case RetroEnvironment.SET_SUPPORT_NO_GAME:
                if (data != nint.Zero) _coreSupportsNoGame = Marshal.ReadByte(data) != 0;
                return true;

            // System Directory(BIOS等)とSave Directory(セーブファイル)は
            // ROMごとのフォルダではなく、実行ファイル基準+コアごとのディレクトリにする
            // (system/<コアファイル名>、saves/<コアファイル名>)。
            // PCエンジン(mednafen_pce系)やMAME系はSystem DirectoryにBIOSや
            // 追加ROMを置く運用が前提のため、ROMフォルダ依存だと詰む。
            // GET_SYSTEM_DIRECTORY/GET_SAVE_DIRECTORY/SET_VARIABLES/GET_VARIABLE/
            // GET_VARIABLE_UPDATEの実処理はCoreEnvironmentSupport(_coreEnv)へ移動した
            // (映像/HWレンダリング状態に一切触れない自己完結した処理のため)。
            case RetroEnvironment.GET_SYSTEM_DIRECTORY:
                return _coreEnv.HandleGetSystemDirectory(data);

            case RetroEnvironment.GET_SAVE_DIRECTORY:
                return _coreEnv.HandleGetSaveDirectory(data);

            case RetroEnvironment.SET_VARIABLES:
                return _coreEnv.HandleSetVariables(data);

            // SET_VARIABLESの後継(V1/V2)。supermodel等はこちらでオプションを申告してくる。
            case RetroEnvironment.SET_CORE_OPTIONS:
                return _coreEnv.HandleSetCoreOptionsV1(data);
            case RetroEnvironment.SET_CORE_OPTIONS_INTL:
                return _coreEnv.HandleSetCoreOptionsV1Intl(data);
            case RetroEnvironment.SET_CORE_OPTIONS_V2:
                return _coreEnv.HandleSetCoreOptionsV2(data);
            case RetroEnvironment.SET_CORE_OPTIONS_V2_INTL:
                return _coreEnv.HandleSetCoreOptionsV2Intl(data);

            case RetroEnvironment.GET_VARIABLE:
                return _coreEnv.HandleGetVariable(data);

            case RetroEnvironment.GET_VARIABLE_UPDATE:
                return _coreEnv.HandleGetVariableUpdate(data);

            // 値そのものは使っていないが、trueを返すことでポート構成周りの
            // 初期化を素直に済ませてくれるコアがあるため許容しておく。
            case RetroEnvironment.SET_INPUT_DESCRIPTORS:
                InputDiagnostics.LogInputDescriptors(data);
                return true;

            // キーボードのキー通知(retro_keyboard_callback { callback })。キーボード直通のときだけ、押し/離しを送る。
            case RetroEnvironment.SET_KEYBOARD_CALLBACK:
                if (data == nint.Zero) return false;
                _input.SetKeyboardCallback(Marshal.ReadIntPtr(data));
                return true;

            // コントローラの種類の一覧(診断ログだけ出す。従来どおり非対応として扱う)。
            case RetroEnvironment.SET_CONTROLLER_INFO:
                InputDiagnostics.LogControllerInfo(data);
                return false;

            // 対応デバイス種別のビットマスクを返す。JOYPADのみ対応していると申告する。
            case RetroEnvironment.GET_INPUT_DEVICE_CAPABILITIES:
                {
                    if (data == nint.Zero) return false;
                    // JOYPADに加えてANALOG(アナログスティック)にも対応していると申告する。
                    // これによりN64系コア(mupen64plus-next等)がスティック入力を使ってくれる。
                    ulong caps = (1UL << (int)RetroDevice.JOYPAD) | (1UL << (int)RetroDevice.ANALOG);
                    if (KeyboardPassthrough) caps |= 1UL << (int)RetroDevice.KEYBOARD;
                    Marshal.WriteInt64(data, unchecked((long)caps));
                    return true;
                }

            case RetroEnvironment.SET_GEOMETRY:
                {
                    if (data != nint.Zero)
                    {
                        var geometry = Marshal.PtrToStructure<RetroGameGeometry>(data);
                        double newAspect = ComputeAspectRatio(geometry);
                        Debug.WriteLine(
                            $"[EmulationHost] SET_GEOMETRY: base={geometry.base_width}x{geometry.base_height} "
                            + $"max={geometry.max_width}x{geometry.max_height} aspect_ratio(raw)={geometry.aspect_ratio} "
                            + $"=> computedAspect={newAspect:F4} (現在のCoreReportedAspectRatio={CoreReportedAspectRatio:F4})");

                        // SET_GEOMETRYを送ってきたコアについては、これが最新の「正しい」
                        // 基準なので、resyncGeometryOnFrameSizeChangeアクション(上記参照)用の
                        // 補正係数もここで必ず更新しておく(アスペクト比自体に変化が無くても、
                        // base_width/heightだけ変わるケースはありうるため)。
                        UpdateGeometryPixelCorrectionFactor(geometry, newAspect);

                        if (Math.Abs(newAspect - CoreReportedAspectRatio) > 0.0001)
                        {
                            CoreReportedAspectRatio = newAspect;
                            Debug.WriteLine($"[EmulationHost] SET_GEOMETRY: CoreReportedAspectRatio を更新 => {newAspect:F4}。GeometryChangedを発火します。");
                            GeometryChanged?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            Debug.WriteLine("[EmulationHost] SET_GEOMETRY: アスペクト比が閾値以下の変化のため無視(GeometryChangedは発火しません)。");
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[EmulationHost] SET_GEOMETRY: data == nint.Zero (無視)");
                    }
                    return true;
                }

            case RetroEnvironment.SET_MESSAGE:
                return true;

            // ディスク交換(FDSのA面/B面、複数枚組ディスク)。詳細は EmulationHost.Disk.cs。
            case RetroEnvironment.SET_DISK_CONTROL_INTERFACE:
                _disk = DiskControl.Create(data, ext: false);
                Debug.WriteLine($"[Disk] SET_DISK_CONTROL_INTERFACE => {(_disk is null ? "失敗" : $"登録 images={_disk.NumImages}")}");
                return _disk is not null;

            case RetroEnvironment.SET_DISK_CONTROL_EXT_INTERFACE:
                _disk = DiskControl.Create(data, ext: true);
                Debug.WriteLine($"[Disk] SET_DISK_CONTROL_EXT_INTERFACE => {(_disk is null ? "失敗" : $"登録 images={_disk.NumImages}")}");
                return _disk is not null;

            case RetroEnvironment.GET_DISK_CONTROL_INTERFACE_VERSION:
                Debug.WriteLine("[Disk] GET_DISK_CONTROL_INTERFACE_VERSION");
                if (data == nint.Zero) return false;
                Marshal.WriteInt32(data, 1); // EXTインターフェース(version 1)に対応
                return true;

            // GET_LOG_INTERFACEの実処理もCoreEnvironmentSupport(_coreEnv)へ移動した。
            case RetroEnvironment.GET_LOG_INTERFACE:
                return _coreEnv.HandleGetLogInterface(data);

            // ジャイロ/加速度(センサー)と振動をゲームパッド(DualSense等)へ繋ぐ。
            case RetroEnvironment.GET_SENSOR_INTERFACE:
                return _motionRumble.HandleGetSensorInterface(data);

            case RetroEnvironment.GET_RUMBLE_INTERFACE:
                return _motionRumble.HandleGetRumbleInterface(data);

            default:
                if (_loggedUnhandledEnv.Add(cmd))
                    Debug.WriteLine($"[Env] 未対応のenvironmentコマンド: {cmd}");
                // 未対応のenvironmentコマンド。falseを返すのは「非対応」の正常な応答であり、
                // 多くのコアはこれを許容してデフォルト動作にフォールバックする。
                return false;
        }
    }

    // retro_audio_sample_batch_t の実処理は AudioProcessor.SampleBatch (_audio) へ、
    // retro_input_state_t の実処理は GamepadInputMapper.InputState (_input) へ移動した。
    // retro_input_poll_t は、SDL_EVENT_QUIT の検知(スレッド終了)がEmulationHost自身の
    // ライフサイクル管理の一部であるため、薄いラッパーとしてここに残す。
    private bool _inputPolledThisFrame;

    /// <summary>
    /// ステートセーブ画像を「上画面のみ」で撮るために一時的に切り替える、コアオプションの一覧
    /// (現在値と違うものだけ)。対象コアでなければ空。
    /// </summary>
    private List<(string Key, string Value)> GetHwThumbnailLayoutOverrides()
    {
        var list = new List<(string Key, string Value)>();

        void AddIfDifferent(string key, string value)
        {
            if (_coreEnv.HasVariable(key) && _coreEnv.GetVariableValue(key) != value)
                list.Add((key, value));
        }

        // 3DS(Citra): 「1画面のみ」+ 上画面
        if (_coreEnv.HasVariable("citra_layout_option"))
        {
            AddIfDifferent("citra_layout_option", "Single Screen Only");
            AddIfDifferent("citra_swap_screen", "Top");
        }

        // DS(melonDS): 「上画面のみ」
        AddIfDifferent("melonds_screen_layout", "Top Only");

        return list;
    }

    /// <summary>
    /// HWレンダリングのコア(DS/3DS)の一時停止直前に、ステートセーブ画像を上画面のみで撮る
    /// シーケンスを1ステップ進める。撮影が必要で進行中ならtrue(呼び出し側は次のフレームを
    /// 実行してから再度呼ぶ)。不要・完了ならfalse(そのまま一時停止してよい)。
    /// エミュレーションスレッドのループ内からのみ呼ぶ。
    /// </summary>
    private bool StepHwStateThumbnailCapture(ref double nextFrame, double now)
    {
        switch (_hwThumbPhase)
        {
            case 0:
            {
                var overrides = GetHwThumbnailLayoutOverrides();
                if (overrides.Count == 0) return false; // 対象外のコア、または既に上画面のみ

                _hwThumbSaved = overrides
                    .Select(o => (o.Key, _coreEnv.GetVariableValue(o.Key) ?? o.Value))
                    .ToList();
                foreach (var o in overrides) _coreEnv.SetVariableQuiet(o.Key, o.Value);

                _suppressPresent = true;
                _hwThumbFrames = 0;
                _hwThumbPhase = 1;
                nextFrame = now;
                return true;
            }

            case 1:
                // コアがレイアウト変更を反映するまでに1フレーム以上かかることがあるため、2フレーム進める。
                if (++_hwThumbFrames < 3) { nextFrame = now; return true; }

                _stateThumbValid = false;
                if (_hwFbo != 0)
                    CaptureHwThumbnail(_lastHwFrameWidth, _lastHwFrameHeight, stateOnly: true);

                RestoreHwThumbnailOverrides();
                _hwThumbFrames = 0;
                _hwThumbPhase = 2;
                nextFrame = now;
                return true;

            case 2:
                // 元のレイアウトへ戻ったフレームを2フレーム進めてから(この間も非表示)一時停止する。
                if (++_hwThumbFrames < 2) { nextFrame = now; return true; }

                _suppressPresent = false;
                _hwThumbPhase = 3;
                return false;

            default:
                return false;
        }
    }

    private void RestoreHwThumbnailOverrides()
    {
        if (_hwThumbSaved is null) return;
        foreach (var saved in _hwThumbSaved) _coreEnv.SetVariableQuiet(saved.Key, saved.Value);
        _hwThumbSaved = null;
    }

    /// <summary>撮影シーケンスの途中で一時停止が取り消された場合の後始末。</summary>
    private void AbortHwStateThumbnailCapture()
    {
        RestoreHwThumbnailOverrides();
        _suppressPresent = false;
        _hwThumbPhase = 0;
        _hwThumbFrames = 0;
    }

    // ------------------------------------------------------------------
    // Flycast(ドリームキャスト)のメモリカード(VMU)認識の補正
    //
    // 起動直後の状態では、設定上は拡張スロットにVMUが挿さっていても、ゲームからは
    // 認識されないことがある。設定画面で一度別の値へ変えてから戻すと認識される
    // (コアが「オプションが変わった」ことをきっかけに周辺機器を挿し直すため)。
    // それと同じ操作を、起動して少し経ってから自動で1回だけ行う。
    // ------------------------------------------------------------------
    private static readonly string[] DeviceSlotKeys =
    {
        "reicast_device_port1_slot1", "reicast_device_port1_slot2",
        "reicast_device_port2_slot1", "reicast_device_port2_slot2",
        "reicast_device_port3_slot1", "reicast_device_port3_slot2",
        "reicast_device_port4_slot1", "reicast_device_port4_slot2",
    };

    private int _deviceRefreshPhase;
    private int _deviceRefreshFrames;
    private List<(string Key, string Value)>? _deviceRefreshSaved;

    /// <summary>core.Run()の直後に毎回呼ぶ。対象外のコアでは最初の呼び出しで何もしなくなる。</summary>
    private void StepDeviceOptionRefresh()
    {
        if (_deviceRefreshPhase >= 3) return;

        if (_deviceRefreshPhase == 0 && !_coreEnv.HasVariable(DeviceSlotKeys[0]))
        {
            _deviceRefreshPhase = 3; // 周辺機器オプションを持たないコア
            return;
        }

        _deviceRefreshFrames++;
        switch (_deviceRefreshPhase)
        {
            case 0:
                // 起動して約2秒後(コアの初期化が一通り終わってから)。
                if (_deviceRefreshFrames < 120) return;

                _deviceRefreshSaved = new List<(string, string)>();
                foreach (string key in DeviceSlotKeys)
                {
                    string? current = _coreEnv.GetVariableValue(key);
                    if (current is null) continue;
                    _deviceRefreshSaved.Add((key, current));
                    // 「設定を順送りして別の値へ変える」操作の代わり。VMUとぷるぷるぱっくを入れ替える。
                    _coreEnv.SetVariableQuiet(key, current == "VMU" ? "Purupuru" : "VMU");
                }
                _deviceRefreshFrames = 0;
                _deviceRefreshPhase = 1;
                break;

            case 1:
                // コアが変更を取り込むまで2フレーム待ってから、元の値へ戻す。
                if (_deviceRefreshFrames < 2) return;
                if (_deviceRefreshSaved is not null)
                {
                    foreach (var saved in _deviceRefreshSaved) _coreEnv.SetVariableQuiet(saved.Key, saved.Value);
                    _deviceRefreshSaved = null;
                }
                _deviceRefreshFrames = 0;
                _deviceRefreshPhase = 2;
                break;

            case 2:
                if (_deviceRefreshFrames < 2) return;
                _deviceRefreshPhase = 3;
                break;
        }
    }

    private void InputPollCallback()
    {
        _inputPolledThisFrame = true;
        Sdl.SDL_PumpEvents();

        while (Sdl.SDL_PollEvent(out var ev))
        {
            if (ev.type == Sdl.SDL_EVENT_QUIT) { _running = false; continue; }
            if (ev.type == Sdl.SDL_EVENT_GAMEPAD_SENSOR_UPDATE) { _input.HandleSensorEvent(ev); continue; }
            _input.HandleDeviceEvent(ev);
        }

        _input.CapturePads();
        _input.RefreshKeyboardState();
        _input.DispatchKeyboardEvents();
        _input.PollStickCursor();
        _input.PollMouse();
        _input.DetectMenuToggleCombo(_isHwRendered);
        _input.DetectKeyboardMenuKey();
        _input.DetectFastForwardCombo();
        _input.DetectFastForwardReverseCombo();
        _input.UpdateHotkeySuppression();
        _input.GyroPointerEnabled = _wiiGyroPointer;
        // ウィンドウの外へ出て戻ってきたとき(またはしばらく経ったとき)にカーソルが再表示されたままになるため、
        // 画面内に入った瞬間と、画面内にいる間は約0.5秒ごとに、隠す指定をやり直す。
        bool inside = _input.MouseInside;
        bool hideWanted = _hideCursorWanted && !KeepCursorVisible;
        if (hideWanted != _cursorHidden)
        {
            _cursorHidden = hideWanted;
            if (_cursorHidden) Sdl.SDL_HideCursor(); else Sdl.SDL_ShowCursor();
        }
        else if (_cursorHidden && inside && (!_cursorWasInside || ++_cursorRehideCounter % 30 == 0))
        {
            Sdl.SDL_ShowCursor();
            Sdl.SDL_HideCursor();
        }
        _cursorWasInside = inside;
        _input.PollGyroPointer();
        _input.PollLightgunAim();
        _input.DetectPointerRecenterCombo();
    }
}