using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EmulatorLauncher.Emulation;

/// <summary>コアが申告する入力情報(SET_INPUT_DESCRIPTORS / SET_CONTROLLER_INFO)をDebug出力へ書く診断用。</summary>
internal static class InputDiagnostics
{
    private static readonly string[] DeviceNames = { "NONE", "JOYPAD", "MOUSE", "KEYBOARD", "LIGHTGUN", "ANALOG", "POINTER" };

    private static readonly string[] JoypadIds =
    {
        "B", "Y", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT",
        "A", "X", "L", "R", "L2", "R2", "L3", "R3",
    };

    private static string DeviceName(uint device)
    {
        uint baseDevice = device & 0xFF;
        string name = baseDevice < DeviceNames.Length ? DeviceNames[baseDevice] : baseDevice.ToString();
        return device > 0xFF ? $"{name}(subclass {device >> 8}, raw {device})" : name;
    }

    private static string IdName(uint device, uint index, uint id)
    {
        uint baseDevice = device & 0xFF;
        if (baseDevice == 1) return id < JoypadIds.Length ? JoypadIds[id] : id.ToString();
        if (baseDevice == 5) return $"index{index}/{(id == 0 ? "X" : id == 1 ? "Y" : id.ToString())}";
        return $"index{index}/id{id}";
    }

    private static string? ReadString(nint ptr) => ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    /// <summary>struct retro_input_descriptor { unsigned port, device, index, id; const char* description; } の配列(description==NULLで終端)。</summary>
    public static void LogInputDescriptors(nint data)
    {
        try
        {
            if (data == nint.Zero) return;
            var sb = new StringBuilder("[InputDesc] SET_INPUT_DESCRIPTORS");
            int stride = nint.Size == 8 ? 24 : 20;
            for (int i = 0; i < 512; i++)
            {
                nint p = data + i * stride;
                nint descPtr = Marshal.ReadIntPtr(p, 16);
                if (descPtr == nint.Zero) break;
                uint port = (uint)Marshal.ReadInt32(p, 0);
                uint device = (uint)Marshal.ReadInt32(p, 4);
                uint index = (uint)Marshal.ReadInt32(p, 8);
                uint id = (uint)Marshal.ReadInt32(p, 12);
                sb.AppendLine().Append($"  port{port} {DeviceName(device)} {IdName(device, index, id)} = \"{ReadString(descPtr)}\"");
            }
            Debug.WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputDesc] 読み取り失敗: {ex.Message}");
        }
    }

    /// <summary>struct retro_controller_info { const retro_controller_description* types; unsigned num_types; } の配列(types==NULLで終端。1要素=1ポート)。</summary>
    public static void LogControllerInfo(nint data)
    {
        try
        {
            if (data == nint.Zero) return;
            var sb = new StringBuilder("[InputDesc] SET_CONTROLLER_INFO");
            int infoStride = nint.Size == 8 ? 16 : 8;
            int descStride = nint.Size == 8 ? 16 : 8;
            for (int port = 0; port < 16; port++)
            {
                nint info = data + port * infoStride;
                nint types = Marshal.ReadIntPtr(info, 0);
                if (types == nint.Zero) break;
                uint num = (uint)Marshal.ReadInt32(info, nint.Size);
                sb.AppendLine().Append($"  port{port} ({num}種類)");
                for (int i = 0; i < num && i < 64; i++)
                {
                    nint d = types + i * descStride;
                    string? desc = ReadString(Marshal.ReadIntPtr(d, 0));
                    uint id = (uint)Marshal.ReadInt32(d, nint.Size);
                    sb.AppendLine().Append($"    device={DeviceName(id)} \"{desc}\"");
                }
            }
            Debug.WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputDesc] コントローラ情報の読み取り失敗: {ex.Message}");
        }
    }
}
