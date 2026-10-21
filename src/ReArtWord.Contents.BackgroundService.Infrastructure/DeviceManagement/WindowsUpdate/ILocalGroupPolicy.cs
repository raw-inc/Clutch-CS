using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WindowsUpdate;

/// <summary>IGroupPolicyObjectの標準vtable順を維持するCOM契約です。</summary>
[ComImport]
[Guid("EA502723-A23D-11d1-A7D3-0000F87571E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILocalGroupPolicy
{
    /// <summary>標準インターフェースの作成slotです。この機能では呼びません。</summary>
    public void New([MarshalAs(UnmanagedType.LPWStr)] string domain, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags);

    /// <summary>標準インターフェースのドメイン読取りslotです。この機能では呼びません。</summary>
    public void OpenDs([MarshalAs(UnmanagedType.LPWStr)] string path, uint flags);

    /// <summary>ローカルマシンのポリシーを読み込みます。</summary>
    public void OpenLocalMachine(uint flags);

    /// <summary>標準インターフェースのリモートslotです。この機能では呼びません。</summary>
    public void OpenRemoteMachine([MarshalAs(UnmanagedType.LPWStr)] string name, uint flags);

    /// <summary>選択したセクションを標準レジストリポリシーとして保存します。</summary>
    public void Save([MarshalAs(UnmanagedType.Bool)] bool machine, [MarshalAs(UnmanagedType.Bool)] bool add, in Guid extension, in Guid application);

    /// <summary>標準インターフェースの削除slotです。この機能では呼びません。</summary>
    public void Delete();

    /// <summary>標準インターフェースの名前slotです。この機能では呼びません。</summary>
    public void GetName(IntPtr name, uint length);

    /// <summary>標準インターフェースの表示名slotです。この機能では呼びません。</summary>
    public void GetDisplayName(IntPtr name, uint length);

    /// <summary>標準インターフェースの表示名設定slotです。この機能では呼びません。</summary>
    public void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name);

    /// <summary>標準インターフェースのパスslotです。この機能では呼びません。</summary>
    public void GetPath(IntPtr path, uint length);

    /// <summary>標準インターフェースのDSパスslotです。この機能では呼びません。</summary>
    public void GetDsPath(uint section, IntPtr path, uint length);

    /// <summary>標準インターフェースのファイルパスslotです。この機能では呼びません。</summary>
    public void GetFileSystemPath(uint section, IntPtr path, uint length);

    /// <summary>呼出元が閉じるポリシーのレジストリhandleを取得します。</summary>
    public void GetRegistryKey(uint section, out IntPtr key);
}
