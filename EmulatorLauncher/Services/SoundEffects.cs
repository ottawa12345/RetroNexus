using System;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace EmulatorLauncher.Services;

/// <summary>
/// メニュー操作の効果音(Assets/Sound/*.mp3)を鳴らす。WinUIのMediaPlayer(mp3対応)を使い、
/// 連続して押されても音が途切れないよう、同じ音を複数のプレーヤーで順番に鳴らす。
/// ファイルが無い/再生に失敗した場合は何もしない(操作そのものは妨げない)。
/// UIスレッドから呼ぶこと。
/// </summary>
public static class SoundEffects
{
    private const int PoolSize = 4;

    /// <summary>false の間は効果音を鳴らさない(アプリ設定の「効果音」)。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>効果音の音量(0.0〜1.0)。アプリ設定の「効果音」で変える。</summary>
    public static double Volume { get; set; } = 1.0;

    private sealed class Effect
    {
        public MediaPlayer[] Players = Array.Empty<MediaPlayer>();
        public int Next;
    }

    private static readonly System.Collections.Generic.Dictionary<string, Effect?> Effects = new();

    /// <summary>効果音を鳴らす。name は Assets/Sound 内のファイル名(拡張子なし。例: "move")。</summary>
    public static void Play(string name)
    {
        if (!Enabled) return;

        try
        {
            if (!Effects.TryGetValue(name, out Effect? effect))
            {
                effect = Load(name);
                Effects[name] = effect;
            }

            if (effect is null) return;

            MediaPlayer player = effect.Players[effect.Next];
            effect.Next = (effect.Next + 1) % effect.Players.Length;
            player.Volume = Volume;
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SoundEffects] 再生に失敗しました({name}): {ex.Message}");
        }
    }

    private static Effect? Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sound", name + ".mp3");
        if (!File.Exists(path))
        {
            System.Diagnostics.Debug.WriteLine($"[SoundEffects] ファイルがありません: {path}");
            return null;
        }

        var players = new MediaPlayer[PoolSize];
        for (int i = 0; i < players.Length; i++)
        {
            var p = new MediaPlayer
            {
                RealTimePlayback = true,     // 低遅延
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.GameEffects,
                Source = MediaSource.CreateFromUri(new Uri(path)),
            };
            p.CommandManager.IsEnabled = false; // メディアキー/システムのメディア表示に出さない
            players[i] = p;
        }

        return new Effect { Players = players };
    }
}
