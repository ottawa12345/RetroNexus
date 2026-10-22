using System;
using System.Runtime.InteropServices;
using EmulatorLauncher.Input;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// libretroコアの音声出力(retro_audio_sample_batch_t)を受け取り、SDL3のオーディオストリームへ
/// 流し込む処理を担当するクラス。<see cref="EmulationHost"/>の内部分割(4回目)の一環として、
/// 単なるファイル分割(partial class)ではなく実際に独立したクラスとして切り出した
/// (音声ストリームのライフサイクルと動的レート制御が、映像/入力から見て完全に自己完結しているため)。
///
/// EmulationHostは、このクラスのインスタンスをフィールドとして保持し、
/// retro_audio_sample_batch_t 用のdelegateは <see cref="SampleBatch"/> を指すように
/// EmulationHost側で生成・保持する(delegateのライフタイム管理はEmulationHost.cs側の
/// 既存ルールに合わせる)。
/// </summary>
internal sealed class AudioProcessor
{
    private readonly Action<string> _reportStatus;

    private nint _stream;
    private byte[] _buffer = Array.Empty<byte>();

    // 音声アンダーラン対策用の「低水位」しきい値(バイト数)。
    // 固定値ではなく、コアの実サンプルレートに応じて算出する
    // (サンプルレートが高いコアで固定値4096だと相対的に短すぎ、
    //  低いコアだと長すぎて効果が出るタイミングがずれるため)。
    private int _lowWatermarkBytes = 4096;

    /// <summary>SDLのオーディオデバイスバッファのフレーム数(EmulationHostがヒントとして指定する)。</summary>
    internal const int DeviceSampleFrames = 2048;

    // 目標キュー量(バイト)。SDLはデバイスのバッファ1回分(DeviceSampleFrames)を一度に引き取るため、
    // キュー量がそれより少ないとアンダーラン(プチプチ/ジャリジャリ音)になる。
    // 32768HzのGBAでは2048フレーム≒62msあり、従来の目標(約40ms)では常に不足していた。
    private int _targetQueuedBytes = 0;
    private double _queuedEmaBytes = -1; // -1 = 未初期化
    private const double RateControlEmaAlpha = 0.02;

    // フレーム待ち時間の補正幅。以前は±1%だったが、PX68k(X68000)のように「申告するfpsと、
    // 音声を出すペースが7%ほどずれている」コアでは足りず、音が数秒遅れた。
    // 再生速度(ピッチ)を変えずに解決するため、ゲームの進行ペース自体を最大±10%まで
    // 音声側に合わせる(音声を基準にゲーム速度を決める。実機の実速度に一致する)。
    // ゲインは小さめ(0.04)にして、EMAの遅れがあっても振動しないようにしてある
    // (誤差が大きいほど強く補正され、7%のずれなら残量が目標の約2.7倍で釣り合う)。
    private const double RateControlMaxAdjust = 0.10;
    private const double RateControlGain = 0.04;

    // キュー残量の上限(秒)。コアの実際の音声生産量が申告サンプルレートより多い等で
    // キューが溜まり続けると、音が数秒遅れて聞こえる。上限を超えたらキューを捨てて追いつく
    // (継続的なずれは下の再生速度補正で吸収し、これは最後の安全弁)。
    private const double MaxQueuedSeconds = 0.5;

    private int _maxQueuedBytes = int.MaxValue;
    private float _playbackRatio = 1.0f;

    // Open()時にコアの申告サンプルレートを保存しておく(早送り中のピッチシフト計算に使う)。
    private int _baseFreq;

    public AudioProcessor(Action<string> reportStatus)
    {
        _reportStatus = reportStatus;
    }

    /// <summary>現在オーディオストリームを開いているかどうか。</summary>
    public bool IsOpen => _stream != nint.Zero;

