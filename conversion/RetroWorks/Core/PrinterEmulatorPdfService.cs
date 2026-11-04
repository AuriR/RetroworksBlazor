namespace RetroWorks.Core;

/// <summary>
/// Emulates the AppleWorks "print to printer" flow, but produces a PDF in the browser.
/// The C# side composes the document into fixed-width pages (header + body + footer);
/// the JS side (<c>interop.js</c> → <c>printPdf</c>) lays them out as a monospace grid in
/// the bundled ImgWriter NLQ font and triggers a download. Kept separate from the docs so
/// the print pipeline stays reusable when Data Base / Spreadsheet printing is added later.
/// </summary>
public static class PrinterEmulatorPdfService
{
    /// <summary>Print a Word Processor document to PDF (US Letter, 76 columns, black on white).
    /// Returns the file name printed, or null if the user cancelled the prompt.</summary>
    public static async Task<string?> PrintWpAsync(App app, WPDoc doc)
    {
        string? fn = await doc.ExportPathAsync(".pdf", "PrintToPdfFile");
        if (fn is null) return null;

        var pages = doc.BuildPrintPages();
        await app.PrintPdfAsync(fn, pages);
        return fn;
    }
}
