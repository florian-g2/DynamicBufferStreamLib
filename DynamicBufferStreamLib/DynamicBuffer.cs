using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DynamicBufferStreamLib
{
    internal class DynamicBuffer : IDisposable
    {
        #region Private variables
        /// <summary>
        /// The pointer to to allocated umanaged memory.
        /// </summary>
        private IntPtr memoryPointer;

        /// <summary>
        /// The handle to a created buffer file .
        /// </summary>
        private SafeFileHandle fileHandle;

        /// <summary>
        /// Counter of active file buffers.
        /// </summary>
        /// <remarks>If the value reaches 0 during an buffer unallocation, the fileReadBuffer will also be unallocated.</remarks>
        private static byte activeFileBuffers;

        /// <summary>
        /// The pointer to the file read buffer.
        /// This buffer will be unallocted if no more file buffers are active.
        /// </summary>
        private static IntPtr fileReadBuffer = IntPtr.Zero;

        /// <summary>
        /// Defines the size in bytes of the <paramref name="fileReadBuffer" />.
        /// </summary>
        private static uint fileReadBufferSize;

        /// <summary>
        /// Struct which will contain the file offset for write operations.
        /// </summary>
        private NativeOverlapped nof;
        #endregion

        #region Internal variables
        /// <summary>
        /// Defines the mode of the buffer.
        /// <value>null means that this is custom buffer.</value>
        /// </summary>
        internal BufferMode mode = BufferMode.Custom;

        /// <summary>
        /// Indicates if this buffer is saved on disk.
        /// </summary>
        internal bool diskBuffer;

        /// <summary>
        /// The buffer size.
        /// </summary>
        internal int size;

        /// <summary>
        /// The entry position in the buffers array of the stream.
        /// </summary>
        internal int entryPos = -1;

        /// <summary>
        /// If the buffer is in use.
        /// </summary>
        internal bool isInUse;
        #endregion

        /// <summary>
        /// Initializes a new buffer and allocates the memory or creates the file.
        /// </summary>
        /// <param name="bufferMode">The buffer mode which the buffer will be created with.</param>
        internal DynamicBuffer(BufferMode bufferMode) : this(bufferMode.bufferSize, bufferMode.diskBuffer)
        {
            mode = bufferMode;
        }

        /// <summary>
        /// Initializes a new buffer with the specific values.
        /// </summary>
        /// <remarks>The buffer mode will be 255</remarks>
        /// <param name="bufferSize">The desired buffer size</param>
        /// <param name="diskBuffer">If the buffer should be stored on the disk</param>
        internal DynamicBuffer(int bufferSize, bool diskBuffer)
        {
            this.diskBuffer = diskBuffer;
            this.size = bufferSize;

            if (diskBuffer)
            {
                activeFileBuffers++;

                // creates and opens a non shareable temporary file which will be written directly onto the disk.
                fileHandle = Native.Kernel32.CreateFile(
                    Path.GetTempFileName(),
                    (uint)FileAccess.ReadWrite,
                    0,
                    IntPtr.Zero,
                    (uint)FileMode.Create,
                    Native.Kernel32.FILE_FLAG_WRITE_THROUGH | Native.Kernel32.FILE_FLAG_NO_BUFFERING | Native.Kernel32.FILE_FLAG_SEQUENTIAL_SCAN | Native.Kernel32.FILE_FLAG_DELETE_ON_CLOSE,
                    IntPtr.Zero);
            }
            else
            {
                memoryPointer = Marshal.AllocHGlobal(bufferSize);
            }
        }

        /// <summary>
        /// Writes the buffer.
        /// </summary>
        /// <remarks>The DynamicBuffer has no automatic offset / position tracking.</remarks>
        /// <param name="offset">The offset in the buffer.</param>
        /// <param name="buffer">The buffer which should be written.</param>
        /// <param name="amountBytes">The amount of bytes that should be written.</param>
        internal unsafe void Write(int offset, IntPtr buffer, int amountBytes)
        {
            if (diskBuffer)
            {
                // sets the offset.
                nof.OffsetLow = offset;

                uint bytesWritten;

                int result = Native.Kernel32.WriteFile(fileHandle, buffer, amountBytes, out bytesWritten, ref nof);

                if (result != 1 || bytesWritten != amountBytes)
                {
                    throw new IOException("disk buffer write failed!", new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            else
            {
                Buffer.MemoryCopy(buffer.ToPointer(), (memoryPointer + offset).ToPointer(), amountBytes, amountBytes);
                //Native.Msvcrt.memcpy(memoryPointer + offset, buffer, new UIntPtr((uint)amountBytes));
            }
        }

        /// <summary>
        /// Retrieves a pointer for this buffer.
        /// </summary>
        /// <remarks>
        /// Disk cached buffers will be read into the shared <paramref name="fileReadBuffer"/>.
        /// Important for disk cached buffers: This should be only called synchronously and the pointer should be handled synchronously. Do not call this a second time if first returned pointer is still in use.
        /// </remarks>
        /// <returns>A pointer to an umanaged memory which contains the buffer.</returns>
        internal unsafe IntPtr GetPointer()
        {
            if (diskBuffer)
            {
                // allocates a new file read buffer if the size is not matching or it is not existing.
                if (fileReadBufferSize == 0 || fileReadBufferSize != size)
                {
                    if (fileReadBuffer != IntPtr.Zero) Marshal.FreeHGlobal(fileReadBuffer);

                    fileReadBuffer = Marshal.AllocHGlobal(size);
                    fileReadBufferSize = (uint)size;
                }

                // sets the file positon to the beginning
                Native.Kernel32.SetFilePointerEx(fileHandle, 0, out _, 0);

                uint bytesRead;

                int result = Native.Kernel32.ReadFile(fileHandle, fileReadBuffer, fileReadBufferSize, out bytesRead, null);

                if (result != 1 || bytesRead == 0)
                {
                    throw new IOException("disk buffer read failed!", new Win32Exception(Marshal.GetLastWin32Error()));
                }

                return fileReadBuffer;
            }
            else
            {
                return memoryPointer;
            }
        }

        /// <summary>
        /// Unallocates the memory or closes the file handle.
        /// </summary>
        /// <remarks>Also unallocates the fileReadBuffer if this was the last disk buffer.</remarks>
        public void Dispose()
        {
            if (diskBuffer)
            {
                activeFileBuffers--;

                if (activeFileBuffers == 0)
                {
                    Marshal.FreeHGlobal(fileReadBuffer);
                    fileReadBuffer = IntPtr.Zero;
                    fileReadBufferSize = 0;
                }

                fileHandle.Dispose();
            }
            else
            {
                Marshal.FreeHGlobal(memoryPointer);
            }
        }
    }
}
