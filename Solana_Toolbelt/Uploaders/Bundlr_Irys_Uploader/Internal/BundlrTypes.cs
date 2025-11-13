using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Solana.Unity.Toolbelt.Internal
{
    public readonly struct BundlrTag
    {
        public string Name { get; }
        public string Value { get; }

        public BundlrTag(string name, string value)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value ?? string.Empty;
        }
    }

    internal static class BundlrTagSerializer
    {
        private const int MaxTagBytes = 4096;

        public static byte[] Serialize(IReadOnlyList<BundlrTag> tags)
        {
            if (tags == null || tags.Count == 0)
                return Array.Empty<byte>();

            using var stream = new MemoryStream();
            WriteLong(stream, tags.Count);
            foreach (var tag in tags)
            {
                WriteString(stream, tag.Name);
                WriteString(stream, tag.Value);
            }
            WriteLong(stream, 0);

            if (stream.Length > MaxTagBytes)
                throw new InvalidOperationException($"Bundlr tags exceed {MaxTagBytes} bytes after serialization.");

            return stream.ToArray();
        }

        private static void WriteString(Stream stream, string value)
        {
            value ??= string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteLong(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteLong(Stream stream, long value)
        {
            ulong zigZag = (ulong)((value << 1) ^ (value >> 63));
            while ((zigZag & ~0x7FUL) != 0)
            {
                stream.WriteByte((byte)((zigZag & 0x7F) | 0x80));
                zigZag >>= 7;
            }
            stream.WriteByte((byte)zigZag);
        }
    }

    internal static class Base64UrlUtility
    {
        public static string Encode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            string base64 = Convert.ToBase64String(data);
            return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }

    internal static class Base58Utility
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        private static readonly int[] AlphabetIndexes = BuildIndexes();

        public static byte[] Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<byte>();

            int zeros = 0;
            while (zeros < input.Length && input[zeros] == '1')
                zeros++;

            var input58 = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c >= 128 || AlphabetIndexes[c] < 0)
                    throw new FormatException($"Invalid Base58 character '{c}' at position {i}.");
                input58[i] = (byte)AlphabetIndexes[c];
            }

            var decoded = new byte[input.Length];
            int outputStart = decoded.Length;
            int inputStart = zeros;
            while (inputStart < input58.Length)
            {
                int remainder = 0;
                for (int i = inputStart; i < input58.Length; i++)
                {
                    int digit = input58[i];
                    int temp = remainder * 58 + digit;
                    input58[i] = (byte)(temp / 256);
                    remainder = temp % 256;
                }

                decoded[--outputStart] = (byte)remainder;
                while (inputStart < input58.Length && input58[inputStart] == 0)
                    inputStart++;
            }

            while (outputStart < decoded.Length && decoded[outputStart] == 0)
                outputStart++;

            var result = new byte[zeros + decoded.Length - outputStart];
            for (int i = 0; i < zeros; i++)
                result[i] = 0;
            Buffer.BlockCopy(decoded, outputStart, result, zeros, decoded.Length - outputStart);
            return result;
        }

        public static string Encode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            int zeros = 0;
            while (zeros < data.Length && data[zeros] == 0)
                zeros++;

            var input = new byte[data.Length];
            Buffer.BlockCopy(data, 0, input, 0, data.Length);

            var encoded = new char[data.Length * 2];
            int outputStart = encoded.Length;
            int inputStart = zeros;
            while (inputStart < input.Length)
            {
                int remainder = 0;
                for (int i = inputStart; i < input.Length; i++)
                {
                    int value = (input[i] & 0xFF);
                    int temp = remainder * 256 + value;
                    input[i] = (byte)(temp / 58);
                    remainder = temp % 58;
                }

                encoded[--outputStart] = Alphabet[remainder];
                while (inputStart < input.Length && input[inputStart] == 0)
                    inputStart++;
            }

            while (zeros-- > 0)
                encoded[--outputStart] = '1';

            return new string(encoded, outputStart, encoded.Length - outputStart);
        }

        private static int[] BuildIndexes()
        {
            var indexes = new int[128];
            for (int i = 0; i < indexes.Length; i++)
                indexes[i] = -1;
            for (int i = 0; i < Alphabet.Length; i++)
                indexes[Alphabet[i]] = i;
            return indexes;
        }
    }
}
