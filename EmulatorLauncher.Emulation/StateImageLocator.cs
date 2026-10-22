using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmulatorLauncher.Emulation;

/// <summary>ゲーム詳細画面に並べる「ステートセーブの画像」を探す。</summary>
public static class StateImageLocator
{
    public sealed class Entry
    {
        /// <summary>フォルダ名("suspend" / "auto" / スロット番号)。</summary>
        public required string Key { get; init; }

        public required string ImagePath { get; init; }
        public required DateTime SavedAt { get; init; }

        /// <summary>保存時の画面比率(幅/高さ)。</summary>
        public double? Aspect { get; init; }

        /// <summary>画像に添える短い呼び名(「中断」「オート」「スロット2」)。</summary>
        public required string Label { get; init; }
    }

    private sealed class Meta { public Dictionary<string, MetaSlot> Slots { get; set; } = new(); }
    private sealed class MetaSlot { public string SavedAt { get; set; } = ""; public double Aspect { get; set; } }

    public static IReadOnlyList<Entry> GetRecent(string corePath, string romPath, int max)
    {
        var result = new List<Entry>();
        try
        {
            // suspendフォルダの1階層上がゲームのセーブフォルダ。
            string gameFolder = Path.GetDirectoryName(SuspendState.GetFolder(corePath, romPath)) ?? string.Empty;
            if (gameFolder.Length == 0 || !Directory.Exists(gameFolder)) return result;

            var meta = LoadMeta(Path.Combine(gameFolder, "saves.json"));

            Entry? suspend = SuspendState.Exists(corePath, romPath) ? Make(gameFolder, SuspendState.FolderName, "中断", meta) : null;

            var others = new List<Entry>();
            foreach (string dir in Directory.EnumerateDirectories(gameFolder))
            {
                string name = Path.GetFileName(dir);
                string? label = name == "auto" ? "オート"
                    : int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? $"スロット{n}"
                    : null;
                if (label is null) continue; // suspend / restart などは別扱い
                var e = Make(gameFolder, name, label, meta);
                if (e is not null) others.Add(e);
            }

            if (suspend is not null) result.Add(suspend);
            result.AddRange(others.OrderByDescending(e => e.SavedAt));
        }
        catch
        {
            // 読めなければ「画像なし」として扱う(詳細画面の表示は妨げない)
        }
        return result.Take(Math.Max(0, max)).ToList();
    }

    private static Entry? Make(string gameFolder, string key, string label, Meta meta)
    {
        string folder = Path.Combine(gameFolder, key);
        string image = Path.Combine(folder, "screenshot.bmp");
        if (!File.Exists(Path.Combine(folder, "state.dat")) || !File.Exists(image)) return null;

        DateTime savedAt = meta.Slots.TryGetValue(key, out var m)
            && DateTime.TryParse(m.SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : File.GetLastWriteTime(image);
        double? aspect = m is { Aspect: > 0 } ? m.Aspect : null;
        return new Entry { Key = key, ImagePath = image, SavedAt = savedAt, Label = label, Aspect = aspect };
    }

    private static Meta LoadMeta(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(path)) ?? new Meta() : new Meta(); }
        catch { return new Meta(); }
    }
}
