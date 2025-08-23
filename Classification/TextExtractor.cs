using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Explore.Build
{
    public static class TextExtractor
    {
        // ---- 外部ツールの既定パス（環境変数で上書き可） ----
        // Poppler: pdftotext / pdftoppm
        private static readonly string PdftotextExe =
            Environment.GetEnvironmentVariable("PDFTOTEXT_EXE")
            ?? @"C:\Program Files\poppler\bin\pdftotext.exe";

        private static readonly string PdftoppmExe =
            Environment.GetEnvironmentVariable("PDFTOPPM_EXE")
            ?? @"C:\Program Files\poppler\bin\pdftoppm.exe";

        // Tesseract OCR（日本語+英語）
        private static readonly string TesseractExe =
            Environment.GetEnvironmentVariable("TESSERACT_EXE")
            ?? @"C:\Program Files\Tesseract-OCR\tesseract.exe";

        private static readonly string TesseractLang =
            Environment.GetEnvironmentVariable("TESSERACT_LANG")
            ?? "jpn+eng";

        /// <summary>
        /// ファイルからテキスト抽出。PDFは
        /// ①pdftotext → ②pdftoppm+tesseract → ③Python(PyMuPDF)+tesseract の順で試す。
        /// 抽出後は正規化して 4〜8KB に整える（既定 8KB）。
        /// </summary>
        public static async Task<string> ExtractAsync(string path, int maxBytes = 8 * 1024)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            try
            {
                if (ext is ".txt" or ".md" or ".csv" or ".log")
                {
                    var raw = await File.ReadAllTextAsync(path);
                    return NormalizeAndTrim(raw, maxBytes);
                }

                if (ext == ".docx")
                {
                    var raw = ExtractDocx(path);
                    return NormalizeAndTrim(raw, maxBytes);
                }

                if (ext == ".pdf")
                {
                    // 1) pdftotext
                    var raw = await TryPdfToTextAsync(path);

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        // 2) Poppler(+tesseract) OCR フォールバック
                        raw = await TryOcrPdfAsync(path, firstPages: 2, dpi: 300);
                    }

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        // 3) Python(PyMuPDF + Tesseract) フォールバック（Poppler不要）
                        raw = await TryOcrPdfViaPythonAsync(path, firstPages: 2, dpi: 300);
                    }

                    return NormalizeAndTrim(raw, maxBytes);
                }
            }
            catch
            {
                // いずれも失敗時は空を返す
            }

            return string.Empty;
        }

        // --------- helpers ----------

        private static async Task<string> TryPdfToTextAsync(string pdfPath)
        {
            try
            {
                if (!File.Exists(PdftotextExe))
                    return string.Empty;

                // pdftotext -layout -q PDF -
                var psi = new ProcessStartInfo
                {
                    FileName = PdftotextExe,
                    Arguments = $"-layout -q \"{pdfPath}\" -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                var p = Process.Start(psi);
                if (p == null) return string.Empty;
                using (p)
                {
                    var text = await p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    return text ?? string.Empty;
                }
            }
            catch { return string.Empty; }
        }

        /// <summary>Poppler(pdftoppm)で画像化 → TesseractでOCR</summary>
        private static async Task<string> TryOcrPdfAsync(string pdfPath, int firstPages, int dpi)
        {
            // pdftoppm で PNG にレンダリング → tesseract で OCR
            if (!File.Exists(PdftoppmExe) || !File.Exists(TesseractExe))
                return string.Empty;

            var work = Path.Combine(Path.GetTempPath(),
                $"fr_ocr_{Path.GetFileNameWithoutExtension(pdfPath)}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);

            try
            {
                // pdftoppm -r 300 -png -f 1 -l {firstPages} "in.pdf" "outprefix"
                var outPrefix = Path.Combine(work, "page");
                var ppm = new ProcessStartInfo
                {
                    FileName = PdftoppmExe,
                    Arguments = $"-r {dpi} -png -f 1 -l {firstPages} \"{pdfPath}\" \"{outPrefix}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var p1 = Process.Start(ppm);
                if (p1 != null)
                {
                    using (p1) { await p1.WaitForExitAsync(); }
                }

                var images = Directory.EnumerateFiles(work, "page-*.png").OrderBy(x => x).ToList();
                if (images.Count == 0) return string.Empty;

                var sb = new StringBuilder();
                foreach (var img in images)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = TesseractExe,
                        Arguments = $"\"{img}\" stdout -l {TesseractLang}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    var p2 = Process.Start(psi);
                    if (p2 == null) continue;
                    using (p2)
                    {
                        var text = await p2.StandardOutput.ReadToEndAsync();
                        await p2.WaitForExitAsync();
                        if (!string.IsNullOrEmpty(text))
                            sb.AppendLine(text);
                    }
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Popplerが無い環境向けのフォールバック。
        /// Python + PyMuPDF でPDFをラスタライズ → TesseractでOCR
        /// </summary>
        private static async Task<string> TryOcrPdfViaPythonAsync(string pdfPath, int firstPages, int dpi)
        {
            // Tesseract は必須（Python側からも使う）
            if (!File.Exists(TesseractExe))
                return string.Empty;

            // Python 実行ファイル（指定なければ python → py の順で試す）
            string? python = Environment.GetEnvironmentVariable("PYTHON_EXE");
            if (string.IsNullOrWhiteSpace(python)) python = "python";

            // 一時スクリプト生成
            var work = Path.Combine(Path.GetTempPath(), $"fr_pyocr_{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            var scriptPath = Path.Combine(work, "ocr_pdf_to_text.py");

            var lang = TesseractLang;
            var py = $@"# -*- coding: utf-8 -*-
import io, sys
try:
    import fitz
    import pytesseract
    from PIL import Image
    pytesseract.pytesseract.tesseract_cmd = r""{TesseractExe.Replace(@"\", @"\\")}""
    pdf = fitz.open(r""{pdfPath.Replace(@"\", @"\\")}"")
    n = min({firstPages}, len(pdf))
    out = []
    for i in range(n):
        p = pdf[i]
        pix = p.get_pixmap(dpi={dpi})
        img = Image.open(io.BytesIO(pix.tobytes('ppm')))
        out.append(pytesseract.image_to_string(img, lang=""{lang}""))
    sys.stdout.write(''.join(out))
except Exception:
    pass
";
            try
            {
                await File.WriteAllTextAsync(scriptPath, py, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = python!,
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process? proc = null;
                try { proc = Process.Start(psi); }
                catch
                {
                    // Python ランチャ（py -3）も試す
                    psi.FileName = "py";
                    psi.Arguments = $"-3 \"{scriptPath}\"";
                    try { proc = Process.Start(psi); } catch { return string.Empty; }
                }

                if (proc == null) return string.Empty;
                using (proc)
                {
                    var text = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    return text ?? string.Empty;
                }
            }
            catch { return string.Empty; }
            finally
            {
                try { Directory.Delete(work, true); } catch { /* ignore */ }
            }
        }

        // -------- .docx を軽くテキスト化（Zip内の XML をタグ除去） --------
        private static string ExtractDocx(string path)
        {
            try
            {
                // .docx = zip(word/document.xml) を雑にテキスト化
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) return string.Empty;
                using var s = entry.Open();
                using var sr = new StreamReader(s, Encoding.UTF8);
                var xml = sr.ReadToEnd();
                var txt = Regex.Replace(xml, "<.*?>", " ");
                txt = System.Net.WebUtility.HtmlDecode(txt);
                return Regex.Replace(txt, @"\s+", " ").Trim();
            }
            catch { return string.Empty; }
        }

        /// <summary>改行・空白を整え、UTF-8 の境界を壊さないように最大バイト長で丸める</summary>
        public static string NormalizeAndTrim(string s, int maxBytes = 8 * 1024)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // 改行/空白をつめる
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @"\s*\r?\n\s*", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();

            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= maxBytes) return s;

            // UTF-8 の境界を壊さないように丸める
            var len = maxBytes;
            while (len > 0 && (bytes[len - 1] & 0xC0) == 0x80) len--;
            return Encoding.UTF8.GetString(bytes, 0, len);
        }
    }
}
