# Regenerates windows/PadLinkTray/padlink.ico (16-256 px, PNG-compressed) from the same
# artwork as Logo.Draw in Common.cs. Needs Windows PowerShell 5.1 (built into Windows):
#   powershell -NoProfile -ExecutionPolicy Bypass -File windows\tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System.Drawing;
using System.Drawing.Drawing2D;
public static class LogoGen {
    public static void Draw(Graphics g, float size) {
        float k = size / 32f;
        g.ScaleTransform(k, k);
        using (var bg = RoundedRect(0.5f, 0.5f, 31f, 31f, 7.5f))
        using (var grad = new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32),
                   Color.FromArgb(56, 142, 255), Color.FromArgb(20, 84, 214)))
            g.FillPath(grad, bg);
        using (var pad = new GraphicsPath(FillMode.Winding)) {
            pad.AddPath(RoundedRect(5f, 10f, 22f, 10.5f, 5f), false);
            pad.AddEllipse(4.2f, 12.5f, 9.5f, 12.5f);
            pad.AddEllipse(18.3f, 12.5f, 9.5f, 12.5f);
            using (var white = new SolidBrush(Color.White)) g.FillPath(white, pad);
        }
        using (var cut = new SolidBrush(Color.FromArgb(28, 98, 226))) {
            g.FillRectangle(cut, 7.6f, 14.3f, 4.6f, 1.6f);
            g.FillRectangle(cut, 9.1f, 12.8f, 1.6f, 4.6f);
            g.FillEllipse(cut, 20.6f, 12.6f, 2.2f, 2.2f);
            g.FillEllipse(cut, 18.4f, 15.0f, 2.2f, 2.2f);
        }
    }
    static GraphicsPath RoundedRect(float x, float y, float w, float h, float r) {
        var p = new GraphicsPath(); float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    public static byte[] Png(int size) {
        using (var bmp = new Bitmap(size, size))
        using (var g = Graphics.FromImage(bmp))
        using (var ms = new System.IO.MemoryStream()) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Draw(g, size);
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
'@

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { , [LogoGen]::Png($s) }
$out = Join-Path $PSScriptRoot '..\PadLinkTray\padlink.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
# also a preview PNG for docs
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'padlink-256.png'), [LogoGen]::Png(256))
"wrote $out"
