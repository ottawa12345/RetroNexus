using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>設定画面「ランチャー管理」(Views.SettingsPage)でLauncherInfo1件を一覧表示するための表示用ラッパー。</summary>
public class LauncherListItem : INotifyPropertyChanged
{
    /// <summary>元になったLauncherInfo。</summary>
    public LauncherInfo Launcher { get; set; } = new();

    /// <summary>一覧の1行目に表示するランチャー名。</summary>
    public string DisplayName => Launcher.Name;

    /// <summary>コンソールの表示名(Consoles.jsonのname。無ければId)。</summary>
    public string ConsoleDisplayName { get; set; } = string.Empty;

    /// <summary>「libretroコア: xxx.dll」/「外部コマンド: C:\...\Cemu.exe」のような、起動方式の説明。</summary>
    public string DetailText => Launcher.IsExternal
        ? $"外部コマンド: {Launcher.ExePath}"
        : $"libretroコア: {Launcher.CorePath}";

    /// <summary>そのコンソールで現在使用中のランチャー(Consoles.jsonのlauncherName)か。</summary>
    public bool IsInUse { get; set; }

    /// <summary>行の右端に表示する状態の文言(Switchの設定画面の「ON/OFF」と同じ位置)。</summary>
    public string InUseText => IsInUse ? "使用中" : "未使用";

    /// <summary>一覧先頭の「＋ 新規追加」カードかどうか。</summary>
    public bool IsAddNewCard { get; set; }

    private bool _isFocused;

    /// <summary>ゲームパッドで現在フォーカスしている行か(SettingsPage.RefreshRowFocusFlagsが設定)。</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}