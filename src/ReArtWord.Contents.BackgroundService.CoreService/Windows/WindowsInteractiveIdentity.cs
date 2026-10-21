using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>ユーザー本人の通常・昇格トークンを選ぶWindows境界です。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsInteractiveIdentity
{
    /// <summary>現在のプロセスが有効な管理者権限を持つか確認します。</summary>
    /// <returns>管理者グループが有効ならtrueです。</returns>
    internal static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Systemではコンソール利用者、それ以外では自身のトークンから要求権限を選びます。</summary>
    /// <returns>呼出元が破棄するトークンです。未ログオン時の管理者モードだけSYSTEMを許可します。</returns>
    internal static SafeAccessTokenHandle UserToken(bool elevated = false, bool strictUserToken = false)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SafeAccessTokenHandle? token = null;
        bool systemFallback = false;
        bool restricted = false;
        try
        {
            if (identity.IsSystem)
            {
                uint session = WindowsProcessNative.WTSGetActiveConsoleSessionId();
                if (!WindowsProcessNative.WTSQueryUserToken(session, out token))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (!AllowsSystemFallback(elevated, session, error))
                    {
                        throw new InvalidOperationException("No interactive console user is available for the requested user terminal.");
                    }
                    token.Dispose();
                    if (!WindowsProcessNative.DuplicateTokenEx(identity.AccessToken, 0x02000000, IntPtr.Zero, 2, 1, out token))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }
                    systemFallback = true;
                }
                else
                {
                    // WTSのログオントークンそのものへSessionIdや整合性変更を適用しません。
                    if (!WindowsProcessNative.DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out SafeAccessTokenHandle owned))
                    {
                        int error = Marshal.GetLastWin32Error();
                        owned.Dispose();
                        throw new Win32Exception(error);
                    }
                    token.Dispose();
                    token = owned;
                }
            }
            else
            {
                if (!WindowsProcessNative.DuplicateTokenEx(identity.AccessToken, 0x02000000, IntPtr.Zero, 2, 1, out token))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            if (IsElevated(token) != elevated)
            {
                SafeAccessTokenHandle linked;
                if (strictUserToken && !identity.IsSystem && !elevated)
                {
                    // 自身の制限トークンならSeAssignPrimaryTokenを追加せずに通常シェルを起動できます。
                    if (!WindowsProcessNative.CreateRestrictedToken(identity.AccessToken, 4, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out linked))
                    {
                        int error = Marshal.GetLastWin32Error();
                        linked.Dispose();
                        throw new Win32Exception(error);
                    }
                    restricted = true;
                }
                else
                {
                    linked = LinkedToken(token);
                }
                token.Dispose();
                token = linked;
            }
            if (restricted)
            {
                AllowUserOwnedObjects(token, identity.User!);
            }
            if (strictUserToken && !elevated)
            {
                SetMediumIntegrity(token);
            }
            using WindowsIdentity user = new(token.DangerousGetHandle());
            if (user.IsSystem && !systemFallback || IsElevated(token) != elevated || new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator) != elevated)
            {
                throw new InvalidOperationException("The requested user terminal privilege is unavailable. Another account or SYSTEM will not be substituted.");
            }
            SafeAccessTokenHandle result = token;
            token = null;
            return result;
        }
        finally
        {
            token?.Dispose();
        }
    }

    /// <summary>LUA生成が引き継ぐ管理者向け既定DACLへ、本人が子プロセスを操作する権限を加えます。</summary>
    private static void AllowUserOwnedObjects(SafeAccessTokenHandle token, SecurityIdentifier user)
    {
        _ = WindowsProcessNative.GetTokenInformation(token, 6, IntPtr.Zero, 0, out int length);
        IntPtr info = Marshal.AllocHGlobal(length);
        try
        {
            if (!WindowsProcessNative.GetTokenInformation(token, 6, info, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            IntPtr aclPointer = Marshal.ReadIntPtr(info);
            if (aclPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("The restricted user token has no default access policy.");
            }
            byte[] original = new byte[unchecked((ushort)Marshal.ReadInt16(aclPointer, 2))];
            Marshal.Copy(aclPointer, original, 0, original.Length);
            RawAcl acl = new(original, 0);
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x10000000, user, false, null));
            byte[] bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            IntPtr updated = Marshal.AllocHGlobal(IntPtr.Size + bytes.Length);
            try
            {
                Marshal.WriteIntPtr(updated, updated + IntPtr.Size);
                Marshal.Copy(bytes, 0, updated + IntPtr.Size, bytes.Length);
                if (!WindowsProcessNative.SetTokenInformation(token, 6, updated, IntPtr.Size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(updated);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    /// <summary>LUAのグループ制限に加えて、通常ユーザーの整合性レベルへ下げます。</summary>
    private static void SetMediumIntegrity(SafeAccessTokenHandle token)
    {
        SecurityIdentifier medium = new("S-1-16-8192");
        byte[] sid = new byte[medium.BinaryLength];
        medium.GetBinaryForm(sid, 0);
        int labelSize = 2 * IntPtr.Size;
        IntPtr label = Marshal.AllocHGlobal(labelSize + sid.Length);
        try
        {
            Marshal.WriteIntPtr(label, label + labelSize);
            Marshal.WriteInt32(label, IntPtr.Size, 0x20);
            Marshal.Copy(sid, 0, label + labelSize, sid.Length);
            if (!WindowsProcessNative.SetTokenInformation(token, 25, label, labelSize + sid.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(label);
        }
    }

    /// <summary>未ログオンだけをSYSTEM代替対象にし、アクセス拒否などの障害を混同しません。</summary>
    /// <returns>管理者モードで対話ユーザーが存在しない場合だけtrueです。</returns>
    internal static bool AllowsSystemFallback(bool elevated, uint session, int error)
        => elevated && (error is 1008 or 1312 || session == uint.MaxValue && error == 87);

    /// <summary>グループ所属だけでなく、OSが保持する実際の昇格状態を確認します。</summary>
    /// <returns>TokenElevationが有効な場合はtrueです。</returns>
    internal static bool IsElevated(SafeAccessTokenHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!WindowsProcessNative.GetTokenInformation(token, 20, buffer, sizeof(int), out _))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>UACで結び付けられた同じユーザーの反対側の権限を取得します。</summary>
    /// <returns>呼出元が解放する関連トークンです。</returns>
    private static SafeAccessTokenHandle LinkedToken(SafeAccessTokenHandle source)
    {
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!WindowsProcessNative.GetTokenInformation(source, 19, buffer, IntPtr.Size, out _))
            {
                throw new InvalidOperationException("This user has no token for the requested privilege. UAC elevation is not requested by Terminal.");
            }
            using SafeAccessTokenHandle linked = new(Marshal.ReadIntPtr(buffer));
            if (!WindowsProcessNative.DuplicateTokenEx(linked, 0x02000000, IntPtr.Zero, 2, 1, out SafeAccessTokenHandle primary))
            {
                int error = Marshal.GetLastWin32Error();
                primary.Dispose();
                throw new Win32Exception(error);
            }
            return primary;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}
