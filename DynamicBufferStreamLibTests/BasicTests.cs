using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using DynamicBufferStreamLib;
using Brotli;

namespace DynamicBufferStreamLibTests
{
    [TestClass]
    public class BasicTests
    {
        /// <summary>
        ///  Gets or sets the test context which provides
        ///  information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext { get; set; }

        private string testFilesPath;
        private string testProjectPath;
        private string testCompressedFilePath;

        [TestInitialize]
        public void PrepareTestFiles()
        {
            int testsProjectsIndex = AppDomain.CurrentDomain.BaseDirectory.IndexOf("DynamicBufferStreamLibTests", StringComparison.OrdinalIgnoreCase);
            testProjectPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory.Substring(0, testsProjectsIndex), "DynamicBufferStreamLibTests");

            testFilesPath = Path.Combine(testProjectPath, "TestEnvironment", "TestFiles") + '\\';

            if (Directory.Exists(testFilesPath))
            {
                if (Directory.EnumerateFiles(testFilesPath).Count() == 760) return;

                Directory.Delete(testFilesPath, true);
            }

            Directory.CreateDirectory(testFilesPath);

            #region Generating test files
            RandomBufferGenerator randomBufferGenerator = new RandomBufferGenerator(25000000);

            #region generate 500 100kb files
            for (int i = 0; i < 500; i++)
            {
                using (FileStream fileStream = File.Create(testFilesPath + "file100kb-" + i))
                {
                    using (BinaryWriter binaryWriter = new BinaryWriter(fileStream))
                    {
                        binaryWriter.Write(randomBufferGenerator.GenerateBufferFromSeed(100000));
                    }
                }
            }
            #endregion

            #region generate 250 3mb files
            for (int i = 0; i < 250; i++)
            {
                using (FileStream fileStream = File.Create(testFilesPath + "file3mb-" + i))
                {
                    using (BinaryWriter binaryWriter = new BinaryWriter(fileStream))
                    {
                        binaryWriter.Write(randomBufferGenerator.GenerateBufferFromSeed(3000000));
                    }
                }
            }
            #endregion

            #region generate 10 25mb files
            for (int i = 0; i < 10; i++)
            {
                using (FileStream fileStream = File.Create(testFilesPath + "file25mb-" + i))
                {
                    using (BinaryWriter binaryWriter = new BinaryWriter(fileStream))
                    {
                        binaryWriter.Write(randomBufferGenerator.GenerateBufferFromSeed(25000000));
                    }
                }
            }
            #endregion
            #endregion
        }

        [TestCleanup]
        public void RemoveTestFiles()
        {
            File.Delete(testCompressedFilePath);
        }

        public void StartCompression(Stream stream)
        {
            BrotliStream brotliStream = new BrotliStream(stream, System.IO.Compression.CompressionMode.Compress, true);

            foreach (string filePath in Directory.GetFiles(testFilesPath, "*"))
            {
                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None))
                {
                    fileStream.CopyTo(brotliStream);
                }
            }

            brotliStream.Dispose();
        }

        public void TestDefault(byte bufferStartMode, bool performaceDetection)
        {
            testCompressedFilePath = Path.Combine(testProjectPath, "TestEnvironment", "testCompressedFile.br");

            DynamicBufferStream dynamicBufferStream = new DynamicBufferStream(testCompressedFilePath, DynamicBufferStream.GetDefaultBufferModes(), bufferStartMode, performaceDetection);
            dynamicBufferStream.OnBufferModeLowering += (_, bufferArgs) =>
            {

                TestContext.WriteLine($"bufferMode lowering ({bufferArgs.timeSpent}ms): {bufferArgs.previousBufferMode.bufferSize}x{bufferArgs.previousBufferMode.maxBufferAmount} => {bufferArgs.bufferMode.bufferSize}x{bufferArgs.bufferMode.maxBufferAmount}, previousAverageLoad: {bufferArgs.previousAverageBufferLoad}");
            };

            dynamicBufferStream.OnBufferModeRaise += (_, bufferArgs) =>
            {
                TestContext.WriteLine($"bufferMode raise ({bufferArgs.timeSpent}ms): {bufferArgs.previousBufferMode.bufferSize}x{bufferArgs.previousBufferMode.maxBufferAmount} => {bufferArgs.bufferMode.bufferSize}x{bufferArgs.bufferMode.maxBufferAmount}, previousAverageLoad: {bufferArgs.previousAverageBufferLoad}");
            };

            StartCompression(dynamicBufferStream);

            dynamicBufferStream.Wait();
            dynamicBufferStream.Dispose();
        }

        #region Default modes tests
        [TestMethod]
        [Description("Test on buffer size 65.536 with hot buffer direct write through.")]
        public void TestDefaultBufferMode0()
        {
            TestDefault(0, false);
        }

        [TestMethod]
        [Description("Test on buffer size 1.048.576")]
        public void TestDefaultBufferMode3()
        {
            TestDefault(2, false);
        }

        [TestMethod]
        [Description("Test on buffer size 16.777.216 with hot buffer swapping and disk buffering")]
        public void TestDefaultBufferMode6()
        {
            TestDefault(5, false);
        }
        #endregion

        #region Default modes tests with performance detection
        [TestMethod]
        [Description("Test on start buffer size 65.536 with hot buffer direct write through and performance detection.")]
        public void TestDefaultBufferMode0WithPerformanceDetection()
        {
            TestDefault(0, true);
        }

        [TestMethod]
        [Description("Test on start buffer size 1.048.576 with performance detection.")]
        public void TestDefaultBufferMode3WithPerformanceDetection()
        {
            TestDefault(2, true);
        }

        [TestMethod]
        [Description("Test on start buffer size 16.777.216 with hot buffer swapping, disk buffering and performance detection.")]
        public void TestDefaultBufferMode6WithPerformanceDetection()
        {
            TestDefault(5, true);
        }
        #endregion

        /*[TestMethod]
        public void TestWith()
        {
            testCompressedFilePath = Path.Combine(testProjectPath, "TestEnvironment", "testCompressedFile.br");

            FileStream conventionalFileStream = File.Create(testCompressedFilePath);

            BrotliStream brotliStream = new BrotliStream(conventionalFileStream, System.IO.Compression.CompressionMode.Compress, true);

            foreach (string filePath in Directory.GetFiles(testFilesPath, "*"))
            {
                using (FileStream fileStream = File.OpenRead(filePath))
                {
                    fileStream.CopyTo(brotliStream);
                }
            }

            brotliStream.Dispose();
            conventionalFileStream.Dispose();
        }*/
    }
}
