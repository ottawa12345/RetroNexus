using System;
using System.Collections.Generic;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// EmulationHostのうち、巻き戻し(Rewind)機能を担当する部分。EmulationHost.csからの分割(2回目)。
/// フィールド・メソッドは全てEmulationHost本体と共有される(partial class)。
/// キャプチャ元データ(_fullFrameBgra等)を書き込む側(VideoRefreshCallback経由の
/// CaptureFullFrameForRewind)はまだメインファイル側にある(映像/描画の分割候補)。
/// </summary>
public sealed partial class EmulationHost
{
    // ================= 巻き戻し(Rewind) =================
    // 実行中、約1秒毎にステートセーブ相当のスナップショット(ステート本体+その瞬間の
    // スクリーンショット)をリングバッファへ積み続け、直近 MaxRewindFrames 個まで保持する。
    // キャプチャ/復元はどちらもエミュレーションスレッドのループ内でのみ行う
    // (retro_serialize/unserializeをそれ以外のスレッドから呼ばないため)。
    // UIスレッドはロック越しにスナップショット(配列コピー)だけを読み取る。

    public const int MaxRewindFrames = 30;
    private const double RewindCaptureIntervalMs = 1000.0;

    // キャプチャの所要時間の記録(原因調査用。間隔は変えない)。
    private double _rewindCostMaxMs;
    private int _rewindCaptureCount;
    private int _rewindStateSize;

    // ステートの保存が遅すぎるコア(MAME 2003 Plus は76KBで約370ms)では、キャプチャのたびに
    // 画面が止まり、巻き戻しより害が大きい。連続して遅かったら、そのゲームでは巻き戻しを止める
    // (巻き戻し画面には「データがありません」と出る)。コアの種類には依存しない。
    private const double RewindTooSlowMs = 100.0;
    private const int RewindTooSlowLimit = 2;
    private int _rewindSlowStreak;
    private volatile bool _rewindTooSlow;

    /// <summary>ステートの保存が遅すぎて、このゲームでは巻き戻しを止めたか(操作説明から外すために使う)。</summary>
    public bool IsRewindUnavailable => _rewindTooSlow;

    /// <summary>巻き戻しを止めたときに、エミュレーションスレッドから通知する。</summary>
    public event EventHandler? RewindBecameUnavailable;

    // 取り除いた最古フレームのステート用配列。同じ大きさなら再利用して、巨大な配列の確保を繰り返さない。
    private byte[]? _spareStateBuffer;

    /// <summary>
    /// 巻き戻し機能を使うかどうか(「設定」画面のトグルに対応)。UIスレッドから変更可能で、
    /// 次にメインループが回った時点から即座に反映される(OFFの間は新たなキャプチャを行わない)。
    /// </summary>
    public bool RewindEnabled
    {
        get => _rewindEnabled;
        set => _rewindEnabled = value;
    }
    private volatile bool _rewindEnabled = true;

    /// <summary>巻き戻し1コマ分のデータ。UI側での一覧表示・復元に使う。</summary>
    public sealed class RewindFrame
    {
        public required byte[] StateData { get; init; }
        public required byte[] ScreenshotBgra { get; init; }
        public required int ScreenshotWidth { get; init; }
        public required int ScreenshotHeight { get; init; }
    }

    private readonly object _rewindLock = new();
    private readonly List<RewindFrame> _rewindFrames = new();
    private double _rewindElapsedSinceCaptureMs;

    // UIスレッドから要求される、復元対象のインデックス(0=最も古い)。-1 = 要求なし。
    private volatile int _pendingRewindIndex = -1;

    // 巻き戻し用に「劣化なしのオリジナル解像度」でフレームを1枚だけ保持するバッファ。
    // メニュー背景用の _rawThumbBgra は表示負荷を抑えるために縮小・ブラー済みのため、
    // 巻き戻し画面のプレビューにはこちらの高画質版を使う。
    // 毎フレーム変換すると重いコアで負荷になり得るため、実際に巻き戻しキャプチャを
    // 行う予定のフレーム(約1秒に1回)だけ、VideoRefreshCallback側で変換する。
    private volatile bool _captureFullFrameRequested;
    private byte[] _fullFrameBgra = Array.Empty<byte>();
    private int _fullFrameWidth;
    private int _fullFrameHeight;
    private bool _fullFrameValid;

    // ================= 巻き戻し(Rewind) =================

    /// <summary>
    /// 現在保持している巻き戻しフレームのスナップショットを、古い順の配列として取得する。
    /// UIスレッドから呼び出し可能(ロックした上で配列をコピーするだけなので安全)。
    /// </summary>
    public IReadOnlyList<RewindFrame> GetRewindFrames()
    {
        lock (_rewindLock)
        {
            return _rewindFrames.ToArray();
        }
    }

    /// <summary>
    /// 次にエミュレーションスレッドのループが回ったタイミングで、GetRewindFrames() が返した
    /// 配列における index (0-based, 古い順) の地点へ復元する。復元後、それより新しい
    /// 巻き戻しフレームは(その未来が失われたため)破棄される。
    /// </summary>
    public void RequestRewindTo(int index)
    {
        if (index < 0) return;
        _pendingRewindIndex = index;
    }

