using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicBufferStreamLib
{
    internal class Native
    {
        internal static class Kernel32
        {
            [DllImport("kernel32.dll")]
            internal static extern void RtlZeroMemory(IntPtr dst, uint length);

            public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
            public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
            public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
            public const uint FILE_ATTRIBUTE_TEMPORARY = 0x00000100;
            public const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern SafeFileHandle CreateFile(
              [In][MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
              uint dwDesiredAccess, uint dwShareMode,
              [In] IntPtr lpSecurityAttributes,
              uint dwCreationDisposition,
              uint dwFlagsAndAttributes,
              [In] IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern unsafe int WriteFile(SafeFileHandle handle, IntPtr buffer, int numBytesToWrite, out uint numberOfBytesWritten, [In] ref NativeOverlapped lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern unsafe int WriteFile(SafeFileHandle handle, IntPtr buffer, int numBytesToWrite, out uint numberOfBytesWritten, NativeOverlapped* lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
            public static extern unsafe int ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);


            [DllImport("kernel32.dll")]
            public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);
        }

        internal static class Msvcrt
        {
            [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
            public static extern IntPtr memcpy(IntPtr dest, IntPtr src, UIntPtr count);
        }
    }
}
