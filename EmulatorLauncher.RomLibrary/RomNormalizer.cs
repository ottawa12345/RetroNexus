using EmulatorLauncher.Services;

namespace EmulatorLauncher.RomLibrary;

/// <summary>カートリッジ系ROMのCRC-32を、RDB(libretro-database)が前提とする「ヘッダ無し/z64並び」に正規化してから計算する。</summary>
internal static class RomNormalizer
{
    private enum ByteOrder { None, Swap16, Reverse32 }

    private readonly record struct Plan(int Skip, ByteOrder Order);

    public static bool MayNeedNormalization(string consoleId) => consoleId.ToLowerInvariant() switch
    {
        "fc" or "nes" or "fds" or "sfc" or "snes" or "pce" or "sgx" or "tg16"
            or "lynx" or "a7800" or "7800" or "n64" => true,
        _ => false,
    };

    private static Plan? GetPlan(string consoleId, ReadOnlySpan<byte> head, long length)
    {
        switch (consoleId.ToLowerInvariant())
        {
            case "fc" or "nes":
                if (head.Length >= 16 && head[0] == 'N' && head[1] == 'E' && head[2] == 'S' && head[3] == 0x1A)
                    return new Plan(16, ByteOrder.None);
                return null;

            case "fds":
                if (head.Length >= 16 && head[0] == 'F' && head[1] == 'D' && head[2] == 'S' && head[3] == 0x1A)
                    return new Plan(16, ByteOrder.None);
                return null;

            case "sfc" or "snes":
                return length % 1024 == 512 ? new Plan(512, ByteOrder.None) : null;

            case "pce" or "sgx" or "tg16":
                return length % 8192 == 512 ? new Plan(512, ByteOrder.None) : null;

            case "lynx":
                if (head.Length >= 64 && head[0] == 'L' && head[1] == 'Y' && head[2] == 'N' && head[3] == 'X')
                    return new Plan(64, ByteOrder.None);
                return null;

            case "a7800" or "7800":
                if (head.Length >= 128 && head[0] <= 0x03
                    && System.Text.Encoding.ASCII.GetString(head.Slice(1, 9)) == "ATARI7800")
                    return new Plan(128, ByteOrder.None);
                return null;

            case "n64":
                if (head.Length < 4) return null;
                // z64: 80 37 12 40(そのまま) / v64: 37 80 40 12 / n64: 40 12 37 80
                if (head[0] == 0x37 && head[1] == 0x80) return new Plan(0, ByteOrder.Swap16);
                if (head[0] == 0x40 && head[1] == 0x12) return new Plan(0, ByteOrder.Reverse32);
                return null;

            default:
                return null;
        }
    }

    /// <summary>ストリームの内容をconsoleIdに応じて正規化したCRC-32を返す(1回の読み込みで済む)。</summary>
    public static uint? TryComputeCrc(Stream stream, string consoleId, long length)
    {
        if (!MayNeedNormalization(consoleId)) return null;

        try
        {
            byte[] buffer = new byte[81920]; // 4の倍数(N64の並べ替えが境界をまたがないようにする)
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read < 4) return null;

            if (GetPlan(consoleId, buffer.AsSpan(0, read), length) is not Plan plan) return null;

            uint state = 0xFFFFFFFFu;
            int offset = plan.Skip;
            while (read > 0)
            {
                if (offset > read) return null;
                Span<byte> chunk = buffer.AsSpan(offset, read - offset);
                Reorder(chunk, plan.Order);
                state = Crc32.Update(state, chunk);
                offset = 0;
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }

            return state ^ 0xFFFFFFFFu;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RomNormalizer] 正規化CRCの計算に失敗しました: {ex.Message}");
            return null;
        }
    }

    private static void Reorder(Span<byte> data, ByteOrder order)
    {
        switch (order)
        {
            case ByteOrder.Swap16:
                for (int i = 0; i + 1 < data.Length; i += 2)
                    (data[i], data[i + 1]) = (data[i + 1], data[i]);
                break;
            case ByteOrder.Reverse32:
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    (data[i], data[i + 3]) = (data[i + 3], data[i]);
                    (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
                }
                break;
        }
    }
}