    /// <summary>
    /// 巻き戻し用のスナップショットを1つ取る。エミュレーションスレッドのループ内から、
    /// 約1秒に1回だけ呼ばれる想定(retro_run 同様、必ずエミュレーションスレッド上で実行する)。
    /// </summary>
    private void CaptureRewindFrame()
    {
        if (_core is null) return;
        if (!_fullFrameValid) return; // まだオリジナル解像度フレームが1枚も来ていない

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long logCallsBefore = System.Threading.Interlocked.Read(ref CoreEnvironmentSupport.LogCallCount);

        // retro_serialize_size はコアによっては重い(状態の保存を試して大きさを数える)ため、
        // 毎回は呼ばず、最初に調べた値を使い回す。保存に失敗したときだけ調べ直す。
        bool sizeQueried = false;
        if (_rewindStateSize <= 0)
        {
            _rewindStateSize = _core.SerializeSize();
            sizeQueried = true;
        }

        int size = _rewindStateSize;
        if (size <= 0) return; // このコアはステートセーブ非対応 → 巻き戻しも不可
        double sizeMs = sw.Elapsed.TotalMilliseconds;

        byte[] stateBuffer = _spareStateBuffer is { } spare && spare.Length == size ? spare : new byte[size];
        _spareStateBuffer = null;
        if (!_core.Serialize(stateBuffer))
        {
            _rewindStateSize = 0; // 次回、大きさを調べ直す
            return;
        }
        double serializeMs = sw.Elapsed.TotalMilliseconds - sizeMs;

        // オリジナル解像度・無劣化のBGRAをそのまま複製する
        // (この関数はエミュレーションスレッドからしか呼ばれず、_fullFrameBgra を書くのも
        //  同じスレッド上のVideoRefreshCallbackだけなのでロック不要)。
        byte[] shot = (byte[])_fullFrameBgra.Clone();
        int w = _fullFrameWidth;
        int h = _fullFrameHeight;

        var frame = new RewindFrame
        {
            StateData = stateBuffer,
            ScreenshotBgra = shot,
            ScreenshotWidth = w,
            ScreenshotHeight = h,
        };

        lock (_rewindLock)
        {
            _rewindFrames.Add(frame);
            if (_rewindFrames.Count > MaxRewindFrames)
            {
                _spareStateBuffer = _rewindFrames[0].StateData;
                _rewindFrames.RemoveAt(0);
            }
        }

        double totalMs = sw.Elapsed.TotalMilliseconds;
        long logCalls = System.Threading.Interlocked.Read(ref CoreEnvironmentSupport.LogCallCount) - logCallsBefore;
        LogRewindCost(totalMs, sizeQueried ? sizeMs : 0, serializeMs, size, logCalls);

        _rewindSlowStreak = totalMs > RewindTooSlowMs ? _rewindSlowStreak + 1 : 0;
        if (_rewindSlowStreak >= RewindTooSlowLimit)
        {
            _rewindTooSlow = true;
            lock (_rewindLock) { _rewindFrames.Clear(); }
            EmulatorLauncher.Services.AppLog.Warn("Emu",
                $"このゲームではステートの保存が遅く(約 {totalMs:F0} ms)、画面が止まるため、巻き戻しを止めました");
            RewindBecameUnavailable?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>キャプチャの所要時間をログに残す(最初の3回と、これまでの最大を大きく超えたとき)。</summary>
    private void LogRewindCost(double totalMs, double sizeMs, double serializeMs, int stateSize, long logCalls)
    {
        _rewindCaptureCount++;
        if (_rewindCaptureCount > 3 && totalMs <= _rewindCostMaxMs * 1.5) return;

        _rewindCostMaxMs = Math.Max(_rewindCostMaxMs, totalMs);
        EmulatorLauncher.Services.AppLog.Info("Emu",
            $"巻き戻しのキャプチャ({_rewindCaptureCount} 回目): ステート {stateSize / 1024} KB / サイズ確認 {sizeMs:F1} ms・保存 {serializeMs:F1} ms・合計 {totalMs:F1} ms / コアのログ呼び出し {logCalls} 回");
    }

    /// <summary>指定インデックスの巻き戻しフレームへ復元する。エミュレーションスレッドのループ内からのみ呼ばれる。</summary>
    private void RewindToInternal(int index)
    {
        if (_core is null) return;

        RewindFrame? frame = null;
        lock (_rewindLock)
        {
            if (index >= 0 && index < _rewindFrames.Count)
            {
                frame = _rewindFrames[index];

                // 選択した時点より未来の履歴は失われるため、以降のフレームは破棄する
                int removeStart = index + 1;
                if (removeStart < _rewindFrames.Count)
                    _rewindFrames.RemoveRange(removeStart, _rewindFrames.Count - removeStart);
            }
        }

        if (frame is null)
        {
            ReportStatus("巻き戻し失敗: 対象のフレームがありません");
            return;
        }

        if (!_core.Unserialize(frame.StateData))
        {
            ReportStatus("巻き戻し失敗: retro_unserializeが失敗しました");
            return;
        }

        _rewindElapsedSinceCaptureMs = 0;
        ReportStatus("巻き戻しました");
    }

    /// <summary>巻き戻しバッファを空にする(リセット/手動ロード等、タイムラインが不連続になるタイミングで呼ぶ)。</summary>
    private void ClearRewindBuffer()
    {
        lock (_rewindLock)
        {
            _rewindFrames.Clear();
        }
        _rewindElapsedSinceCaptureMs = 0;
    }

}
