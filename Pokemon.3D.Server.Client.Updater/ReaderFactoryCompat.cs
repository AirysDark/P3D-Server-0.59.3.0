// ReaderFactoryCompat.cs
using System;
using System.IO;
using SharpCompress.Readers;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SharpCompress.Readers
{
    public static class ReaderFactory
    {
        public static IReader Open(Stream stream)
        {
            // Automatically detect the archive type and return a reader
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            return ReaderFactoryBase.Open(stream);
        }
    }

    internal static class ReaderFactoryBase
    {
        public static IReader Open(Stream stream)
        {
            try
            {
                return SharpCompress.Readers.ReaderFactory.Open(stream);
            }
            catch
            {
                // Fallback for older SharpCompress versions
                return ArchiveFactory.Open(stream).ExtractAllEntries();
            }
        }
    }
}