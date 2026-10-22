using System;
using System.Diagnostics;
using System.IO;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// EmulationHostのうち、ディスク交換(ファミコンディスクシステムのA面/B面、複数枚組ディスク)を
/// 担当する部分。コアが SET_DISK_CONTROL_(EXT_)INTERFACE で渡してきた関数群を使う。
/// 交換は「イジェクト → 数十フレーム待つ → 画像切り替え → 挿入」の順で行う
/// (ディスクシステムのBIOSが入れ替えを検知できるよう、実機同様に一度抜いた状態を見せる)。
/// 実際の呼び出しは全てエミュレーションスレッドのループ内で行う。
/// </summary>
public sealed partial class EmulationHost
{
    /// <summary>イジェクトしてから次のディスクを挿入するまでに回すフレーム数。</summary>
    private const int DiskEjectWaitFrames = 45;

    private DiskControl? _disk;

    /// <summary>未対応のenvironmentコマンド番号を、種類ごとに1回だけログへ出すための記録(原因調査用)。</summary>
    private readonly System.Collections.Generic.HashSet<uint> _loggedUnhandledEnv = new();
    private volatile int _diskSwitchTarget = -1;
    private int _diskSwitchPhase; // 0=待機, 1=イジェクト後の待ち
    private int _diskSwitchWaitFrames;
    private int _diskSwitchPendingIndex;

    /// <summary>
    /// 起動時に最初から入れておくディスクの番号(0始まり。複数枚組を「2枚目から始める」ときに、起動前に呼び出し側が決める)。
    /// 0なら何もしない(コアが選ぶ先頭のディスクのまま)。
    /// </summary>
    public int InitialDiskIndex { get; set; }

    /// <summary>
    /// retro_load_game直後・最初のretro_runより前に、InitialDiskIndexのディスクへ入れ替える。
    /// まだ1フレームも動いていないので、ゲームが先頭のディスクを読み始める前に済む。
    /// コアが断ったとき(失敗を返したとき)は、通常の入れ替え(起動後に数十フレーム待つ方式)に任せる。
    /// </summary>
    private void ApplyInitialDisk()
    {
        int index = InitialDiskIndex;
        if (index <= 0 || _disk is null) return;

        int count = _disk.NumImages;
        if (index >= count)
        {
            Debug.WriteLine($"[Disk] 起動時のディスク指定が範囲外: index={index} count={count}");
            return;
        }

        bool ejected = _disk.SetEjected(true);
        bool indexOk = ejected && _disk.SetImageIndex(index);
        bool inserted = _disk.SetEjected(false);
        Debug.WriteLine($"[Disk] 起動時のディスク指定: index={index} eject={ejected} set={indexOk} insert={inserted}");

        if (!(ejected && indexOk && inserted)) _diskSwitchTarget = index;
    }

    /// <summary>ディスクの入れ替えが行われたとき(または完了したとき)に発生する。</summary>
    public event EventHandler? DiskChanged;

    /// <summary>コアが認識しているディスク枚数(FDSならA面/B面を1枚として数えた面数)。非対応なら0。</summary>
    public int DiskCount => _disk?.NumImages ?? 0;

    /// <summary>現在挿入されているディスクの番号(0始まり)。</summary>
    public int DiskIndex => _disk?.ImageIndex ?? 0;

    /// <summary>入れ替え作業中(イジェクト待ちを含む)かどうか。</summary>
    public bool IsDiskSwitching => _diskSwitchTarget >= 0 || _diskSwitchPhase != 0 || _fdsScriptRequested is not null || _fdsStep >= 0;

    // ---- fceumm(ファミコンディスクシステム)用: ボタン入力の代行 ----
    // FCEUmmはlibretroのディスクコントロールに対応しておらず、ディスクの入れ替えを
    // Lボタン(面/ディスクの選択)とRボタン(挿入/取り出し)のコントローラー入力で受け付ける。
    // そこで、中断メニューの操作から「取り出し(R) → 次の面を選択(L) → 挿入(R)」を
    // 仮想のボタン押下として順に流す。ディスク1の裏面(B面)も、ディスク2も、
    // 同じ操作を繰り返すたびに順送り(A→B→次のディスクのA→…→最初へ)で切り替わる。
    //
    // Mesenも同様にディスクコントロールを持たず、R1=「面を替える」のボタン入力で操作する。
    // MesenのR1は取り出し・挿入までコア内部で行うので、1回押すだけでよい。
    private static readonly (uint Mask, int Frames)[] FceummFlipScript =
    {
        (1u << (int)RetroJoypad.R, 5),  // 取り出し
        (0, 40),
        (1u << (int)RetroJoypad.L, 5),  // 次の面を選択
        (0, 40),
        (1u << (int)RetroJoypad.R, 5),  // 挿入
        (0, 10),
    };

    private static readonly (uint Mask, int Frames)[] MesenSwitchSideScript =
    {
        (1u << (int)RetroJoypad.R, 5),  // 面を替える(取り出し→反転→挿入はコア側)
        (0, 10),
    };

    private volatile (uint Mask, int Frames)[]? _fdsScriptRequested;
    private (uint Mask, int Frames)[]? _fdsScript;
    private int _fdsStep = -1;
    private int _fdsStepFramesLeft;

    private bool IsCore(string name) =>
        Path.GetFileNameWithoutExtension(_corePath).Contains(name, StringComparison.OrdinalIgnoreCase);

