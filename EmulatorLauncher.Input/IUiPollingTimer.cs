using System;

namespace EmulatorLauncher.Input;

/// <summary>UIスレッド上で一定間隔ごとにコールバックを呼び出すタイマーの抽象化。</summary>
public interface IUiPollingTimer
{
    /// <summary>ポーリング間隔とコールバックを設定する(まだ開始はしない)。</summary>
    void Configure(TimeSpan interval, Action onTick);

    /// <summary>ポーリングを開始する。</summary>
    void Start();

    /// <summary>ポーリングを停止する。</summary>
    void Stop();
}
