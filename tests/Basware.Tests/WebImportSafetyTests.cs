using Npgsql;
using WpfAppBaswareLogin.Services;
using Xunit;
namespace Basware.Tests;
public class WebImportSafetyTests
{
    [Theory]
    [InlineData("1361-211", "1361211")]
    [InlineData("0000123", "0000123")]
    public void PreservesCnkDigits(string input,string expected) => Assert.Equal(expected,CnkNumber.Normalize(input));
    [Theory]
    [InlineData("123")]
    [InlineData("123456a")]
    public void RejectsInvalidCnk(string input) => Assert.Throws<ArgumentException>(()=>CnkNumber.Normalize(input));
    [Fact]
    public async Task RejectsDtdBeforeOpeningDatabase()
    {
        await using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var importer=new EdiXmlImporter(source);
        await Assert.ThrowsAsync<System.Xml.XmlException>(()=>importer.ImportXmlAsync("<!DOCTYPE Document [<!ENTITY x 'bad'>]><Document><DocumentNumber>&x;</DocumentNumber></Document>"));
    }
    [Fact]
    public async Task RejectsHtmlBeforeOpeningDatabase()
    {
        await using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        await Assert.ThrowsAsync<InvalidDataException>(()=>new EdiXmlImporter(source).ImportXmlAsync("<html><body>Login</body></html>"));
    }
}
