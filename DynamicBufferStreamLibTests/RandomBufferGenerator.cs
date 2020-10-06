using System;
using System.Collections.Generic;
using System.Text;

namespace DynamicBufferStreamLibTests
{
    internal class RandomBufferGenerator
    {
        private readonly Random _random = new Random();
        private readonly byte[] _seedBuffer;

        private byte[] buffer;
        private int currentBufferSize;


        public RandomBufferGenerator(int maxBufferSize)
        {
            _seedBuffer = new byte[maxBufferSize];

            _random.NextBytes(_seedBuffer);
        }

        public byte[] GenerateBufferFromSeed(int size)
        {
            int randomWindow = _random.Next(0, size);

            if (currentBufferSize != size)
            {
                buffer = new byte[size];
                currentBufferSize = size;
            }

            Buffer.BlockCopy(_seedBuffer, randomWindow, buffer, 0, size - randomWindow);
            Buffer.BlockCopy(_seedBuffer, 0, buffer, size - randomWindow, randomWindow);

            return buffer;
        }
    }
}
