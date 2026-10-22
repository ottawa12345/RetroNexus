using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace EmulatorLauncher.Input;

/// <summary>
/// 接続されたゲームコントローラーを「1P/2P」の2枠へ割り当てて管理する、アプリ全体で唯一の窓口。
///
/// 割り当てのルール:
///   ・見つけた順に、空いている一番小さい番号(1P→2P)へ割り当てる。
///   ・3台目以降は無視する(枠が空くまで開かない)。
///   ・切断されたら、その番号の枠だけを空ける。他の枠は動かさない(2Pが1Pへ詰められることは無い)。
///   ・空いた枠には、その後「新しく検出された(接続イベントが来た)」コントローラーが入る。
///     すでに接続されていて無視されていた3台目は、枠が空いても自動では入らない(つなぎ直すと入る)。
///   ・ゲーム以外の操作(メニュー等)は、1P・2Pのどちらのコントローラーでも行える。
///
/// メニュー(<see cref="SdlGamepadService"/>)とゲーム(EmulationHost)は、SDLイベントポンプの所有者が
/// 交代する(同時にポンプしない)が、どちらも接続/切断イベントをここへ渡すだけにして、
/// 割り当て状態はここで保持し続ける。ハンドルはアプリ終了まで開いたままにする。
/// </summary>
public static class GamepadSlots
{
    /// <summary>同時に扱うコントローラーの最大数。</summary>
    public const int MaxSlots = 2;

    private const int ActivityStickThreshold = 12000;

    private static readonly object Gate = new();
    private static readonly nint[] Handles = new nint[MaxSlots];
    private static readonly uint[] InstanceIds = new uint[MaxSlots];
    private static bool _initialEnumerationDone;

    private static readonly GamepadButton[] AllButtons = Enum.GetValues<GamepadButton>();

    /// <summary>指定の枠(0=1P、1=2P)のSDLハンドル。未接続なら nint.Zero。</summary>
    public static nint GetHandle(int slot)
        => (uint)slot < MaxSlots ? Volatile.Read(ref Handles[slot]) : nint.Zero;

    public static bool IsConnected(int slot) => GetHandle(slot) != nint.Zero;

