using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EmulatorLauncher.Services;

/// <summary>
/// 本体(Windows)の音量・画面の明るさ・バッテリー残量を読み書きする。「システム設定」パネル用。
/// - 音量: Core Audio(IAudioEndpointVolume)。既定の再生デバイスの音量。
/// - 明るさ: WMI(WmiMonitorBrightness)。内蔵ディスプレイ(ノート/ハンドヘルド)だけ対応。
///   外付けモニターなど変えられない環境では「未対応」(null)を返す。
/// - バッテリー: GetSystemPowerStatus。
/// どれも失敗しても例外を外へ出さない(未対応として扱う)。
/// </summary>
public static class SystemControls
{
    // ------------------------------------------------------------------
    // 音量(Core Audio)
    // ------------------------------------------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static IAudioEndpointVolume? OpenEndpointVolume()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out IMMDevice device) != 0) return null;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out object iface) != 0) return null;
            return iface as IAudioEndpointVolume;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本体の音量(0〜100)。取得できなければnull。ミュート中は0として返す。</summary>
    public static int? GetVolume()
    {
        IAudioEndpointVolume? volume = OpenEndpointVolume();
        if (volume is null) return null;
        try
        {
            if (volume.GetMasterVolumeLevelScalar(out float level) != 0) return null;
            if (volume.GetMute(out bool muted) == 0 && muted) return 0;
            return (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
        }
        catch { return null; }
    }

    /// <summary>本体の音量を設定する(0〜100)。0より大きくするときはミュートも解除する。</summary>
    public static bool SetVolume(int percent)
    {
        IAudioEndpointVolume? volume = OpenEndpointVolume();
        if (volume is null) return false;
        try
        {
            Guid context = Guid.Empty;
            percent = Math.Clamp(percent, 0, 100);
            if (percent > 0) volume.SetMute(false, ref context);
            return volume.SetMasterVolumeLevelScalar(percent / 100f, ref context) == 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // 明るさ(WMI)
    // ------------------------------------------------------------------

    /// <summary>画面の明るさ(0〜100)。変えられない環境(外付けモニターのみ等)ではnull。</summary>
    public static Task<int?> GetBrightnessAsync() => Task.Run<int?>(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"),
                new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (ManagementBaseObject o in searcher.Get())
            {
                return Convert.ToInt32(o["CurrentBrightness"]);
            }
        }
        catch { /* 未対応 */ }
        return null;
    });

    private static int _pendingBrightness = -1;
    private static int _brightnessBusy;

    /// <summary>画面の明るさを設定する(0〜100)。連続して呼ばれても、最後の値だけを順に反映する
    /// (WMIの呼び出しは数十〜数百ミリ秒かかるため、呼び出し側を待たせない)。</summary>
    public static void SetBrightness(int percent)
    {
        Volatile.Write(ref _pendingBrightness, Math.Clamp(percent, 0, 100));
        if (Interlocked.Exchange(ref _brightnessBusy, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int value = Interlocked.Exchange(ref _pendingBrightness, -1);
                    if (value < 0) break;
                    try
                    {
                        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"),
                            new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
                        foreach (ManagementObject o in searcher.Get())
                        {
                            o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)value });
                        }
                    }
                    catch { /* 反映できなくても続行 */ }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _brightnessBusy, 0);
                // 解除の直前に新しい値が入っていた場合は、もう一度回す。
                if (Volatile.Read(ref _pendingBrightness) >= 0) SetBrightness(Volatile.Read(ref _pendingBrightness));
            }
        });
    }

    // ------------------------------------------------------------------
    // Wi-Fi / Bluetooth(Windowsの無線オン/オフ)
    // ------------------------------------------------------------------

    public enum RadioType { WiFi, Bluetooth }

    private static async Task<Windows.Devices.Radios.Radio?> FindRadioAsync(RadioType type)
    {
        var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
        if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed) return null;
        var kind = type == RadioType.WiFi ? Windows.Devices.Radios.RadioKind.WiFi : Windows.Devices.Radios.RadioKind.Bluetooth;
        var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
        return radios.FirstOrDefault(r => r.Kind == kind);
    }

    /// <summary>無線がオンかどうか。その無線が無い/取得できない場合はnull。</summary>
    public static async Task<bool?> GetRadioOnAsync(RadioType type)
    {
        try
        {
            var radio = await FindRadioAsync(type);
            return radio is null ? null : radio.State == Windows.Devices.Radios.RadioState.On;
        }
        catch { return null; }
    }

    /// <summary>無線をオン/オフする。成功したらtrue。</summary>
    public static async Task<bool> SetRadioAsync(RadioType type, bool on)
    {
        try
        {
            var radio = await FindRadioAsync(type);
            if (radio is null) return false;
            var result = await radio.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
            return result == Windows.Devices.Radios.RadioAccessStatus.Allowed;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // 電源モード(Windowsの「電源モード」: 省電力 / バランス / 高パフォーマンス)
    // ------------------------------------------------------------------

    /// <summary>省電力(「最も高い電力効率」)。</summary>
    public static readonly Guid PowerModeSaver = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    /// <summary>バランス。</summary>
    public static readonly Guid PowerModeBalanced = Guid.Empty;
    /// <summary>高パフォーマンス(「最も高いパフォーマンス」)。</summary>
    public static readonly Guid PowerModePerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActualOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    /// <summary>今の電源モード。取得できない環境(未対応のWindows)ではnull。</summary>
    public static Guid? GetPowerMode()
    {
        try { return PowerGetActualOverlayScheme(out Guid g) == 0 ? g : null; }
        catch { return null; }
    }

    public static bool SetPowerMode(Guid mode)
    {
        try { return PowerSetActiveOverlayScheme(mode) == 0; }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // スリープ / 再起動 / シャットダウン
    // ------------------------------------------------------------------

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static void Sleep()
    {
        try { SetSuspendState(false, false, false); } catch { }
    }

    public static void Restart() => RunShutdown("/r /t 0");

    public static void Shutdown() => RunShutdown("/s /t 0");

    private static void RunShutdown(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // バッテリー
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>バッテリーの状態。バッテリーが無い/取得できない場合はnull。</summary>
    // ------------------------------------------------------------------
    // CPU使用率・メモリ使用率
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeRaw { public uint Low; public uint High; public ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTimeRaw idle, out FileTimeRaw kernel, out FileTimeRaw user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length; public uint MemoryLoad;
        public ulong TotalPhys; public ulong AvailPhys;
        public ulong TotalPageFile; public ulong AvailPageFile;
        public ulong TotalVirtual; public ulong AvailVirtual; public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static ulong _lastIdle, _lastTotal;

    /// <summary>前回呼び出しからのCPU使用率(0〜100)。初回は0を返す。失敗時はnull。</summary>
    public static int? GetCpuUsage()
    {
        try
        {
            if (!GetSystemTimes(out FileTimeRaw idle, out FileTimeRaw kernel, out FileTimeRaw user)) return null;
            ulong i = idle.Value, total = kernel.Value + user.Value; // kernelにidleを含む
            ulong di = i - _lastIdle, dt = total - _lastTotal;
            bool first = _lastTotal == 0;
            _lastIdle = i; _lastTotal = total;
            if (first || dt == 0) return 0;
            return (int)Math.Clamp(Math.Round((1.0 - (double)di / dt) * 100.0), 0, 100);
        }
        catch { return null; }
    }

    /// <summary>メモリ使用率(%)と使用量/総量(GB)。失敗時はnull。</summary>
    public static (int Percent, double UsedGb, double TotalGb)? GetMemoryUsage()
    {
        try
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m) || m.TotalPhys == 0) return null;
            const double Gb = 1024.0 * 1024 * 1024;
            return ((int)m.MemoryLoad, (m.TotalPhys - m.AvailPhys) / Gb, m.TotalPhys / Gb);
        }
        catch { return null; }
    }

    public static (int Percent, bool Charging)? GetBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out SystemPowerStatus s)) return null;
            if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255) return null; // バッテリー無し/不明
            if (s.BatteryLifePercent > 100) return null;
            return (s.BatteryLifePercent, (s.BatteryFlag & 8) != 0 || s.ACLineStatus == 1);
        }
        catch { return null; }
    }
}
