using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RunaVoice_WinUI.Services.Engine;

public static unsafe partial class EngineNative
{
    public const uint RUNA_ENGINE_ABI_VERSION = 2;
    public const int RUNA_OK = 0;
    public const int RUNA_ERR_GENERIC = 1;
    public const int RUNA_ERR_INVALID_ARG = 2;
    public const int RUNA_ERR_PANIC = 3;
    public const int RUNA_ERR_BUFFER_TOO_SMALL = 4;

    public const string DllName = "runa_engine";

    private static IntPtr _dllHandle = IntPtr.Zero;
    private static readonly object _initLock = new();

    static EngineNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(EngineNative).Assembly, ResolveDll);
    }

    private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == DllName || libraryName == "runa_engine.dll")
        {
            lock (_initLock)
            {
                if (_dllHandle != IntPtr.Zero) return _dllHandle;

                string fullPath = Path.Combine(AppContext.BaseDirectory, "runa_engine.dll");
                if (File.Exists(fullPath))
                {
                    try
                    {
                        _dllHandle = NativeLibrary.Load(fullPath);
                        return _dllHandle;
                    }
                    catch { }
                }
            }
        }
        return IntPtr.Zero;
    }

    public static bool IsDllAvailable()
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, "runa_engine.dll");
        if (!File.Exists(fullPath)) return false;

        string shaPath = Path.Combine(AppContext.BaseDirectory, "runa_engine.dll.sha256");
        if (File.Exists(shaPath))
        {
            try
            {
                string expectedHash = File.ReadAllText(shaPath).Trim();
                using var fs = File.OpenRead(fullPath);
                byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(fs);
                string actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                if (!string.Equals(expectedHash.ToLowerInvariant(), actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    [LibraryImport(DllName, EntryPoint = "runa_engine_abi_version")]
    public static partial uint RunaEngineAbiVersion();

    [LibraryImport(DllName, EntryPoint = "runa_engine_create")]
    public static partial int RunaEngineCreate(
        byte* configJson,
        nuint len,
        delegate* unmanaged[Cdecl]<IntPtr, uint, byte*, nuint, void> cb,
        IntPtr user,
        out IntPtr outEngine);

    [LibraryImport(DllName, EntryPoint = "runa_engine_last_create_error")]
    public static partial int RunaEngineLastCreateError(
        byte* buf,
        nuint cap,
        out nuint len);

    public static string GetLastCreateError()
    {
        try
        {
            byte[] buffer = new byte[8192];
            fixed (byte* ptr = buffer)
            {
                int res = RunaEngineLastCreateError(ptr, (nuint)buffer.Length, out nuint actualLen);
                if (res == RUNA_OK && actualLen > 0)
                {
                    return System.Text.Encoding.UTF8.GetString(ptr, (int)Math.Min((int)actualLen, buffer.Length));
                }
            }
        }
        catch { }
        return string.Empty;
    }

    [LibraryImport(DllName, EntryPoint = "runa_engine_command")]
    public static partial int RunaEngineCommand(
        IntPtr engine,
        byte* json,
        nuint len);

    [LibraryImport(DllName, EntryPoint = "runa_engine_query")]
    public static partial int RunaEngineQuery(
        IntPtr engine,
        byte* json,
        nuint len,
        byte* outBuf,
        nuint outCap,
        out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "runa_engine_set_video_surface", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RunaEngineSetVideoSurface(
        IntPtr engine,
        string streamId,
        IntPtr swapChainPanelNative);

    [LibraryImport(DllName, EntryPoint = "runa_engine_last_error")]
    public static partial int RunaEngineLastError(
        IntPtr engine,
        byte* buf,
        nuint cap,
        out nuint len);

    [LibraryImport(DllName, EntryPoint = "runa_engine_destroy")]
    public static partial void RunaEngineDestroy(IntPtr engine);
}

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISwapChainPanelNative
{
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain);
}