    /// <summary>
    /// コアの retro_get_system_av_info() で得られたサンプルレートに合わせてSDLオーディオ
    /// ストリームを開き、再生を開始する。失敗した場合はステータス通知のみ行い、以降の
    /// <see cref="SampleBatch"/> は無音のまま何もしない(呼び出し元の続行は妨げない)。
    /// </summary>
    public void Open(double sampleRateHint)
    {
        var audioSpec = new Sdl.SDL_AudioSpec
        {
            format = Sdl.SDL_AUDIO_S16,
            channels = 2,
            freq = sampleRateHint > 0 ? (int)sampleRateHint : 44100,
        };
        _stream = Sdl.SDL_OpenAudioDeviceStream(
            Sdl.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, ref audioSpec, nint.Zero, nint.Zero);

        if (_stream != nint.Zero)
        {
            // アプリ設定の「ゲーム音量」を反映する(ゲーム起動時に1回)。
            try
            {
                int volume = Math.Clamp(AppPreferencesStore.Load().GameVolume, 0, 100);
                if (volume < 100) Sdl.SDL_SetAudioStreamGain(_stream, volume / 100f);
            }
            catch
            {
                // 音量が設定できなくても、等倍のまま鳴らす。
            }

            Sdl.SDL_ResumeAudioStreamDevice(_stream);
        }
        else
            _reportStatus("警告: オーディオデバイスを開けませんでした: " + Sdl.SDL_GetErrorString());

        _lowWatermarkBytes = (int)(audioSpec.freq * 0.02) * 2 * sizeof(short);
        _maxQueuedBytes = (int)(audioSpec.freq * MaxQueuedSeconds) * 2 * sizeof(short);
        _baseFreq = audioSpec.freq;
        _playbackRatio = 1.0f;

        // 目標 = max(従来の40ms, デバイスバッファ1.5回分)。
        int oldTarget = _lowWatermarkBytes * 2;
        int deviceTarget = (int)(DeviceSampleFrames * 1.5) * 2 * sizeof(short);
        _targetQueuedBytes = Math.Max(oldTarget, deviceTarget);
    }

    /// <summary>エミュレーション終了時の後始末。</summary>
    public void Close()
    {
        if (_stream != nint.Zero)
            Sdl.SDL_DestroyAudioStream(_stream);
        _stream = nint.Zero;
        _queuedEmaBytes = -1;
        _appliedSpeedMultiplier = 1;
        _muted = false;
        _baseFreq = 0;
        _playbackRatio = 1.0f;
    }

    /// <summary>一時停止(メニュー表示)時に音声出力を止める。</summary>
    public void Pause()
    {
        if (_stream != nint.Zero)
            Sdl.SDL_PauseAudioStreamDevice(_stream);
    }

    /// <summary>
    /// 一時停止から再開する際、溜まっていたバッファを捨てて再生を再開する。
    /// 再開直後は残量EMAも作り直す(クリア直後の値をそのまま使うと誤ったレート調整になるため)。
    /// </summary>
    public void ResumeAfterPause()
    {
        if (_stream == nint.Zero) return;
        Sdl.SDL_ClearAudioStream(_stream);
        Sdl.SDL_ResumeAudioStreamDevice(_stream);
        _queuedEmaBytes = -1;
    }

    // 早送り中の音声の扱い。当初は「早送り中は常にミュート」というシンプルな実装
    // だったが、「2倍・3倍程度までは音声を聞きたい」という要望を受けて、
    // ピッチシフト再生(SDL_AudioStreamのリサンプリングを利用した倍速再生)へ変更した。
    //
    // 早送りはコアが単位時間あたりに生成する音声データ量を倍率倍に増やす
    // (ホスト側がretro_run()をより高頻度に呼ぶため)。そのデータを「実際より
    // 高いサンプルレートで録られたもの」だとSDLへ申告する(SDL_SetAudioStreamFormatの
    // srcspec.freq = 元のサンプルレート × 倍率)と、SDLは実際のデバイス側フォーマット
    // (dstspecにnint.Zeroを渡すことで変更しない=変わらず元のまま)へ自動的に
    // ダウンサンプリングして再生する。結果として、倍率倍のデータ量が1/倍率の
    // 再生時間に圧縮される(=キューが際限なく溜まることはない)と同時に、
    // 再生速度が上がりピッチも上がる(いわゆる「早送り再生」の音)。
    //
    // 倍率が上がりすぎるとピッチも上がりすぎて音として破綻するため、一定倍率
    // (MaxAudibleSpeedMultiplier)を超える場合はミュートするという安全弁を残してある。
    // 現状の早送り倍率は{1,2,3}の最大3倍までしか無い(EmulationHost.
    // FastForwardMultipliers参照。以前は5倍まであったが、「2倍・3倍までは音声を
    // 聞きたい」という要望に合わせて5倍自体を廃止した)ため、この閾値に実際に
    // 到達することは無いが、将来倍率のラインナップを増やした場合に備えて残してある。
    // internal: EmulationHost.cs側でステータス文言の分岐(「(音声ミュート)」表示の
    // 有無)にそのまま使うため、同一アセンブリ内から参照できるようにしてある。
    internal const int MaxAudibleSpeedMultiplier = 3;

