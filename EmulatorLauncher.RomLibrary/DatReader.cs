using System.Globalization;
using EmulatorLauncher.Models;

namespace EmulatorLauncher.RomLibrary;

/// <summary>libretro-databaseの"dat"フォルダにあるclrmamepro形式のDATファイル(RDBが用意されていないコンソール用。例: "Nintendo - Wii U.dat")を読み込み、RDBと同じに詰める。</summary>
public static class DatReader
{
    public static Task<RdbDatabase> LoadDatabaseAsync(string datPath)
        => Task.Run(() => Load(datPath));

    private static RdbDatabase Load(string datPath)
    {
        var db = new RdbDatabase();
        try
        {
            RdbGameEntry? entry = null;
            string? serial = null;
            string? romName = null;
            int depth = 0;
            bool inGame = false;
            bool inRom = false;

            foreach (string rawLine in File.ReadLines(datPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.EndsWith("(", StringComparison.Ordinal) && !line.Contains('"'))
                {
                    string head = line[..^1].Trim();
                    depth++;
                    if (depth == 1 && head == "game")
                    {
                        inGame = true;
                        entry = new RdbGameEntry();
                        serial = null;
                        romName = null;
                    }
                    else if (depth == 2 && head == "rom" && inGame)
                    {
                        inRom = true;
                    }
                    continue;
                }

                // "rom ( name "..." serial "..." )" のように1行に収まっている場合は読み飛ばさず、先頭の"(" 以降をキー/値として扱う代わりに、ここでは対象外(libretro-databaseのDATは複数行形式)。
                if (line == ")")
                {
                    if (depth == 2) inRom = false;
                    if (depth == 1 && inGame && entry is not null)
                    {
                        Commit(db, entry, serial, romName);
                        inGame = false;
                        entry = null;
                    }
                    if (depth > 0) depth--;
                    continue;
                }

                if (!inGame || entry is null) continue;

                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string key = line[..space];
                string value = line[(space + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                if (inRom)
                {
                    if (key == "name") romName ??= value;
                    else if (key == "serial") serial ??= value;
                    continue;
                }

                switch (key)
                {
                    case "name": entry.Name = value.Trim(); break;
                    case "description": entry.Description = value; break;
                    case "genre": entry.Genre = value; break;
                    case "developer": entry.Developer = value.Trim(); break;
                    case "users": entry.Users = value; break;
                    case "serial": serial = value; break;
                    case "releaseyear":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
                        {
                            entry.ReleaseYear = year;
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // 読めなかった場合は、ここまでに読めた分だけを使う(スキャン自体は止めない)。
            System.Diagnostics.Debug.WriteLine($"[DatReader] DATの読み込みに失敗しました({datPath}): {ex.Message}");
        }

        return db;
    }

    private static void Commit(RdbDatabase db, RdbGameEntry entry, string? serial, string? romName)
    {
        // rom名が無い場合は、ゲーム名をそのままファイル名照合のキーにする。
        string key = !string.IsNullOrEmpty(romName) ? romName : (entry.Name ?? "");
        if (key.Length == 0) return;

        entry.RomName = key;
        if (!string.IsNullOrEmpty(serial)) entry.Serial = serial;
        db.AddRomName(key, entry);
        if (!string.IsNullOrEmpty(serial))
        {
            db.AddSerial(serial, entry);
        }
    }
}
