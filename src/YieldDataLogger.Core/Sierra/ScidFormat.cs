using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace YieldDataLogger.Core.Sierra;

/// <summary>One Sierra Chart s_IntradayRecord (40 bytes). <see cref="Us"/> is µs since 1899-12-30 UTC.</summary>
public readonly record struct ScidRec(
    long Us, float Open, float High, float Low, float Close,
    uint Trades, uint Volume, uint BidVolume, uint AskVolume)
{
    /// <summary>A price-only tick: O=H=L=C, one trade, volume 1 (keeps volume-based studies sane).</summary>
    public static ScidRec Price(long us, double price)
    {
        var p = (float)price;
        return new ScidRec(us, p, p, p, p, 1, 1, 0, 0);
    }
}

/// <summary>
/// Sierra Chart intraday (.scid) file layout and the file handling every writer shares:
/// 56-byte header, 40-byte records with int64 µs since 1899-12-30 UTC (strictly ascending),
/// opens that let Sierra keep the file open, and the Users:Modify ACE that services running
/// as LocalSystem must add so Sierra and the user can rewrite or delete what they create.
/// Used by <see cref="YieldDataLogger.Core.Sinks.ScidSink"/> (price ticks) and
/// <see cref="ScidTradeFile"/> (exchange trades with volume and aggressor side).
/// </summary>
public static class ScidFormat
{
    public const int HeaderSize = 56;
    public const int RecordSize = 40;

    /// <summary>Microseconds between the Sierra epoch (1899-12-30) and the Unix epoch.</summary>
    public const long UnixEpochOffsetUs = 25_569L * 86_400L * 1_000_000L;

    public static long UnixSecondsToSierraUs(double unixSeconds) =>
        (long)Math.Round(unixSeconds * 1_000_000d) + UnixEpochOffsetUs;

    public static double SierraUsToUnixSeconds(long us) => (us - UnixEpochOffsetUs) / 1_000_000d;

    public static long UnixUsToSierraUs(long unixUs) => unixUs + UnixEpochOffsetUs;

    public static long SierraUsToUnixUs(long us) => us - UnixEpochOffsetUs;

    /// <summary>Read/write open that tolerates Sierra (or anyone) holding the file open, and deletes.</summary>
    public static FileStream OpenShared(string file, FileMode mode) =>
        new(file, mode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    public static long RecordCount(long fileLength) =>
        fileLength < HeaderSize ? 0 : (fileLength - HeaderSize) / RecordSize;

    public static void WriteHeader(BinaryWriter w)
    {
        w.Write("SCID"u8);        // FileTypeUniqueHeaderID
        w.Write((uint)HeaderSize);
        w.Write((uint)RecordSize);
        w.Write((ushort)1);       // Version
        w.Write((ushort)0);       // Unused1
        w.Write((uint)0);         // UTCStartIndex
        w.Write(new byte[36]);    // Reserve
    }

    public static void WriteRecord(BinaryWriter w, in ScidRec r)
    {
        w.Write(r.Us);
        w.Write(r.Open);
        w.Write(r.High);
        w.Write(r.Low);
        w.Write(r.Close);
        w.Write(r.Trades);
        w.Write(r.Volume);
        w.Write(r.BidVolume);
        w.Write(r.AskVolume);
    }

    public static ScidRec ReadRecord(ReadOnlySpan<byte> b) => new(
        BitConverter.ToInt64(b),
        BitConverter.ToSingle(b[8..]),  BitConverter.ToSingle(b[12..]),
        BitConverter.ToSingle(b[16..]), BitConverter.ToSingle(b[20..]),
        BitConverter.ToUInt32(b[24..]), BitConverter.ToUInt32(b[28..]),
        BitConverter.ToUInt32(b[32..]), BitConverter.ToUInt32(b[36..]));

    /// <summary>Index of the first record whose timestamp is &gt;= <paramref name="us"/>.</summary>
    public static long LowerBound(FileStream fs, long count, long us)
    {
        if (us == long.MinValue) return 0;
        long lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (ReadTimestamp(fs, mid) < us) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public static long ReadTimestamp(FileStream fs, long index)
    {
        Span<byte> buf = stackalloc byte[8];
        fs.Seek(HeaderSize + index * RecordSize, SeekOrigin.Begin);
        fs.ReadExactly(buf);
        return BitConverter.ToInt64(buf);
    }

    /// <summary>
    /// Services run as LocalSystem, so files they create are owned by SYSTEM. Inherited ACLs on
    /// a Sierra Data folder usually let users read, but not always write or delete - add an
    /// explicit Users:Modify ACE so Sierra and the user have full use. Never throws.
    /// </summary>
    public static void GrantUsersModify(string file, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi  = new FileInfo(file);
            var acl = fi.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow));
            fi.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            // Not fatal - Sierra can typically still read via inherited permissions.
            logger.LogWarning(ex, "Could not grant Users access to {File}", file);
        }
    }
}
