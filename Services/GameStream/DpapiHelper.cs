using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Windows DPAPI (Data Protection API) yerel Win32 şifreleme yardımcısı (PDF Bölüm 5.3)
    /// Hiçbir haricî NuGet bağımlılığı olmadan CryptProtectData ve CryptUnprotectData kullanır.
    /// </summary>
    public static class DpapiHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static string ProtectString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            DATA_BLOB entropyBlob = new DATA_BLOB();

            IntPtr inPtr = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inPtr, plainBytes.Length);
            inBlob.cbData = plainBytes.Length;
            inBlob.pbData = inPtr;

            try
            {
                if (CryptProtectData(ref inBlob, "RunaSunshineCreds", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                {
                    byte[] cipherBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
                    LocalFree(outBlob.pbData);
                    return Convert.ToBase64String(cipherBytes);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inPtr);
            }

            return "";
        }

        public static string UnprotectString(string base64Cipher)
        {
            if (string.IsNullOrEmpty(base64Cipher)) return "";

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(base64Cipher);
                DATA_BLOB inBlob = new DATA_BLOB();
                DATA_BLOB outBlob = new DATA_BLOB();
                DATA_BLOB entropyBlob = new DATA_BLOB();

                IntPtr inPtr = Marshal.AllocHGlobal(cipherBytes.Length);
                Marshal.Copy(cipherBytes, 0, inPtr, cipherBytes.Length);
                inBlob.cbData = cipherBytes.Length;
                inBlob.pbData = inPtr;

                try
                {
                    if (CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    {
                        byte[] plainBytes = new byte[outBlob.cbData];
                        Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                        LocalFree(outBlob.pbData);
                        return Encoding.UTF8.GetString(plainBytes);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(inPtr);
                }
            }
            catch
            {
                // Bozuk veya başka kullanıcıya ait veri
            }

            return "";
        }
    }
}
