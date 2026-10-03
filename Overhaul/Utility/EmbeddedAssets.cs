using System;
using System.IO;
using UnityEngine;

namespace Overhaul.Utility
{
    internal static class EmbeddedAssets
    {
        internal static AssetBundle LoadBundle(string resourceName)
        {
            using (var stream = typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidOperationException("Missing embedded asset bundle: " + resourceName);
                // Jotunn 2.24.3 LoadAssetBundleFromResources disposes its stream on return.
                // Unity may read a stream later, so use owned bytes for these embedded bundles.
                var bundle = AssetBundle.LoadFromMemory(ReadAll(stream));
                if (!bundle) throw new InvalidOperationException("Unable to load embedded asset bundle: " + resourceName);
                return bundle;
            }
        }

        internal static byte[] ReadAll(Stream stream)
        {
            var bytes = new byte[checked((int)(stream.Length - stream.Position))];
            int position = 0;
            while (position < bytes.Length)
            {
                int read = stream.Read(bytes, position, bytes.Length - position);
                if (read == 0) throw new EndOfStreamException("Incomplete embedded asset bundle");
                position += read;
            }
            return bytes;
        }
    }
}