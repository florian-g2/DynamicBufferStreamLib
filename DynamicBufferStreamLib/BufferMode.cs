namespace DynamicBufferStreamLib
{
    /// <summary>
    /// Struct that represents a buffer mode.
    /// </summary>
    /// <value>Property <c>bufferSize</c> defines the buffer size in bytes.</value>
    /// <value>Property <c>maxBufferAmount</c> defines the max amount of concurrent initialized buffers.</value>
    public struct BufferMode
    {
        public static BufferMode Custom = new BufferMode(0, 0, false);

        public int bufferSize;
        public byte maxBufferAmount;
        public bool diskBuffer;
        public int performanceDetectionInterval;
        public bool directWriteThrough;

        public BufferMode(int bufferSize, byte maxBufferAmount, bool diskBuffer)
        {
            this.bufferSize = bufferSize;
            this.maxBufferAmount = maxBufferAmount;
            this.diskBuffer = diskBuffer;
            directWriteThrough = false;
            performanceDetectionInterval = 5000;
        }

        public BufferMode(int bufferSize, byte maxBufferAmount, bool diskBuffer, int performanceDetectionInterval)
        {
            this.bufferSize = bufferSize;
            this.maxBufferAmount = maxBufferAmount;
            this.diskBuffer = diskBuffer;
            this.performanceDetectionInterval = performanceDetectionInterval;
            directWriteThrough = false;
        }

        public BufferMode(int bufferSize, byte maxBufferAmount, bool diskBuffer, int performanceDetectionInterval, bool directWriteThrough)
        {
            this.bufferSize = bufferSize;
            this.maxBufferAmount = maxBufferAmount;
            this.diskBuffer = diskBuffer;
            this.performanceDetectionInterval = performanceDetectionInterval;
            this.directWriteThrough = directWriteThrough;
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
            {
                return false;
            }

            BufferMode objectToCompareWith = (BufferMode)obj;

            return objectToCompareWith.bufferSize == bufferSize &&
                   objectToCompareWith.maxBufferAmount == maxBufferAmount &&
                   objectToCompareWith.diskBuffer == diskBuffer;
        }

        public static bool operator ==(BufferMode mode1, BufferMode mode2)
        {
            return mode1.Equals(mode2);
        }

        public static bool operator !=(BufferMode mode1, BufferMode mode2)
        {
            return !mode1.Equals(mode2);
        }

        public override int GetHashCode()
        {
            return -1;
        }

        public override string ToString()
        {
            return $"bufferSize: {bufferSize}, maxBufferAmount: {maxBufferAmount}, diskBuffer: {diskBuffer}, performanceDetectionInterval: {performanceDetectionInterval}, directWriteThrough: {directWriteThrough}";
        }
    }
}