    private bool _muted;
    private int _appliedSpeedMultiplier = 1;

    /// <summary>
    /// 早送りの倍率が切り替わるたびに呼ぶ。倍率が
    /// <see cref="MaxAudibleSpeedMultiplier"/> 以下ならピッチシフトした音声を鳴らし、
    /// それを超える場合はミュートする(SampleBatch側で音声データそのものを捨てる)。
    /// 実際に状態が変わる時だけ、ストリームの残留データをクリアし、レート制御の
    /// 移動平均も作り直す(ResumeAfterPauseと同じ考え方。切り替え直前/直後に
    /// 古いキュー状態を引きずって変な速度調整がかかるのを防ぐ)。
    /// </summary>
    public void SetSpeedMultiplier(int multiplier)
    {
        if (multiplier <= 0) multiplier = 1;

        bool shouldMute = multiplier > MaxAudibleSpeedMultiplier;

        if (multiplier == _appliedSpeedMultiplier && shouldMute == _muted)
            return; // 変化なし

        _appliedSpeedMultiplier = multiplier;
        _muted = shouldMute;

        if (_stream != nint.Zero)
        {
            Sdl.SDL_ClearAudioStream(_stream);
            SetPlaybackRatio(1.0f);

            if (!_muted && _baseFreq > 0)
            {
                // multiplier==1の時はbaseFreqそのものになり、通常フォーマットへの
                // 復帰も兼ねる。
                var srcSpec = new Sdl.SDL_AudioSpec
                {
                    format = Sdl.SDL_AUDIO_S16,
                    channels = 2,
                    freq = _baseFreq * multiplier,
                };
                Sdl.SDL_SetAudioStreamFormat(_stream, ref srcSpec, nint.Zero);
            }
        }

        _queuedEmaBytes = -1;
    }

    private long _framesReceived;
    private long _callsReceived;
    private long _maxFramesPerCall;

    /// <summary>SampleBatchが呼ばれた回数の累計(ペース診断用)。</summary>
    public long CallsReceived => System.Threading.Interlocked.Read(ref _callsReceived);

    /// <summary>1回の呼び出しで渡された最大フレーム数(ペース診断用。読んだら0に戻す)。</summary>
    public long TakeMaxFramesPerCall() => System.Threading.Interlocked.Exchange(ref _maxFramesPerCall, 0);

    /// <summary>コアから受け取った音声フレームの累計(ペース診断用)。</summary>
    public long FramesReceived => System.Threading.Interlocked.Read(ref _framesReceived);

