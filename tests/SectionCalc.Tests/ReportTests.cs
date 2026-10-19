using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using SectionCalc;
using Xunit;

public class ReportTests
{
    [Fact]
    public void ReportIsValidWordWithInputsResultsAndSelection()
    {
        var p = SectionProject.Example(SectionKind.Pile);
        p.Conditions.ShearCheck = true; p.Conditions.ShearModelConfirmed = true;
        p.Selection = new SelectionSettings { PileCountMin = 10, PileCountMax = 14, PileCountStep = 2, Diameters = [20, 25] };
        var selection = Selection.Run(p);
        var result = Engine.Check(selection.Best?.Candidate.Project ?? p);
        string path = Path.Combine(Path.GetTempPath(), $"section-report-{Guid.NewGuid():N}.docx");
        try
        {
            ReportWriter.Write(path, result, selection, new DateTime(2026, 10, 19));
            using var doc = WordprocessingDocument.Open(path, false);
            var errors = new OpenXmlValidator().Validate(doc).Select(e => e.Path?.XPath + " :: " + e.Description).Distinct().ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors.Take(12)));
            string text = doc.MainDocumentPart!.Document.Body!.InnerText;
            foreach (var expected in new[] { "Расчёт сечения", "Исходные данные", "Усилия", "Результаты проверок",
                         "Подбор армирования", "Примечания", "Прочность нормального сечения", "19.10.2026" })
                Assert.Contains(expected, text);
            Assert.Single(doc.MainDocumentPart.ImageParts);
        }
        finally { File.Delete(path); }
    }
}
