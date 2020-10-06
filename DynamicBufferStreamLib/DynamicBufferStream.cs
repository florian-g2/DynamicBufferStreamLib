using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicBufferStreamLib
{
    public class DynamicBufferStream : Stream
    {
        #region Private variables
        /// <summary>
        /// Counter of the dstFileLength
        /// </summary>
        private long length = 0;

        /// <summary>
        /// Restricts the amount of simultaneously used buffers.
        /// </summary>
        /// <remarks>If the max allowed simultaneously active buffers amount is reduced, the semaphore will not be replaced but will lock the reduced amount.</remarks>
        private SemaphoreSlim bufferAmountRestrictionSemaphore;

        /// <summary>
        /// Specifies the amount of manual locked spaces.
        /// </summary>
        private int bufferAmountRestrictionSemaphoreHardlocked = 0;

        /// <summary>
        /// Restricts that only one buffer at a time is allowed to write the destination.
        /// </summary>
        private SemaphoreSlim writeQueue = new SemaphoreSlim(1, 1);

        /// <summary>
        /// A file handle of the destination file.
        /// </summary>
        private SafeFileHandle fileHandle;

        /// <summary>
        /// If the fileHandle should be closed on dispose;
        /// </summary>
        private bool closeFileHandleOnDispose = false;

        /// <summary>
        /// Indicates the current active buffer mode.
        /// </summary>
        private BufferMode currentBufferMode;

        /// <summary>
        /// The position of the current buffer mode in the bufferModes array.
        /// </summary>
        private int currentBufferModeIndex;

        /// <summary>
        /// The list of assigned buffer modes.
        /// </summary>
        /// <remarks>The buffer modes capacity must increase from left to right.</remarks>
        /// <example><code>
        /// new BufferMode[] {
        ///     new BufferMode(65536, 1, false, 1500, true),
        ///     new BufferMode(1048576, 2, false, 1500),
        ///     new BufferMode(2097152, 2, false, 2000),
        ///     new BufferMode(4194304, 4, false, 3000),
        ///     new BufferMode(8388608, 4, false, 5000),
        ///     new BufferMode(16777216, 4, true, 7500)
        /// }
        /// </code>
        /// </example>
        private BufferMode[] bufferModes;

        /// <summary>
        /// The current position of the hotBuffer.
        /// </summary>
        private int hotBufferOffset;

        /// <summary>
        /// The hot buffer which will get written every write operation.
        /// </summary>
        /// <remarks>This can only be an memory buffer.</remarks>
        private DynamicBuffer hotBuffer;

        /// <summary>
        /// An second hotbuffer which is locked (with swappedHotBufferLock) due an buffer to disk copy.
        /// </summary>
        /// <remarks>Only used if the active buffer mode specifies disk buffers</remarks>
        private DynamicBuffer swappedHotBuffer;


        /// <summary>
        /// A lock to prevent the concurrent access on the swappedHotBuffer.
        /// </summary>
        private SemaphoreSlim swappedHotBufferSemaphore = new SemaphoreSlim(1,1);

        /// <summary>
        /// An array which contains initalizied buffers.
        /// </summary>
        private DynamicBuffer[] buffers;

        /// <summary>
        /// Counter of buffers that are in use.
        /// </summary>
        private byte buffersInUse;

        /// <summary>
        /// Indicates if this stream got flushed. If this Stream is flushed, it is no more writeable.
        /// </summary>
        private bool gotFlushed;

        #region Performance evaluation specific variables
        /// <summary>
        /// Trigger which executes a performance evalution method.
        /// </summary>
        private Timer performanceDetectionTrigger;

        /// <summary>
        /// Boolean which is indicating if the performance evaluation is running.
        /// </summary>
        private bool performanceEvaluationRunning;

        /// <summary>
        /// Trigger which executes a method that collects samples of the buffer load.
        /// </summary>
        private Timer bufferLoadSamplerTrigger;

        /// <summary>
        /// The amount of collected samples.
        /// </summary>
        private ushort collectedSamples;

        /// <summary>
        /// All sampled buffer loads added up.
        /// </summary>
        private ushort bufferLoad;

        private Stopwatch bufferModeChangeStopwatch = new Stopwatch();

        /// <summary>
        /// Semaphore to keep the performance evaluation and worker thread in sync.
        /// </summary>
        private SemaphoreSlim performanceEvaluationSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Semaphore to keep the performance evaluation and worker thread in sync.
        /// </summary>
        private SemaphoreSlim workSemaphore = new SemaphoreSlim(1, 1);
        #endregion
        #endregion

        #region Public variables
        /// <summary>
        /// This value is used to confirm that the passed bufferModes array does not undercuts this value and that the Flush() method has a baseline for the lowest cluster size.
        /// </summary>
        public int MinClusterSize = 4096;

        /// <summary>
        /// Indicates if the stream is disposed.
        /// </summary>
        public bool IsDisposed;

        /// <summary>
        /// Action which gets called if the buffer mode changes.
        /// </summary>
        public EventHandler<BufferModeChangeArgs> OnBufferModeRaise;
        public EventHandler<BufferModeChangeArgs> OnBufferModeLowering;
        #endregion

        #region Public classes
        public class BufferModeChangeArgs : EventArgs
        {
            public BufferMode previousBufferMode;
            public BufferMode bufferMode;
            public long timeSpent;
            public float previousAverageBufferLoad;
        }
        #endregion

        #region Private methods
        /// <summary>
        /// Lowers the buffer mode by one position.
        /// </summary>
        /// <remarks>
        /// This function creates a new smaller hotBuffer,
        /// enques the older and bigger hotBuffer in the transfers queue,
        /// locks spaces in the bufferAmountRestrictionSemaphore(only on max buffer amount change), 
        /// and disposes unused buffers of an other mode. (only on max buffer amount change)
        /// </remarks>
        private void LowerBufferMode()
        {
            if (currentBufferModeIndex == 0) throw new InvalidOperationException("Cannot lower the buffer mode any further!");

            void ShrinkHotBuffer(int newSize)
            {
                if (newSize >= hotBufferOffset)
                {
                    DynamicBuffer oldHotBuffer = hotBuffer;
                    hotBuffer = new DynamicBuffer(newSize, false);
                    hotBuffer.Write(0, oldHotBuffer.GetPointer(), hotBufferOffset);

                    oldHotBuffer.Dispose();
                }
                else
                {
                    int requiredSplits = hotBufferOffset / newSize;

                    for (int i = 0; i < requiredSplits; i++)
                    {
                        bufferAmountRestrictionSemaphore.Wait();

                        DynamicBuffer completedBuffer = RentFreeBuffer();

                        completedBuffer.Write(0, hotBuffer.GetPointer(), newSize);

                        EnqueueCompletedBuffer(completedBuffer);
                    }

                    // creating a fitting new hot buffer from the remaining bytes
                    DynamicBuffer oldHotBuffer = hotBuffer;
                    hotBuffer = new DynamicBuffer(newSize, false);
                    hotBuffer.Write(0, oldHotBuffer.GetPointer() + newSize, hotBufferOffset % newSize);

                    hotBufferOffset = hotBufferOffset % newSize;

                    oldHotBuffer.Dispose();
                }
            }

            void UnallocateSwappedHotBuffer()
            {
                swappedHotBuffer.Dispose();
                swappedHotBuffer = null;
            }

            void HardlockReductedCapacityOnSemaphore(int reducedAmount)
            {
                for (int i = 0; i < reducedAmount; i++)
                {
                    bufferAmountRestrictionSemaphore.Wait();
                }

                bufferAmountRestrictionSemaphoreHardlocked += reducedAmount;
            }

            void ChangeBufferAmount(byte bufferAmount)
            {
                if (bufferAmount < currentBufferMode.maxBufferAmount)
                {
                    HardlockReductedCapacityOnSemaphore(currentBufferMode.maxBufferAmount - bufferAmount);
                    DisposeUnusedBuffersDueAmountReduction();
                }
            }

            void DisposeUnusedBuffersDueAmountReduction()
            {
                for (int i = 0; i < buffers.Length; i++)
                {
                    if (buffers[i] != null && !buffers[i].isInUse && buffers[i].mode != currentBufferMode)
                    {
                        buffers[i].Dispose();
                        buffers[i] = null;
                    }
                }
            }

            BufferMode newBufferMode = bufferModes[currentBufferModeIndex - 1];

            ChangeBufferAmount(newBufferMode.maxBufferAmount);
            ShrinkHotBuffer(newBufferMode.bufferSize);

            if (!newBufferMode.diskBuffer && currentBufferMode.diskBuffer)
            {
                swappedHotBufferSemaphore.AvailableWaitHandle.WaitOne();
                UnallocateSwappedHotBuffer();
            }

            performanceDetectionTrigger.Change(newBufferMode.performanceDetectionInterval, newBufferMode.performanceDetectionInterval);

            currentBufferMode = newBufferMode;
            currentBufferModeIndex -= 1;
        }

        /// <summary>
        /// Raises the buffer mode by one position.
        /// </summary>
        /// <remarks>
        /// This function creates a new greater hotBuffer,
        /// and unlocks spaces in the bufferAmountRestrictionSemaphore if an max buffer reduction locked them before.
        /// </remarks>
        private void RaiseBufferMode()
        {
            if (currentBufferModeIndex == bufferModes.Length - 1) throw new InvalidOperationException("Cannot raise the buffer mode any further!");

            void AllocateGreaterHotBuffer(int size)
            {
                if (size > hotBuffer.size)
                {
                    DynamicBuffer oldHotBuffer = hotBuffer;
                    hotBuffer = new DynamicBuffer(size, false);
                    hotBuffer.Write(0, oldHotBuffer.GetPointer(), hotBufferOffset);

                    oldHotBuffer.Dispose();
                }
            }

            void AllocateSwappedHotBuffer(int size)
            {
                swappedHotBuffer = new DynamicBuffer(size, false);
            }

            void UnlockSemaphoreOnAmountIncrease(int increasedAmount)
            {
                for (int i = 0; i < increasedAmount; i++)
                {
                    bufferAmountRestrictionSemaphore.Release();
                }

                bufferAmountRestrictionSemaphoreHardlocked = -increasedAmount;
            }

            void ChangeBufferAmount(byte bufferAmount)
            {
                if (bufferAmount > currentBufferMode.maxBufferAmount)
                {
                    UnlockSemaphoreOnAmountIncrease(bufferAmount - currentBufferMode.maxBufferAmount);
                }
            }


            BufferMode newBufferMode = bufferModes[currentBufferModeIndex + 1];

            ChangeBufferAmount(newBufferMode.maxBufferAmount);
            AllocateGreaterHotBuffer(newBufferMode.bufferSize);

            if (newBufferMode.diskBuffer && !currentBufferMode.diskBuffer) AllocateSwappedHotBuffer(newBufferMode.bufferSize);

            performanceDetectionTrigger.Change(newBufferMode.performanceDetectionInterval, newBufferMode.performanceDetectionInterval);

            currentBufferMode = newBufferMode;
            currentBufferModeIndex += 1;
        }

        /// <summary>
        /// Samples the buffer load.
        /// </summary>
        /// <remarks>This method is called by the bufferLoadSamplerTrigger.</remarks>
        /// <param name="_">Unused but required parameter.</param>
        private void BufferLoadSampler(object _)
        {
            if (performanceEvaluationRunning) return;

            if (bufferAmountRestrictionSemaphore.CurrentCount == 0)
            {
                bufferLoad += currentBufferMode.maxBufferAmount;
            }
            else
            {
                bufferLoad += buffersInUse;
            }

            collectedSamples++;
        }

        /// <summary>
        /// Pauses the workerThread and evaluates the performance which will may lower or raise the buffer mode.
        /// </summary>
        /// <remarks>
        /// Too raise the buffer mode, the buffer load must be an average of equal or greater the max buffer amount - 0.5.
        /// Too lower the buffer mode, the buffer load must be an average of lower or equal the half of the max buffer amount.
        /// </remarks>
        /// <param name="_">Unused but required parameter.</param>
        private void EvaluatePerformace(object _)
        {
            if (performanceEvaluationSemaphore.CurrentCount == 0) return;
            if (collectedSamples < 4) return;

            performanceEvaluationSemaphore.Wait();
            performanceEvaluationRunning = true;
            workSemaphore.AvailableWaitHandle.WaitOne();

            float averageBufferLoad = bufferLoad == 0 ? 0 : (float)bufferLoad / collectedSamples;

            bool BufferLoweringAdvantagous()
            {
                int currentBufferCapacity = bufferModes[currentBufferModeIndex].bufferSize * bufferModes[currentBufferModeIndex].maxBufferAmount;
                int lowerBufferModeCapacity = bufferModes[currentBufferModeIndex - 1].bufferSize * bufferModes[currentBufferModeIndex - 1].maxBufferAmount;

                return averageBufferLoad < currentBufferMode.maxBufferAmount / (currentBufferCapacity / lowerBufferModeCapacity);
            }

            if (currentBufferModeIndex != bufferModes.Length - 1 && averageBufferLoad > currentBufferMode.maxBufferAmount * 0.75f)
            {
                bufferModeChangeStopwatch.Reset();
                bufferModeChangeStopwatch.Start();

                RaiseBufferMode();

                bufferModeChangeStopwatch.Stop();

                if (OnBufferModeRaise != null) Task.Run(() => OnBufferModeRaise(this, new BufferModeChangeArgs { 
                    bufferMode = currentBufferMode,
                    previousBufferMode = bufferModes[currentBufferModeIndex - 1],
                    previousAverageBufferLoad = averageBufferLoad,
                    timeSpent = bufferModeChangeStopwatch.ElapsedMilliseconds }));
            }
            else if (currentBufferModeIndex != 0 && BufferLoweringAdvantagous())
            {
                bufferModeChangeStopwatch.Reset();
                bufferModeChangeStopwatch.Start();

                LowerBufferMode();

                bufferModeChangeStopwatch.Stop();

                if (OnBufferModeLowering != null) Task.Run(() => OnBufferModeLowering(this, new BufferModeChangeArgs
                {
                    bufferMode = currentBufferMode,
                    previousBufferMode = bufferModes[currentBufferModeIndex + 1],
                    previousAverageBufferLoad = averageBufferLoad,
                    timeSpent = bufferModeChangeStopwatch.ElapsedMilliseconds
                }));
            }

            bufferLoad = 0;
            collectedSamples = 0;

            performanceEvaluationRunning = false;
            performanceEvaluationSemaphore.Release();
        }

        /// <summary>
        /// Rents a unused instantiated buffer or allocates a new one.
        /// </summary>
        /// <returns>A rented buffer</returns>
        private DynamicBuffer RentFreeBuffer()
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null && !buffers[i].isInUse)
                {
                    if (buffers[i].mode != currentBufferMode)
                    {
                        buffers[i].Dispose();
                        buffers[i] = null;

                        continue;
                    }

                    buffersInUse++;
                    buffers[i].isInUse = true;

                    return buffers[i];
                }
            }

            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] == null)
                {
                    buffersInUse++;

                    DynamicBuffer newBuffer = new DynamicBuffer(currentBufferMode);

                    newBuffer.entryPos = i;

                    buffers[i] = newBuffer;

                    return newBuffer;
                }
            }

            throw new Exception("Logic error! No free buffer!");
        }

        /// <summary>
        /// Returns a rented buffer and disposes it if the buffer mode is not equal to the current active.
        /// </summary>
        /// <param name="buffer">The previously rented buffer</param>
        private void ReturnBuffer(DynamicBuffer buffer)
        {
            if (buffersInUse > currentBufferMode.maxBufferAmount)
            {
                buffers[buffer.entryPos] = null;
                buffer.Dispose();

                buffersInUse--;

                return;
            }

            buffersInUse--;

            if (buffer.mode != currentBufferMode)
            {
                int bufferEntryPos = buffer.entryPos;
                buffer.Dispose();

                buffers[bufferEntryPos] = null;
            }
            else
            {
                buffers[buffer.entryPos].isInUse = false;
            }
        }

        /// <summary>
        /// Enqueues a completed buffer into the writeQueue for transfer.
        /// </summary>
        /// <param name="completedBuffer">The completed buffer which should be written down</param>
        private unsafe void EnqueueCompletedBuffer(DynamicBuffer completedBuffer)
        {
            // pushing the completed buffer into the file stream
            writeQueue.WaitAsync().ContinueWith((_) =>
            {
                uint bytesWritten;

                int result = Native.Kernel32.WriteFile(fileHandle, completedBuffer.GetPointer(), completedBuffer.size, out bytesWritten, null);

                if (result != 1 || bytesWritten != completedBuffer.size)
                {
                    throw new IOException("write failed! bytes written: " + bytesWritten, new Win32Exception(Marshal.GetLastWin32Error()));
                }

                length += completedBuffer.size;

                ReturnBuffer(completedBuffer);

                writeQueue.Release();
                bufferAmountRestrictionSemaphore.Release();
            }, TaskScheduler.Current).ContinueWith((task) => { throw task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Current);
        }

        /// <summary>
        /// Unallocates and disposes all allocated buffers.
        /// </summary>
        private void UnallocateBuffers()
        {

            hotBuffer?.Dispose();
            hotBuffer = null;

            swappedHotBuffer?.Dispose();
            swappedHotBuffer = null;

            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null) buffers[i].Dispose();
            }
        }

        /// <summary>
        /// Simple method to check if the passed value is a power of two.
        /// </summary>
        private bool IsPowerOfTwo(int value)
        {
            return (value & (value - 1)) == 0;
        }
        #endregion

        #region Public methods
        public void Wait()
        {
            for (int i = 0; i < currentBufferMode.maxBufferAmount; i++)
            {
                bufferAmountRestrictionSemaphore.Wait();
            }

            for (int i = 0; i < currentBufferMode.maxBufferAmount; i++)
            {
                bufferAmountRestrictionSemaphore.Release();
            }
        }

        /// <summary>
        /// Returns the default buffer mode setup
        /// </summary>
        /// <returns>The buffer mode array</returns>
        public static BufferMode[] GetDefaultBufferModes()
        {
            return new BufferMode[] {
                new BufferMode(524288, 1, false, 1500, true),
                new BufferMode(1048576, 2, false, 1500),
                new BufferMode(2097152, 2, false, 2000),
                new BufferMode(4194304, 4, false, 3000),
                new BufferMode(8388608, 4, false, 5000),
                new BufferMode(16777216, 4, true, 7500)
            };
        }
        #endregion

        #region Constructors
        public DynamicBufferStream(string dstFilePath, BufferMode[] bufferModes, byte bufferStartPos, bool performanceDetection, int minClusterSize) : this(
            Native.Kernel32.CreateFile(
                dstFilePath,
                0x80000000 | 0x40000000,
                (uint)FileShare.ReadWrite,
                IntPtr.Zero,
                (uint)FileMode.Create,
                Native.Kernel32.FILE_FLAG_NO_BUFFERING | Native.Kernel32.FILE_FLAG_SEQUENTIAL_SCAN | Native.Kernel32.FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero),
            bufferModes, bufferStartPos, performanceDetection, minClusterSize)
        {
            closeFileHandleOnDispose = true;
        }

        public DynamicBufferStream(string dstFilePath, BufferMode[] bufferModes, byte bufferStartPos, bool performanceDetection) : this(dstFilePath, bufferModes, bufferStartPos, performanceDetection, 4096) { }

        public DynamicBufferStream(string dstFilePath, BufferMode[] bufferModes, byte bufferStartPos) : this(dstFilePath, bufferModes, bufferStartPos, true, 4096) { }

        public DynamicBufferStream(string dstFilePath) : this(dstFilePath, GetDefaultBufferModes(), 2, true, 4096) { }


        public DynamicBufferStream(SafeFileHandle fileHandle) : this(fileHandle, GetDefaultBufferModes(), 2, true, 4096) { }
        public DynamicBufferStream(IntPtr filePointer) : this(new SafeFileHandle(filePointer, false)) { }


        public DynamicBufferStream(SafeFileHandle fileHandle, BufferMode[] bufferModes, byte bufferStartPos, bool performanceDetection, int minClusterSize)
        {
            #region Validity checks
            if (fileHandle == null) throw new ArgumentNullException("The file handle must not be null!");
            if (fileHandle.IsInvalid) throw new ArgumentException("The file handle is invalid!");
            if (bufferModes == null) throw new ArgumentNullException("The bufferModes must not be null!");
            if (bufferModes.Length == 0) throw new ArgumentException("The bufferModes must be not empty!");
            if (bufferModes.Length == 1 && performanceDetection) throw new ArgumentException("Cannot execute performace detection if the is only one buffer mode specified!");
            if (minClusterSize == 0 || !IsPowerOfTwo(minClusterSize)) throw new ArgumentException("The minClusterSize must be a power of 2!");

            byte highestBufferAmount = 0;

            for (int i = 0; i < bufferModes.Length; i++)
            {
                if (bufferModes[i].bufferSize == 0 || !IsPowerOfTwo(bufferModes[i].bufferSize)) throw new ArgumentException($"The buffer size must be a power of 2! (buffer mode at index {i})");
                if (bufferModes[i].maxBufferAmount == 0) throw new ArgumentException($"The max allowed amount of concurrent buffers must be greater than zero! (buffer mode at index {i})");
                if (bufferModes[i].performanceDetectionInterval == 0) throw new ArgumentException($"The performance detection interval must be greater than zero! (buffer mode at index {i})");
                if (bufferModes[i].directWriteThrough && bufferModes[i].maxBufferAmount > 1) throw new ArgumentException($"The can be only one direct write through buffer! (buffer mode at index {i})");

                if (i != 0 && bufferModes[i].bufferSize * bufferModes[i].maxBufferAmount <= bufferModes[i - 1].bufferSize * bufferModes[i - 1].maxBufferAmount)
                {
                    throw new ArgumentException($"The buffer mode array ist not arranged correctly! Buffer mode at index {i} has a smaller or equal capacity as the buffer mode at index {i - 1}!");
                }

                if (highestBufferAmount < bufferModes[i].maxBufferAmount) highestBufferAmount = bufferModes[i].maxBufferAmount;
            }
            #endregion

            this.fileHandle = fileHandle;
            this.bufferModes = bufferModes;
            this.MinClusterSize = minClusterSize;

            currentBufferMode = bufferModes[bufferStartPos];
            currentBufferModeIndex = bufferStartPos;

            hotBuffer = new DynamicBuffer(bufferModes[bufferStartPos].bufferSize, false);

            if (bufferModes[bufferStartPos].diskBuffer) swappedHotBuffer = new DynamicBuffer(bufferModes[bufferStartPos].bufferSize, false);

            if (performanceDetection)
            {
                performanceDetectionTrigger = new Timer(EvaluatePerformace, null, bufferModes[bufferStartPos].performanceDetectionInterval, bufferModes[bufferStartPos].performanceDetectionInterval);
                bufferLoadSamplerTrigger = new Timer(BufferLoadSampler, null, 500, 250);
            }

            buffers = new DynamicBuffer[highestBufferAmount];
            bufferAmountRestrictionSemaphore = new SemaphoreSlim(bufferModes[bufferStartPos].maxBufferAmount, highestBufferAmount);
            bufferAmountRestrictionSemaphoreHardlocked = highestBufferAmount - bufferModes[bufferStartPos].maxBufferAmount;

            workSemaphore.Wait();
        }
        public DynamicBufferStream(IntPtr filePointer, BufferMode[] bufferModes, byte bufferStartPos, bool performanceDetection, int minClusterSize) : this(new SafeFileHandle(filePointer, false), bufferModes, bufferStartPos, performanceDetection, minClusterSize) { }


        public DynamicBufferStream(SafeFileHandle fileHandle, BufferMode[] bufferModes, byte bufferStartPos) : this(fileHandle, bufferModes, bufferStartPos, true, 4096) { }
        public DynamicBufferStream(IntPtr filePointer, BufferMode[] bufferModes, byte bufferStartPos) : this(new SafeFileHandle(filePointer, false), bufferModes, bufferStartPos) { }


        public DynamicBufferStream(SafeFileHandle fileHandle, BufferMode[] bufferModes, byte bufferStartPos, bool performanceDetection) : this(fileHandle, bufferModes, bufferStartPos, performanceDetection, 4096) { }
        public DynamicBufferStream(IntPtr filePointer, BufferMode[] bufferModes, byte bufferStartPos, bool performanceDetection) : this(new SafeFileHandle(filePointer, false), bufferModes, bufferStartPos, performanceDetection) { }


        public DynamicBufferStream(SafeFileHandle fileHandle, BufferMode[] bufferModes, byte bufferStartPos, int minClusterSize) : this(fileHandle, bufferModes, bufferStartPos, true, minClusterSize) { }
        public DynamicBufferStream(IntPtr filePointer, BufferMode[] bufferModes, byte bufferStartPos, int minClusterSize) : this(new SafeFileHandle(filePointer, false), bufferModes, bufferStartPos, minClusterSize) { }

        #endregion

        #region Stream Class required variables
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => length;

        public override long Position { get => length; set => throw new NotImplementedException(); }
        #endregion

        #region Stream class required methods
        #region Not implementet
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }
        #endregion

        /// <summary>
        /// Flushes the last buffered bytes to disk.
        /// </summary>
        public unsafe override void Flush()
        {
            gotFlushed = true;

            if (hotBufferOffset > 0)
            {
                uint bytesWritten;

                int finalBlockSize = MinClusterSize;

                for (double i = 0; hotBufferOffset >= finalBlockSize; i++)
                {
                    finalBlockSize = (int)Math.Pow(2d, i);
                }

                IntPtr finalBlock = Marshal.AllocHGlobal(finalBlockSize);

                Native.Msvcrt.memcpy(finalBlock, hotBuffer.GetPointer(), new UIntPtr((uint)hotBufferOffset));

                Native.Kernel32.RtlZeroMemory(finalBlock + hotBufferOffset, (uint)(finalBlockSize - hotBufferOffset));

                int result = Native.Kernel32.WriteFile(fileHandle, finalBlock, finalBlockSize, out bytesWritten, null);

                Marshal.FreeHGlobal(finalBlock);

                if (result != 1 || bytesWritten != finalBlockSize)
                {
                    throw new IOException("flush failed! bytes written: " + bytesWritten);
                }

                length += hotBufferOffset;
            }
        }

        /// <summary>
        /// The main logic where a given buffer gets buffered and later enqueued.
        /// </summary>
        /// <remarks>
        /// If the currentBuffer mode specifies disk buffers, the completed buffer preparing will happen async and the hotBuffer will be swapped with swappedHotBuffer.
        /// If the currentBuffer mode specifies direct write through, the data will be buffered still in the hotBuffer but there is no completed buffer preparing and the hotBuffer will be directly written down.
        /// </remarks>
        /// <param name="buffer">A buffer that should be written</param>
        /// <param name="offset">The offset</param>
        /// <param name="count">The buffer capacity</param>
        public unsafe override void Write(byte[] buffer, int offset, int count)
        {
            if (gotFlushed) throw new Exception("This stream got flushed and is therefore no longer writeable!");

            if (performanceEvaluationRunning)
            {
                if (currentBufferMode.directWriteThrough) bufferAmountRestrictionSemaphore.AvailableWaitHandle.WaitOne();

                workSemaphore.Release();
                performanceEvaluationSemaphore.AvailableWaitHandle.WaitOne();
                workSemaphore.Wait();
            }

            if (hotBufferOffset + count < hotBuffer.size)
            {
                fixed (byte* bufferPointer = buffer)
                {
                    hotBuffer.Write(hotBufferOffset, (IntPtr)bufferPointer, count);

                    hotBufferOffset += count;
                }
            }
            else
            {
                if (!currentBufferMode.directWriteThrough)
                {
                    bufferAmountRestrictionSemaphore.Wait();

                    DynamicBuffer completedBuffer = RentFreeBuffer();

                    if (currentBufferMode.diskBuffer)
                    {
                        swappedHotBufferSemaphore.Wait();

                        DynamicBuffer hotBufferTempRef = hotBuffer;

                        // setting the active hot buffer to the old unused swappedHotBuffer
                        hotBuffer = swappedHotBuffer;

                        // the swappedHotBuffer will be the full and active hotBuffer.
                        swappedHotBuffer = hotBufferTempRef;
                        swappedHotBuffer.isInUse = true;

                        int hotBufferOffsetTemp = hotBufferOffset;

                        Task.Run(() =>
                        {
                            fixed (byte* bufferPointer = buffer)
                            {
                                swappedHotBuffer.Write(hotBufferOffsetTemp, (IntPtr)bufferPointer, hotBuffer.size - hotBufferOffsetTemp);
                            }

                            completedBuffer.Write(0, swappedHotBuffer.GetPointer(), swappedHotBuffer.size);

                            // enqueuing the completed buffer
                            EnqueueCompletedBuffer(completedBuffer);

                            swappedHotBuffer.isInUse = false;
                            swappedHotBufferSemaphore.Release();
                        }).ContinueWith((task) => { throw task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Current);
                    }
                    else
                    {
                        fixed (byte* bufferPointer = buffer)
                        {
                            // copy hot buffer
                            completedBuffer.Write(0, hotBuffer.GetPointer(), hotBufferOffset);

                            // copy new buffer
                            completedBuffer.Write(hotBufferOffset, (IntPtr)bufferPointer, hotBuffer.size - hotBufferOffset);
                        }

                        // enqueuing the completed buffer
                        EnqueueCompletedBuffer(completedBuffer);
                    }

                    // copying remaining bytes of the new buffer
                    fixed (byte* bufferPointer = buffer)
                    {
                        hotBuffer.Write(0, ((IntPtr)bufferPointer) + (hotBuffer.size - hotBufferOffset), count - (hotBuffer.size - hotBufferOffset));
                    }

                    // setting the new offset of the hot buffer
                    hotBufferOffset = count - (hotBuffer.size - hotBufferOffset);
                }
                else
                {
                    // with direct write through we are not locking any spaces in the bufferAmountRestrictionSemaphore.
                    // to keep the bufferSampler collecting the info that we have an buffer load of one buffer, were incrementing the bufferLoad variable.

                    void WriteThrough(IntPtr bufferToWrite, int bufferSize)
                    {
                        uint bytesWritten;

                        int result = Native.Kernel32.WriteFile(fileHandle, bufferToWrite, bufferSize, out bytesWritten, null);

                        if (result != 1 || bytesWritten != bufferSize)
                        {
                            throw new IOException("write failed! bytes written: " + bytesWritten);
                        }

                        length += bufferSize;
                    }

                    if (hotBufferOffset == 0 && count == hotBuffer.size)
                    {
                        fixed (byte* writeReadyBuffer = buffer)
                        {
                            bufferLoad++;
                            WriteThrough((IntPtr)writeReadyBuffer, buffer.Length);
                            bufferLoad--;
                        }
                    }
                    else
                    {
                        bufferLoad++;

                        fixed (byte* bufferPointerInsideAction = buffer)
                        {
                            hotBuffer.Write(hotBufferOffset, (IntPtr)bufferPointerInsideAction, hotBuffer.size - hotBufferOffset);
                        }

                        WriteThrough(hotBuffer.GetPointer(), hotBuffer.size);

                        fixed (byte* bufferPointerInsideAction = buffer)
                        {
                            hotBuffer.Write(0, ((IntPtr)bufferPointerInsideAction) + (hotBuffer.size - hotBufferOffset), count - (hotBuffer.size - hotBufferOffset));
                        }

                        // setting the new offset of the hot buffer
                        hotBufferOffset = count - (hotBuffer.size - hotBufferOffset);

                        bufferLoad--;
                    }
                }

            }

        }


        public override void Close()
        {
            base.Close();
        }
        /// <summary>
        /// Flushes remaining bytes, unallocates all buffers and disposes all intern disposeable classes.
        /// </summary>
        protected unsafe override void Dispose(bool disposing)
        {
            if (!disposing || IsDisposed) return;

            Wait();

            IsDisposed = true;

            Flush();
            UnallocateBuffers();

            performanceEvaluationSemaphore?.Dispose();
            performanceDetectionTrigger?.Dispose();
            bufferLoadSamplerTrigger?.Dispose();
            workSemaphore.Dispose();
            bufferAmountRestrictionSemaphore.Dispose();
            writeQueue.Dispose();

            if (closeFileHandleOnDispose) fileHandle.Dispose();

            base.Dispose(disposing);
        }
        #endregion
    }
}
