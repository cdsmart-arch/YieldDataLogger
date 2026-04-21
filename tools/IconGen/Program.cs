using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates a multi-resolution Windows .ico file for YieldDataLogger.Manager.
// Run once (or whenever the design needs tweaking); commit the resulting appicon.ico.
//
// Design: a dark navy rounded square with a green yield-curve line rising from lower-left
// to upper-right, matching the dashboard's connected-state green and overall dark theme.

var outPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "YieldDataLogger.Manager", "appicon.ico");
outPath = Path.GetFullPath(outPath);

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var framePngs = sizes.Select(s => (size: s, bytes: RenderPng(s))).ToArray();

using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
using var bw = new BinaryWriter(fs);
WriteIco(bw, framePngs);

Console.WriteLine($"Wrote {outPath} ({fs.Length:N0} bytes, {sizes.Length} sizes)");
return 0;

static byte[] RenderPng(int size)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g   = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    // Rounded-square background, inset by 1px so the edge is not clipped.
    var rect   = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
    var radius = Math.Max(2f, size * 0.22f);
    using (var bg = RoundedRect(rect, radius))
    using (var brush = new LinearGradientBrush(
               new PointF(0, 0),
               new PointF(0, size),
               Color.FromArgb(255, 22, 27, 34),
               Color.FromArgb(255, 14, 17, 23)))
    {
        g.FillPath(brush, bg);
        using var border = new Pen(Color.FromArgb(90, 139, 148, 158), Math.Max(1f, size / 128f));
        g.DrawPath(border, bg);
    }

    // Baseline grid (visible only at larger sizes to avoid clutter at 16/24px).
    if (size >= 48)
    {
        using var grid = new Pen(Color.FromArgb(40, 139, 148, 158), 1f);
        var pad = size * 0.18f;
        for (var i = 1; i <= 3; i++)
        {
            var y = pad + (size - 2 * pad) * i / 4f;
            g.DrawLine(grid, pad, y, size - pad, y);
        }
    }

    // Yield curve path - smooth rising line.
    var points = new PointF[]
    {
        new(size * 0.18f, size * 0.78f),
        new(size * 0.36f, size * 0.66f),
        new(size * 0.54f, size * 0.50f),
        new(size * 0.72f, size * 0.36f),
        new(size * 0.86f, size * 0.22f),
    };

    // Green line (connected-state accent).
    using (var linePen = new Pen(Color.FromArgb(255, 63, 185, 80), Math.Max(1.2f, size / 18f))
    {
        LineJoin  = LineJoin.Round,
        StartCap  = LineCap.Round,
        EndCap    = LineCap.Round,
    })
    {
        if (size >= 32) g.DrawCurve(linePen, points, 0.4f);
        else            g.DrawLines(linePen, points);
    }

    // Leading and trailing dots (only visible at larger sizes).
    if (size >= 24)
    {
        var dotR = Math.Max(1.5f, size / 20f);
        using var endBrush = new SolidBrush(Color.FromArgb(255, 230, 230, 230));
        g.FillEllipse(endBrush, points[^1].X - dotR, points[^1].Y - dotR, dotR * 2, dotR * 2);

        if (size >= 48)
        {
            using var startBrush = new SolidBrush(Color.FromArgb(180, 230, 230, 230));
            g.FillEllipse(startBrush, points[0].X - dotR * 0.7f, points[0].Y - dotR * 0.7f, dotR * 1.4f, dotR * 1.4f);
        }
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var d = radius * 2f;
    var path = new GraphicsPath();
    path.StartFigure();
    path.AddArc(r.Left,      r.Top,        d, d, 180, 90);
    path.AddArc(r.Right - d, r.Top,        d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
    path.AddArc(r.Left,      r.Bottom - d, d, d,  90, 90);
    path.CloseFigure();
    return path;
}

static void WriteIco(BinaryWriter w, (int size, byte[] bytes)[] frames)
{
    // ICONDIR (6 bytes).
    w.Write((ushort)0); // Reserved
    w.Write((ushort)1); // Type = icon
    w.Write((ushort)frames.Length);

    // Each ICONDIRENTRY is 16 bytes; image data follows.
    var imageOffset = 6 + 16 * frames.Length;

    foreach (var (size, bytes) in frames)
    {
        // 256 is encoded as 0 per the format spec.
        w.Write((byte)(size == 256 ? 0 : size)); // Width
        w.Write((byte)(size == 256 ? 0 : size)); // Height
        w.Write((byte)0);     // ColorCount
        w.Write((byte)0);     // Reserved
        w.Write((ushort)1);   // Planes
        w.Write((ushort)32);  // BitCount
        w.Write((uint)bytes.Length);    // BytesInRes
        w.Write((uint)imageOffset);     // ImageOffset
        imageOffset += bytes.Length;
    }

    foreach (var (_, bytes) in frames)
        w.Write(bytes);
}
