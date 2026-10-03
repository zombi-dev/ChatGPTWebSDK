using System.Buffers.Binary;

namespace ChatGPTWebSdk.Compatibility;

internal static class ImageDimensions
{
    public static (int Width, int Height) Read(byte[] data, string mime)
    {
        if (mime == "image/png" && data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        if (mime == "image/jpeg" && data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            int position = 2;
            while (position + 4 < data.Length)
            {
                if (data[position] != 0xff) break;
                var marker = data[position + 1];
                if (marker is 0xd8 or 0xd9) { position += 2; continue; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2, 2));
                if (length < 2 || position + length + 2 > data.Length) break;
                if (marker is >= 0xc0 and <= 0xc3 && length >= 7)
                    return (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 7, 2)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 5, 2)));
                position += length + 2;
            }
        }
        throw new ArgumentException("The image header must describe a PNG or JPEG image.");
    }
}
