using System;
using System.IO;
using System.Linq;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace EmulatorLauncher.Services;

/// <summary>
/// メニュー画面のBGM。Assets/Sound/BGM/ 内のmp3をランダムな順で繰り返し再生する
/// (全曲を一巡したら続けて再生する)。ゲーム中は一時停止する(<see cref="Pause"/>/<see cref="Resume"/>)。
/// フォルダやファイルが無い場合は何もしない。UIスレッドから呼ぶこと。
/// </summary>
public static class BgmPlayer
{
    /// <summary>BGMの音量(0.0〜1.0)。アプリ設定の「メニューBGM」で変える(既定は中=0.5)。</summary>
    private static double Volume = 0.5;

    /// <summary>音量(0〜100)を設定する。再生中ならすぐ反映する。</summary>
    public static void SetVolume(int percent)
    {
        Volume = Math.Clamp(percent, 0, 100) / 100.0;
        try { if (_player is not null) _player.Volume = Volume; } catch { }
    }

    private static MediaPlayer? _player;
    private static bool _initialized;
    private static bool _paused;
    private static bool _enabled; // 既定は再生しない(起動時にアプリ設定の値で上書きされる)

    /// <summary>BGMを読み込んで再生を始める。2回目以降は何もしない。</summary>
    public static void Start()
    {
        if (_initialized || !_enabled) return;
        _initialized = true;

        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Sound", "BGM");
            if (!Directory.Exists(folder))
            {
                System.Diagnostics.Debug.WriteLine($"[Bgm] フォルダがありません: {folder}");
                return;
            }

            string[] files = Directory.GetFiles(folder, "*.mp3", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return;

            // 並びをランダムにしてからリストへ入れ、さらにシャッフル再生+繰り返しにする
            // (毎回違う曲から始まり、全曲を一巡しても続く)。
            var list = new MediaPlaybackList { AutoRepeatEnabled = true };
            foreach (string file in files.OrderBy(_ => Random.Shared.Next()))
            {
                list.Items.Add(new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(file))));
            }
            list.ShuffleEnabled = true;

            _player = new MediaPlayer
            {
                Volume = Volume,
                AutoPlay = false,
                Source = list,
            };
            _player.CommandManager.IsEnabled = false;

            if (!_paused && _enabled) _player.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Bgm] 開始に失敗しました: {ex.Message}");
        }
    }

    /// <summary>一時停止する(ゲーム中)。</summary>
    public static void Pause()
    {
        _paused = true;
        try { _player?.Pause(); } catch { }
    }

    /// <summary>一時停止を解除して続きから再生する(メニューへ戻ったとき)。</summary>
    public static void Resume()
    {
        _paused = false;
        if (!_enabled) return;
        Start(); // まだ読み込んでいなければ(起動時にBGMがオフだった場合)ここで読み込んで再生する
        try { _player?.Play(); } catch { }
    }

    /// <summary>
    /// BGMの再生/しないを切り替える(アプリ設定「メニューBGM再生」)。しない場合は止めて、
    /// 以後Resumeしても鳴らさない。設定画面から呼ばれるので、再生に切り替えたときはすぐ鳴らす。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled) Resume();
        else
        {
            try { _player?.Pause(); } catch { }
        }
    }
}
