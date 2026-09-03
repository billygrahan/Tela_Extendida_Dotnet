using System.IO;

namespace Shared;

public class FrameHeader
{
    public ushort RectCount { get; set; }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(RectCount);
    }

    public static FrameHeader Deserialize(BinaryReader reader)
    {
        return new FrameHeader
        {
            RectCount = reader.ReadUInt16()
        };
    }
}

public struct RectHeader
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CompressedDataSize { get; set; }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(CompressedDataSize);
    }

    public static RectHeader Deserialize(BinaryReader reader)
    {
        return new RectHeader
        {
            X = reader.ReadInt32(),
            Y = reader.ReadInt32(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32(),
            CompressedDataSize = reader.ReadInt32()
        };
    }
}