    private bool UsesFceummFdsButtons => _disk is null && IsFamicomDiskSystemContent() && IsCore("fceumm");
    private bool UsesMesenFdsButtons => _disk is null && IsFamicomDiskSystemContent() && IsCore("mesen");

    /// <summary>コアがディスクコントロールを持たず、ボタン代行でFDSのディスクを操作する状態か。</summary>
    private bool UsesFdsButtonFlip => UsesFceummFdsButtons || UsesMesenFdsButtons;

    /// <summary>中断メニューに「ディスク交換」を出すべきか。</summary>
    public bool HasDiskSwitch => DiskCount >= 2 || UsesFdsButtonFlip;


    /// <summary>中断メニューに出す文言。</summary>
    public string GetDiskMenuText()
    {
        int count = DiskCount;
        if (count >= 2)
        {
            return "ディスク入れ替え";
        }

        return "ディスク面を替える";
    }

    /// <summary>指定番号のディスクへ入れ替える(エミュレーションスレッドが次のループで処理する)。</summary>
    public void RequestDiskSwitch(int index)
    {
        if (_disk is null) return;
        int count = DiskCount;
        if (count < 2 || index < 0 || index >= count) return;
        _diskSwitchTarget = index;
    }

    /// <summary>次のディスク(最後なら先頭)へ入れ替える。</summary>
    public void RequestNextDisk()
    {
        int count = DiskCount;
        if (count >= 2)
        {
            RequestDiskSwitch((DiskIndex + 1) % count);
            return;
        }

        if (_fdsStep >= 0) return;
        if (UsesFceummFdsButtons) _fdsScriptRequested = FceummFlipScript;
        else if (UsesMesenFdsButtons) _fdsScriptRequested = MesenSwitchSideScript;
    }

    /// <summary>メニュー表示用のディスク名(例: "1A" / "2B" / "ディスク3")。</summary>
    public string GetDiskName(int index)
    {
        string? label = _disk?.GetImageLabel(index);
        if (!string.IsNullOrEmpty(label)) return label;

        if (IsFamicomDiskSystemContent())
        {
            // ディスクシステムのディスクは1枚に表裏(A面/B面)があり、コアは面ごとに1つの画像として扱う。
            return $"{index / 2 + 1}{(index % 2 == 0 ? 'A' : 'B')}";
        }

        return $"ディスク{index + 1}";
    }

    private bool? _isFdsContent;

    /// <summary>読み込むROMがファミコンディスクシステムのディスクイメージ(.fds/.qd。zipなら中身)か。</summary>
    private bool IsFamicomDiskSystemContent()
    {
        if (_isFdsContent is bool cached) return cached;

        bool result = false;
        string ext = Path.GetExtension(_romPath).ToLowerInvariant();
        if (ext is ".fds" or ".qd")
        {
            result = true;
        }
        else if (ext == ".zip")
        {
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(_romPath);
                foreach (var entry in zip.Entries)
                {
                    string entryExt = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (entryExt is ".fds" or ".qd") { result = true; break; }
                }
            }
            catch
            {
                // 開けないzipはFDSではないものとして扱う。
            }
        }

        _isFdsContent = result;
        return result;
    }

    /// <summary>ループの先頭側(一時停止中も含む)で呼ぶ。要求があればイジェクトまで行う。</summary>
    private void ProcessDiskSwitchRequest()
    {
        var requested = _fdsScriptRequested;
        if (requested is not null)
        {
            _fdsScriptRequested = null;
            if (_fdsStep < 0)
            {
                _fdsScript = requested;
                _fdsStep = 0;
                _fdsStepFramesLeft = requested[0].Frames;
                _input.VirtualPressMask = requested[0].Mask;
            }
        }

        int target = _diskSwitchTarget;
        if (target < 0) return;
        if (_diskSwitchPhase != 0 || _disk is null) { if (_disk is null) _diskSwitchTarget = -1; return; }

        _diskSwitchTarget = -1;
        if (target == _disk.ImageIndex && !_disk.IsEjected) return;

        _diskSwitchPendingIndex = target;
        if (_disk.SetEjected(true))
        {
            _diskSwitchPhase = 1;
            _diskSwitchWaitFrames = DiskEjectWaitFrames;
            ReportStatus("ディスクを取り出しました");
        }
        else
        {
            ReportStatus("ディスクの取り出しに失敗しました");
        }
    }

    /// <summary>retro_run()の直後に毎フレーム呼ぶ。待ちが終わったら画像を切り替えて挿入する。</summary>
    private void TickDiskSwitch()
    {
        if (_fdsStep >= 0 && _fdsScript is not null && --_fdsStepFramesLeft <= 0)
        {
            _fdsStep++;
            if (_fdsStep >= _fdsScript.Length)
            {
                _fdsStep = -1;
                _input.VirtualPressMask = 0;
                ReportStatus("ディスクの面を替えました");
                DiskChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _input.VirtualPressMask = _fdsScript[_fdsStep].Mask;
                _fdsStepFramesLeft = _fdsScript[_fdsStep].Frames;
            }
        }

        if (_diskSwitchPhase != 1 || _disk is null) return;
        if (--_diskSwitchWaitFrames > 0) return;

        _diskSwitchPhase = 0;
        bool indexOk = _disk.SetImageIndex(_diskSwitchPendingIndex);
        bool insertOk = _disk.SetEjected(false);

        ReportStatus(indexOk && insertOk
            ? $"ディスク {GetDiskName(_diskSwitchPendingIndex)} を入れました"
            : "ディスクの入れ替えに失敗しました");
        DiskChanged?.Invoke(this, EventArgs.Empty);
    }
}
