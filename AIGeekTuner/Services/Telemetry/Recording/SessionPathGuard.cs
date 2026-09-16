using System.IO;

namespace AIGeekTuner.Services.Telemetry.Recording;

/// <summary>
/// Shared boundary for all Sessions/{id}/... paths. Session IDs are opaque
/// file-name components, not paths; the persisted application currently emits
/// compact GUIDs, while the broader safe test/catalog IDs remain supported.
/// </summary>
internal static class SessionPathGuard
{
    private const int MaxSessionIdLength = 128;

    public static bool TryNormalizeId(string? sessionId, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId != sessionId.Trim()
            || sessionId.Length > MaxSessionIdLength
            || sessionId is "." or ".."
            || Path.IsPathRooted(sessionId)
            || sessionId.IndexOfAny(['/', '\\']) >= 0
            || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || IsWindowsReservedName(sessionId)
            || sessionId.Any(character =>
                !(character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
        {
            return false;
        }

        normalized = sessionId;
        return true;
    }

    public static bool TryGetSessionDirectory(
        string rootDirectory,
        string? sessionId,
        out string directory)
    {
        directory = string.Empty;
        if (!TryNormalizeId(sessionId, out var normalized))
        {
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(rootDirectory);
            candidate = Path.GetFullPath(Path.Combine(root, normalized));
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return false;
        }

        if (!IsWithinRoot(root, candidate)
            || !IsSafeManagedRoot(root)
            || !IsSafeSessionDirectory(candidate))
        {
            return false;
        }

        directory = candidate;
        return true;
    }

    public static bool IsWithinRoot(string rootDirectory, string candidatePath)
    {
        try
        {
            var root = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(
                    root + Path.AltDirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return false;
        }
    }

    /// <summary>构造 managed store 时拒绝把整个 Sessions 根放在 reparse point 上。</summary>
    public static string RequireSafeRootDirectory(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        if (!IsSafeManagedRoot(root))
        {
            throw new IOException("Sessions 根目录不能是 Windows reparse point。");
        }

        return root;
    }

    /// <summary>
    /// Delete(recursive:true) 前的 fail-closed 检查。发现任意 reparse point
    /// 就拒绝操作，避免递归删除外部 junction/symlink 目标。
    /// </summary>
    public static bool ContainsReparsePoint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (HasReparsePoint(current))
            {
                return true;
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (HasReparsePoint(entry))
                    {
                        return true;
                    }

                    if (Directory.Exists(entry))
                    {
                        pending.Push(entry);
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeManagedRoot(string root) =>
        (!File.Exists(root) && !Directory.Exists(root))
        || (Directory.Exists(root) && !HasReparsePoint(root));

    private static bool IsSafeSessionDirectory(string directory)
    {
        if (File.Exists(directory))
        {
            return false;
        }

        if (!Directory.Exists(directory))
        {
            return true;
        }

        if (HasReparsePoint(directory))
        {
            return false;
        }

        try
        {
            // Fixed store files must not be links to an external file either.
            // Only inspect direct children here; recursive deletion uses the
            // stronger ContainsReparsePoint check above.
            return !Directory.EnumerateFileSystemEntries(directory)
                .Any(HasReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Security checks fail closed when attributes cannot be read.
            return true;
        }
    }

    private static bool IsWindowsReservedName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || value.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Length == 4
            && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && value[3] is >= '1' and <= '9';
    }

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException
            or IOException
            or NotSupportedException;

    public static string RequireSessionDirectory(string rootDirectory, string sessionId)
    {
        if (!TryGetSessionDirectory(rootDirectory, sessionId, out var directory))
        {
            throw new ArgumentException("会话 ID 不是合法的会话标识。", nameof(sessionId));
        }

        return directory;
    }
}
