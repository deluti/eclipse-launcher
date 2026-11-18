using System.Security.Cryptography;
using System.Text;

namespace Eclipse;

public sealed class OfflineAccount
{
    public string Name { get; }
    public string Uuid { get; }
    private OfflineAccount(string name, string uuid) { Name = name; Uuid = uuid; }

    public static OfflineAccount Create(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 3 or > 16 || name.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            throw new InvalidOperationException(L.T("Никнейм: 3–16 символов, латинские буквы, цифры и _"));
        // Match Java UUID.nameUUIDFromBytes for an offline Minecraft profile.
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new OfflineAccount(name, Convert.ToHexString(bytes).ToLowerInvariant());
    }
}
