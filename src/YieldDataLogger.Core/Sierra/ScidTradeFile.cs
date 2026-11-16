using Microsoft.Extensions.Logging;

namespace YieldDataLogger.Core.Sierra;

/// <summary>
/// A Sierra Chart .scid file of full trade records (volume + bid/ask volume), as written by
/// the exchange feed. The price-only counterpart is <see cref="YieldDataLogger.Core.Sinks.ScidSink"/>;
/// both share <see cref="ScidFormat"/>.
///
/// Single writer: the owner calls everything from one task. Each call re-opens the file
/// shared (Sierra keeps it open) and notices if someone else deleted, truncated or grew it.
/// Timestamps are kept strictly ascending - Sierra requires it - by nudging a record that
/// collides with its predecessor forward 1 µs (several fills often share a millisecond).
/// </summary>
public sealed class ScidTradeFile
{
    private readonly string _tempDir;
    private readonly ILogger _logger;

    public ScidTradeFile(string path, string tempDir, ILogger logger)
    {
        FilePath = path;
        _tempDir = tempDir;
        _logger  = logger;
    }

    public string FilePath { get; }

    /// <summary>Number of records in the file.</summary>
    public long Count { get; private set; }

    /// <summary>Timestamp (Sierra µs) of the last record, or <see cref="long.MinValue"/> when empty.</summary>
    public long LastUs { get; private set; } = long.MinValue;

    /// <summary>Creates the file (header + Users:Modify ACL) if needed, trims a torn tail, loads Count/LastUs.</summary>
    public void Init()
    {
        bool created;
        using (var fs = ScidFormat.OpenShared(FilePath, FileMode.OpenOrCreate))
        {
            created = fs.Length < ScidFormat.HeaderSize;
            if (created)
            {
                fs.SetLength(0);
                using var w = new BinaryWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true);
                ScidFormat.WriteHeader(w);
            }
            Load(fs);
        }
        if (created) ScidFormat.GrantUsersModify(FilePath, _logger);
    }

    /// <summary>Appends records at the end of the file.</summary>
    public void Append(IEnumerable<ScidRec> records)
    {
        using var fs = OpenExisting();
        fs.Seek(0, SeekOrigin.End);
        WriteRecords(fs, records);
    }

    /// <summary>
    /// Replaces every record with a timestamp in [<paramref name="fromUs"/>, <paramref name="toUs"/>)
    /// with <paramref name="records"/>, keeping everything before and after. Records after the
    /// range are parked in a temp file while the range is rewritten, so memory stays flat
    /// whatever the file size. Returns the number of records removed.
    /// </summary>
    public long ReplaceRange(long fromUs, long toUs, IEnumerable<ScidRec> records)
    {
        using var fs = OpenExisting();
        var a = ScidFormat.LowerBound(fs, Count, fromUs);
        var b = ScidFormat.LowerBound(fs, Count, toUs);

        string? tail = null;
        try
        {
            if (b < Count)
            {
                Directory.CreateDirectory(_tempDir);
                tail = Path.Combine(_tempDir, $"{Path.GetFileNameWithoutExtension(FilePath)}.{Guid.NewGuid():N}.tail");
                using var t = new FileStream(tail, FileMode.CreateNew, FileAccess.Write);
                fs.Seek(ScidFormat.HeaderSize + b * ScidFormat.RecordSize, SeekOrigin.Begin);
                fs.CopyTo(t);
            }

            fs.SetLength(ScidFormat.HeaderSize + a * ScidFormat.RecordSize);
            Count  = a;
            LastUs = a > 0 ? ScidFormat.ReadTimestamp(fs, a - 1) : long.MinValue;
            fs.Seek(0, SeekOrigin.End);
            WriteRecords(fs, records);

            if (tail is not null)
            {
                using var t = new FileStream(tail, FileMode.Open, FileAccess.Read);
                WriteRecords(fs, ReadRecords(t));
            }
        }
        finally
        {
            if (tail is not null) try { File.Delete(tail); } catch { /* temp dir, harmless */ }
        }
        return b - a;
    }

    /// <summary>Reads all records in [<paramref name="fromUs"/>, <paramref name="toUs"/>). For checks and tests.</summary>
    public List<ScidRec> ReadRange(long fromUs, long toUs)
    {
        using var fs = OpenExisting();
        var a = ScidFormat.LowerBound(fs, Count, fromUs);
        var b = ScidFormat.LowerBound(fs, Count, toUs);
        fs.Seek(ScidFormat.HeaderSize + a * ScidFormat.RecordSize, SeekOrigin.Begin);
        return ReadRecords(fs).Take((int)(b - a)).ToList();
    }

    private void WriteRecords(FileStream fs, IEnumerable<ScidRec> records)
    {
        var last  = LastUs;
        var count = Count;
        try
        {
            // Not disposed: disposing a BufferedStream closes the FileStream under it, which the
            // caller may still need (ReplaceRange writes the parked tail after the new range).
            var bs = new BufferedStream(fs, 1 << 20);
            using var w = new BinaryWriter(bs, System.Text.Encoding.ASCII, leaveOpen: true);
            foreach (var r in records)
            {
                var us = last == long.MinValue ? r.Us : Math.Max(r.Us, last + 1);
                ScidFormat.WriteRecord(w, r with { Us = us });
                last = us;
                count++;
            }
            w.Flush();
            bs.Flush();
        }
        finally
        {
            LastUs = last;
            Count  = count;
        }
    }

    private static IEnumerable<ScidRec> ReadRecords(Stream s)
    {
        var buf = new byte[ScidFormat.RecordSize];
        var bs = new BufferedStream(s, 1 << 20);
        while (true)
        {
            var got = 0;
            while (got < buf.Length)
            {
                var n = bs.Read(buf, got, buf.Length - got);
                if (n == 0) yield break;
                got += n;
            }
            yield return ScidFormat.ReadRecord(buf);
        }
    }

    /// <summary>Opens the file and re-syncs Count/LastUs if anyone else changed its length.</summary>
    private FileStream OpenExisting()
    {
        if (!File.Exists(FilePath))
            throw new FileNotFoundException("The .scid file was deleted; it will be rebuilt.", FilePath);
        var fs = ScidFormat.OpenShared(FilePath, FileMode.Open);
        if (fs.Length != ScidFormat.HeaderSize + Count * ScidFormat.RecordSize)
        {
            if (fs.Length < ScidFormat.HeaderSize)
            {
                fs.Dispose();
                throw new InvalidDataException($"{FilePath} lost its header; it will be rebuilt.");
            }
            _logger.LogWarning("{File} changed size outside this service; re-reading it.", FilePath);
            Load(fs);
        }
        return fs;
    }

    private void Load(FileStream fs)
    {
        var n = ScidFormat.RecordCount(fs.Length);
        var aligned = ScidFormat.HeaderSize + n * ScidFormat.RecordSize;
        if (fs.Length != aligned)
        {
            // A torn trailing record (power loss mid-write) would misalign everything after it.
            _logger.LogWarning("Trimming {Bytes} torn trailing bytes from {File}", fs.Length - aligned, FilePath);
            fs.SetLength(aligned);
        }
        Count  = n;
        LastUs = n > 0 ? ScidFormat.ReadTimestamp(fs, n - 1) : long.MinValue;
    }
}