    /// <summary>retro_audio_sample_batch_t本体。コアから毎フレーム(場合によっては複数回)呼ばれる。</summary>
    public nuint SampleBatch(nint data, nuint frames)
    {
        if (_stream == nint.Zero || frames == 0 || data == nint.Zero)
            return frames;

        // ペース診断用(コアが1秒間に何フレーム分の音声を出しているか)。早送り・ミュート中も数える。
        System.Threading.Interlocked.Add(ref _framesReceived, (long)frames);
        System.Threading.Interlocked.Increment(ref _callsReceived);
        if ((long)frames > System.Threading.Interlocked.Read(ref _maxFramesPerCall))
            System.Threading.Interlocked.Exchange(ref _maxFramesPerCall, (long)frames);

        // 早送り中はここで音声データを丸ごと捨てる(SDLストリームへは一切渡さない)。
        // コア側には「消費された」ものとして扱わせるため、戻り値は通常時と同じくframesを返す
        // (retro_audio_sample_batch_tの戻り値は消費したフレーム数を期待するコアがあるため)。
        if (_muted)
            return frames;

        int byteCount = checked((int)frames * 2 * sizeof(short));

        // 呼び出し頻度が高いため、毎回 new byte[] せずバッファを使い回す。
        // SDL_PutAudioStreamDataは呼び出し中に内部バッファへ同期的にデータをコピーするため、
        // 呼び出しが返った後にこのバッファを次回用に上書きしても問題ない。
        if (_buffer.Length < byteCount)
            _buffer = new byte[byteCount];

        // キューが上限(0.5秒相当)を超えて溜まっていたら捨てて追いつく。
        // 早送り中は、同じ再生時間に対してキューのバイト数が倍率倍になるため上限も倍率倍にする。
        long limit = (long)_maxQueuedBytes * _appliedSpeedMultiplier;
        if (Sdl.SDL_GetAudioStreamQueued(_stream) > limit)
        {
            Sdl.SDL_ClearAudioStream(_stream);
            _queuedEmaBytes = -1;
        }

        Marshal.Copy(data, _buffer, 0, byteCount);
        Sdl.SDL_PutAudioStreamData(_stream, _buffer, byteCount);

        // 注意: 以前はここでキューが薄い時に「前フレームの音声」を複製して補充していたが、
        // クロスフェードなしで別バッファを継ぎ足すと波形に不連続点ができ、それ自体が
        // プチプチ音(クラックリング)の原因になっていた。アンダーラン/オーバーランの
        // 抑制は下の動的レート制御(AdjustFrameTime)で行う。

        return frames;
    }

    /// <summary>
    /// 現在のオーディオストリームのキュー残量(EMAで平滑化)を見て、次フレームまでの
    /// 待ち時間をわずかに早めたり遅めたりする(±1%まで)。呼び出し元(RunLoop)は、
    /// 通常のフレーム待ち時間の代わりにこの戻り値を使う。
    /// </summary>
    public double AdjustFrameTime(double frameTimeMs)
    {
        if (_stream == nint.Zero) return frameTimeMs;

        // ミュート中(早送り中)はストリームへ何も流していないため、キュー残量を見た
        // レート制御には意味が無い(常に「枯渇気味」と誤検出して無駄に加速しようとする)。
        // 早送り中のフレームペースはRunLoop側で倍率を直接掛けて計算しているので、
        // ここでは何もせず素通しにする。
        if (_muted) return frameTimeMs;

        int queuedBytes = Sdl.SDL_GetAudioStreamQueued(_stream);
        _queuedEmaBytes = _queuedEmaBytes < 0
            ? queuedBytes
            : _queuedEmaBytes * (1.0 - RateControlEmaAlpha) + queuedBytes * RateControlEmaAlpha;

        double targetQueuedBytes = _targetQueuedBytes > 0 ? _targetQueuedBytes : _lowWatermarkBytes * 2.0;
        if (targetQueuedBytes <= 0) return frameTimeMs;

        // error < 0: 枯渇気味 → 待ち時間を短くしてRun()を早める(音声生産を加速)
        // error > 0: 溜まりすぎ → 待ち時間を長くしてRun()を遅らせる(音声生産を減速)
        double error = (_queuedEmaBytes - targetQueuedBytes) / targetQueuedBytes;
        // 音声キューが多い → 待ち時間を延ばしてコアの実行(=音声生産)を遅らせる。少ない → 縮める。
        // 再生側は等速のまま(SDL_SetAudioStreamFrequencyRatioでの補正は、ピッチが変わり、
        // キュー破棄時に音程が跳ねるため廃止した)。
        double adjust = Math.Clamp(error * RateControlGain, -RateControlMaxAdjust, RateControlMaxAdjust);

        return frameTimeMs * (1.0 + adjust);
    }

    private void SetPlaybackRatio(float ratio)
    {
        if (_stream == nint.Zero) return;
        if (Math.Abs(ratio - _playbackRatio) < 0.0005f) return;

        _playbackRatio = ratio;
        Sdl.SDL_SetAudioStreamFrequencyRatio(_stream, ratio);
    }
}