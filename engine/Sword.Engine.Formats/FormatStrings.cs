using System.Text;

namespace Sword.Engine.Formats;

/// <summary>Чтение строк форматов движка.</summary>
internal static class FormatStrings
{
    /// <summary>
    /// Строка как у <c>BinaryWriter</c> (7-битная длина, UTF-8), но испорченная длина — <see cref="InvalidDataException"/>,
    /// а не IOException или FormatException, которые не отличить от ошибки чтения потока.
    /// </summary>
    public static string Read(BinaryReader r, string path, int maxBytes)
    {
        int length;
        try
        {
            length = r.Read7BitEncodedInt();
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"{path}: испорчена строка ({e.Message})", e);
        }
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException($"{path}: испорчена строка (длина {length})");
        byte[] bytes = new byte[length];
        r.BaseStream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
