using System.Text;

namespace EmulatorLauncher.RomLibrary;

/// <summary>PS3のPARAM.SFOから読み取った情報。</summary>
public sealed record ParamSfo(string Title, string TitleId);

/// <summary>PARAM.SFO(PS3/PSP等の共通形式)の読み取り。</summary>
public static class ParamSfoReader
{
    /// <summary>読めなければnull。</summary>
    public static ParamSfo? TryRead(string path)
    {
        try
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 20 || d[0] != 0 || d[1] != (byte)'P' || d[2] != (byte)'S' || d[3] != (byte)'F')
            {
                return null;
            }

            uint keyStart = BitConverter.ToUInt32(d, 8);
            uint dataStart = BitConverter.ToUInt32(d, 12);
            uint count = BitConverter.ToUInt32(d, 16);

            string title = string.Empty, titleId = string.Empty;
            for (int i = 0; i < count; i++)
            {
                int entry = 20 + i * 16;
                if (entry + 16 > d.Length) break;

                ushort keyOffset = BitConverter.ToUInt16(d, entry);
                ushort format = BitConverter.ToUInt16(d, entry + 2);
                uint length = BitConverter.ToUInt32(d, entry + 4);
                uint dataOffset = BitConverter.ToUInt32(d, entry + 12);

                // 0x0004=UTF-8(終端なし)、0x0204=UTF-8(終端あり)。
                if (format != 0x0004 && format != 0x0204) continue;

                string key = ReadZeroTerminated(d, (int)keyStart + keyOffset);
                if (key != "TITLE" && key != "TITLE_ID") continue;

                int valueStart = (int)(dataStart + dataOffset);
                if (valueStart < 0 || valueStart >= d.Length) continue;
                int valueLength = (int)Math.Min(length, (uint)(d.Length - valueStart));
                string value = Encoding.UTF8.GetString(d, valueStart, valueLength).TrimEnd('\0');

                if (key == "TITLE") title = value.Replace("\r", " ").Replace("\n", " ").Trim();
                else titleId = value.Trim();
            }

            return new ParamSfo(title, titleId);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadZeroTerminated(byte[] d, int start)
    {
        if (start < 0 || start >= d.Length) return string.Empty;
        int end = start;
        while (end < d.Length && d[end] != 0) end++;
        return Encoding.ASCII.GetString(d, start, end - start);
    }
}