    /// <summary>指定の枠のコントローラー名(SDLが返す製品名)。未接続ならnull。</summary>
    public static string? GetName(int slot)
    {
        nint handle = GetHandle(slot);
        if (handle == nint.Zero) return null;
        try { return Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadName(handle)); }
        catch { return null; }
    }

    /// <summary>接続中(割り当て済み)のコントローラー数(0〜2)。</summary>
    public static int ConnectedCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (IsConnected(i)) count++;
            }

            return count;
        }
    }

    /// <summary>
    /// 接続/切断イベントを処理する。割り当てが変わった場合は、状況を表す文言を返す(変わらなければnull)。
    /// SDL_EVENT_GAMEPAD_ADDED/REMOVED 以外のイベントは無視する。
    /// </summary>
    public static string? HandleDeviceEvent(Sdl.SDL_Event ev)
    {
        if (ev.type == Sdl.SDL_EVENT_GAMEPAD_ADDED)
        {
            lock (Gate)
            {
                return TryAssign(ev.which);
            }
        }

        if (ev.type == Sdl.SDL_EVENT_GAMEPAD_REMOVED)
        {
            lock (Gate)
            {
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (Handles[i] != nint.Zero && InstanceIds[i] == ev.which)
                    {
                        Release(i);
                        return $"{i + 1}Pのコントローラが切断されました";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 起動後に一度だけ、すでに接続されているコントローラーを列挙して空き枠へ割り当てる
    /// (起動直後の接続イベントを取りこぼした場合の保険)。2回目以降の呼び出しは何もしない。
    /// </summary>
    public static List<string> EnsureInitialEnumeration()
    {
        var messages = new List<string>();

        lock (Gate)
        {
            if (_initialEnumerationDone) return messages;
            _initialEnumerationDone = true;

            foreach (uint id in EnumerateInstanceIds())
            {
                string? message = TryAssign(id);
                if (message is not null) messages.Add(message);
            }
        }

        return messages;
    }

    /// <summary>
    /// 割り当て済みのコントローラーが、実際にはもう接続されていない(切断イベントを取りこぼした)場合に、
    /// その枠を空ける。新しいコントローラーは割り当てない(割り当ては接続イベントだけで行う)。
    /// </summary>
    public static List<string> Reconcile()
    {
        var messages = new List<string>();

        lock (Gate)
        {
            var present = new HashSet<uint>(EnumerateInstanceIds());
            for (int i = 0; i < MaxSlots; i++)
            {
                if (Handles[i] != nint.Zero && !present.Contains(InstanceIds[i]))
                {
                    Release(i);
                    messages.Add($"{i + 1}Pのコントローラが切断されました");
                }
            }
        }

        return messages;
    }

    /// <summary>指定の枠のコントローラー名(例: "PS5 DualSense")。未接続ならnull。</summary>
    public static string? DescribeSlot(int slot)
    {
        nint handle = GetHandle(slot);
        return handle == nint.Zero ? null : DescribeHandle(handle);
    }

    /// <summary>コントローラー情報の表示用(設定画面の「コントローラー情報」)。</summary>
    public sealed class GamepadInfo
    {
        public GamepadInfo(int slot, string name, string typeName, ushort vendorId, ushort productId, string? serial)
        {
            Slot = slot; Name = name; TypeName = typeName; VendorId = vendorId; ProductId = productId; Serial = serial;
        }

        /// <summary>0=1P、1=2P。</summary>
        public int Slot { get; }
        public string Name { get; }
        public string TypeName { get; }
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public string? Serial { get; }
    }

    /// <summary>指定の枠のコントローラー情報(未接続ならnull)。</summary>
    public static GamepadInfo? GetInfo(int slot)
    {
        nint handle = GetHandle(slot);
        if (handle == nint.Zero) return null;

        string name = Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadName(handle)) ?? "不明なコントローラ";
        string? serial = null;
        try
        {
            serial = Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadSerial(handle));
        }
        catch
        {
            // シリアルを取れない環境では表示しないだけ。
        }

        string typeName = Sdl.SDL_GetGamepadType(handle) switch
        {
            1 => "標準",
            2 => "Xbox 360",
            3 => "Xbox One / Series",
            4 => "PS3",
            5 => "PS4",
            6 => "PS5",
            7 => "Switch Pro",
            8 or 9 or 10 => "Joy-Con",
            _ => "不明",
        };

        return new GamepadInfo(slot, name, typeName, Sdl.SDL_GetGamepadVendor(handle), Sdl.SDL_GetGamepadProduct(handle),
            string.IsNullOrEmpty(serial) ? null : serial);
    }

    /// <summary>
    /// 1Pと2Pの割り当てを入れ替える(設定画面の「コントローラーの入れ替え」)。
    /// 1台だけ接続している場合は、その1台が1P↔2Pへ移る。何も接続されていなければ何もせずfalse。
    /// 再起動すると、割り当ては接続順に戻る。
    /// </summary>
    public static bool SwapSlots()
    {
        lock (Gate)
        {
            if (Handles[0] == nint.Zero && Handles[1] == nint.Zero) return false;

            nint handle0 = Handles[0];
            nint handle1 = Handles[1];
            uint id0 = InstanceIds[0];
            uint id1 = InstanceIds[1];

            InstanceIds[0] = id1;
            InstanceIds[1] = id0;
            Volatile.Write(ref Handles[0], handle1);
            Volatile.Write(ref Handles[1], handle0);

            ApplyLedColor(0);
            ApplyLedColor(1);
            return true;
        }
    }

    /// <summary>
    /// 指定の枠のコントローラーに、今この瞬間なんらかの入力(ボタン、スティック、トリガー)があるか。
    /// 「どれが1Pでどれが2Pか」を見分ける表示の点灯に使う。読み取るだけでポンプはしない。
    /// </summary>
    public static bool IsActive(int slot)
    {
        nint handle = GetHandle(slot);
        if (handle == nint.Zero) return false;

        foreach (GamepadButton button in AllButtons)
        {
            if (GamepadPhysicalButtonReader.IsPressed(handle, button)) return true;
        }

        return Math.Abs((int)Sdl.SDL_GetGamepadAxis(handle, Sdl.SDL_GAMEPAD_AXIS_LEFTX)) > ActivityStickThreshold
               || Math.Abs((int)Sdl.SDL_GetGamepadAxis(handle, Sdl.SDL_GAMEPAD_AXIS_LEFTY)) > ActivityStickThreshold
               || Math.Abs((int)Sdl.SDL_GetGamepadAxis(handle, Sdl.SDL_GAMEPAD_AXIS_RIGHTX)) > ActivityStickThreshold
               || Math.Abs((int)Sdl.SDL_GetGamepadAxis(handle, Sdl.SDL_GAMEPAD_AXIS_RIGHTY)) > ActivityStickThreshold;
    }

    // ---- 内部(Gateを取った状態で呼ぶ) ----

    private static string? TryAssign(uint instanceId)
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            if (Handles[i] != nint.Zero && InstanceIds[i] == instanceId) return null; // 割り当て済み
        }

        int free = -1;
        for (int i = 0; i < MaxSlots; i++)
        {
            if (Handles[i] == nint.Zero)
            {
                free = i;
                break;
            }
        }

        if (free < 0) return null; // 枠が埋まっている(3台目以降は無視)

        nint handle = Sdl.SDL_OpenGamepad(instanceId);
        if (handle == nint.Zero) return null;

        InstanceIds[free] = instanceId;
        Volatile.Write(ref Handles[free], handle);

        ApplyLedColor(free);

        return $"コントローラ接続: {DescribeHandle(handle)} ({free + 1}P)";
    }

    /// <summary>DualSenseは、どれが何Pか見分けやすいようにライトバーの色を変える(1P=青、2P=赤。失敗しても無視)。</summary>
    private static void ApplyLedColor(int slot)
    {
        nint handle = Handles[slot];
        if (handle == nint.Zero) return;

        if (Sdl.SDL_GetGamepadType(handle) == Sdl.SDL_GAMEPAD_TYPE_PS5)
        {
            if (slot == 0) Sdl.SDL_SetGamepadLED(handle, 0, 96, 255);
            else Sdl.SDL_SetGamepadLED(handle, 255, 48, 48);
        }
    }

    private static void Release(int slot)
    {
        nint handle = Handles[slot];
        Volatile.Write(ref Handles[slot], nint.Zero);
        InstanceIds[slot] = 0;

        if (handle != nint.Zero)
        {
            Sdl.SDL_CloseGamepad(handle);
        }
    }

    private static List<uint> EnumerateInstanceIds()
    {
        var ids = new List<uint>();

        nint ptr = Sdl.SDL_GetGamepads(out int count);
        if (ptr == nint.Zero) return ids;

        try
        {
            for (int i = 0; i < count; i++)
            {
                ids.Add(unchecked((uint)Marshal.ReadInt32(ptr, i * sizeof(uint))));
            }
        }
        finally
        {
            Sdl.SDL_free(ptr);
        }

        return ids;
    }

    private static string DescribeHandle(nint handle)
    {
        string name = Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadName(handle)) ?? "不明なコントローラ";
        return Sdl.SDL_GetGamepadType(handle) switch
        {
            Sdl.SDL_GAMEPAD_TYPE_PS5 => "PS5 DualSense",
            Sdl.SDL_GAMEPAD_TYPE_PS4 => "PS4 DualShock",
            _ => name,
        };
    }
}
