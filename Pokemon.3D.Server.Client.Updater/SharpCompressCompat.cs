// SharpCompressCompat.cs
using System;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SharpCompress.Common
{
    [Flags]
    public enum ExtractOptions
    {
        None = 0,
        ExtractFullPath = 1,
        Overwrite = 2
    }

    internal static class ExtractOptionsCompat
    {
        public static ExtractionOptions ToExtractionOptions(ExtractOptions flags)
        {
            return new ExtractionOptions
            {
                ExtractFullPath = (flags & ExtractOptions.ExtractFullPath) != 0,
                Overwrite = (flags & ExtractOptions.Overwrite) != 0
            };
        }
    }
}

namespace SharpCompress.Readers
{
    public static class ReaderExtensionsCompat
    {
        public static void WriteEntryToDirectory(this IReader reader, string destination, SharpCompress.Common.ExtractOptions flags)
        {
            var options = SharpCompress.Common.ExtractOptionsCompat.ToExtractionOptions(flags);
            reader.WriteEntryToDirectory(destination, options);
        }
    }
}