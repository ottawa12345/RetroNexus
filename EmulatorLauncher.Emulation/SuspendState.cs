using System;
using System.IO;

namespace EmulatorLauncher.Emulation;

/// <summary>「タイトル選択にもどる」で終了するときに保存する、再開用ステート(suspend枠)の置き場所。</summary>
public static class SuspendState
{
    public const string FolderName = "suspend";

    public static string GetFolder(string corePath, string romPath)
    {
        string coreFolder = Sanitize(Path.GetFileNameWithoutExtension(corePath));
        string gameFolder = Sanitize(string.IsNullOrEmpty(romPath) ? "_bios" : Path.GetFileNameWithoutExtension(romPath));
        return Path.Combine(AppContext.BaseDirectory, "statesave", coreFolder, gameFolder, FolderName);
    }

    /// <summary>同じゲームの別のステート(手動スロット番号・"auto")のフォルダ。</summary>
    public static string GetStateFolder(string corePath, string romPath, string key)
        => Path.Combine(Path.GetDirectoryName(GetFolder(corePath, romPath)) ?? string.Empty, Sanitize(key));

    /// <summary>再開用のステートが保存されているか。</summary>
    public static bool Exists(string corePath, string romPath)
    {
        // 設定で再開機能が無効なら、保存済みのステートがあっても無いものとして扱う。
        if (!AppPreferencesStore.Load().ResumeEnabled) return false;
        try { return File.Exists(Path.Combine(GetFolder(corePath, romPath), "state.dat")); }
        catch { return false; }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
