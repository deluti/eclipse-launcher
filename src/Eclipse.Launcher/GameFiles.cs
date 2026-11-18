using System.Security.Cryptography;
using System.Text;

namespace Eclipse;

public sealed record EditableGameFile(string Path, string Text, Encoding Encoding, byte[] OriginalHash, bool Bom);

public static class GameFiles
{
    private const int MaximumTextBytes = 2 * 1024 * 1024;
    public static string ValidatePath(string root, string path)
    {
        var full = AppPaths.SafeChild(root, System.IO.Path.GetRelativePath(root, path));
        var cursor = full;
        while (cursor.Length >= System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar).Length)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("Ссылки на внешние папки не открываются."));
            var parent = System.IO.Path.GetDirectoryName(cursor);
            if (parent == null || parent == cursor) break;
            cursor = parent;
        }
        return full;
    }
    public static EditableGameFile Read(string root, string path)
    {
        path = ValidatePath(root, path);
        if (new FileInfo(path).Length > MaximumTextBytes) throw new IOException(L.T("Файл больше 2 МБ. Откройте его во внешнем редакторе."));
        var bytes = File.ReadAllBytes(path);
        Encoding encoding = new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = Encoding.Unicode; skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = Encoding.BigEndianUnicode; skip = 2; }
        else if (bytes.Contains((byte)0)) throw new IOException(L.T("Бинарный файл"));
        try
        {
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            if (text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t'))) throw new IOException(L.T("Бинарный файл"));
            return new(path, text, encoding, SHA256.HashData(bytes), skip > 0);
        }
        catch (DecoderFallbackException) { throw new IOException(L.T("Неизвестная кодировка. Откройте файл во внешнем редакторе.")); }
    }
    public static EditableGameFile Save(string root, EditableGameFile document, string text)
    {
        var path = ValidatePath(root, document.Path);
        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(document.OriginalHash))
            throw new IOException(L.T("Файл изменён вне лаунчера. Откройте его заново перед сохранением."));
        var data = document.Encoding.GetBytes(text);
        var preamble = document.Bom ? document.Encoding.CodePage == 65001 ? new byte[] { 0xef, 0xbb, 0xbf } : document.Encoding.GetPreamble() : [];
        if (data.Length + preamble.Length > MaximumTextBytes) throw new IOException(L.T("Текст превышает 2 МБ. Файл не изменён."));
        var temporary = path + ".eclipse-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { stream.Write(preamble); stream.Write(data); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Read(root, path);
    }
}
