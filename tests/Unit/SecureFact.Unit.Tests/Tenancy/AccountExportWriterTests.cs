using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SecureFact.SharedKernel.Export;
using SecureFact.Tenancy.Application;

namespace SecureFact.Unit.Tests.Tenancy;

/// <summary>The package of an account (ADR-074): the CSV that a spreadsheet opens, the files that stay inside the package, and the table of contents that proves it is complete.</summary>
public sealed class AccountExportWriterTests
{
    private static async IAsyncEnumerable<IReadOnlyList<string?>> Rows(params string?[][] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static async Task<ZipArchive> WriteAsync(Func<ZipAccountDataWriter, Task> write)
    {
        var output = new MemoryStream();
        var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        await using (archive)
        {
            await write(new ZipAccountDataWriter(archive));
        }

        output.Position = 0;
        return new ZipArchive(output, ZipArchiveMode.Read);
    }

    private static string Text(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+51 999 111 222", "'+51 999 111 222")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUMA(A1)", "'@SUMA(A1)")]
    [InlineData("\tdato", "'\tdato")]
    [InlineData("Ferretería El Sol", "Ferretería El Sol")]
    [InlineData("Av. Larco 123, Miraflores", "\"Av. Larco 123, Miraflores\"")]
    [InlineData("dijo \"hola\"", "\"dijo \"\"hola\"\"\"")]
    [InlineData("dos\nlíneas", "\"dos\nlíneas\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_cell_never_runs_as_a_formula_and_keeps_its_commas_and_quotes(string? value, string expected) =>
        Assert.Equal(expected, AccountCsv.Cell(value));

    [Fact]
    public void Numbers_and_dates_are_written_the_same_in_every_culture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-PE");
            Assert.Equal("118.00", AccountCsv.Number(118.00m));
            Assert.Equal("0.18", AccountCsv.Number(0.18m));
            Assert.Equal("1234567", AccountCsv.Number(1_234_567L));
            Assert.Equal("2026-11-03T15:04:05Z", AccountCsv.Stamp(new DateTimeOffset(2026, 11, 3, 10, 4, 5, TimeSpan.FromHours(-5))));
            Assert.Equal("2026-11-03", AccountCsv.Day(new DateOnly(2026, 11, 3)));
            Assert.Null(AccountCsv.Stamp((DateTimeOffset?)null));
            Assert.Null(AccountCsv.Day((DateOnly?)null));
            Assert.Null(AccountCsv.Number((decimal?)null));
            Assert.Equal("si", AccountCsv.YesNo(true));
            Assert.Equal("no", AccountCsv.YesNo(false));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task A_table_is_a_csv_with_the_mark_of_utf8_its_header_and_one_line_per_row()
    {
        using var zip = await WriteAsync(writer => writer.WriteTableAsync("clientes", ["id", "nombre"], Rows(["1", "Ana, la \"mejor\""], ["2", "=CMD()"]), CancellationToken.None));

        var entry = Assert.Single(zip.Entries);
        Assert.Equal("datos/clientes.csv", entry.FullName);
        using var raw = entry.Open();
        var bytes = new MemoryStream();
        await raw.CopyToAsync(bytes);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.ToArray()[..3]);
        Assert.Equal("﻿id,nombre\r\n1,\"Ana, la \"\"mejor\"\"\"\r\n2,'=CMD()\r\n", Encoding.UTF8.GetString(bytes.ToArray()));
    }

    [Fact]
    public async Task A_row_with_a_different_number_of_cells_than_the_header_is_a_bug_and_stops_the_package()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(writer => writer.WriteTableAsync("clientes", ["id", "nombre"], Rows(["1"]), CancellationToken.None)));

        Assert.Contains("clientes", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../fuera.txt")]
    [InlineData("xml/../../fuera.txt")]
    [InlineData("/absoluta.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("xml//doble.xml")]
    [InlineData("./actual.txt")]
    [InlineData("")]
    public async Task A_file_stays_inside_the_package(string path) =>
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(writer => writer.WriteFileAsync(path, new byte[] { 1 }, CancellationToken.None)));

    [Fact]
    public async Task The_table_of_contents_lists_the_rows_and_the_digest_of_each_table_and_how_many_files_each_folder_took()
    {
        using var zip = await WriteAsync(async writer =>
        {
            await writer.WriteTableAsync("clientes", ["id"], Rows(["1"], ["2"], ["3"]), CancellationToken.None);
            await writer.WriteTableAsync("vacia", ["id"], Rows(), CancellationToken.None);
            await writer.WriteFileAsync("xml/20100066603-01-F001-1.xml", Encoding.UTF8.GetBytes("<a/>"), CancellationToken.None);
            await writer.WriteFileAsync("xml/20100066603-01-F001-2.xml", Encoding.UTF8.GetBytes("<b/>"), CancellationToken.None);
            await writer.WriteFileAsync("cdr/R-20100066603-01-F001-1.zip", new byte[] { 1, 2, 3 }, CancellationToken.None);
            await writer.WriteFileAsync("LEEME.txt", Encoding.UTF8.GetBytes("hola"), CancellationToken.None);
            await writer.WriteTableAsync("contenido", ["archivo", "filas", "sha256"], writer.Manifest().ToAsyncEnumerable(), CancellationToken.None);
        });

        var clientes = zip.GetEntry("datos/clientes.csv")!;
        var content = new MemoryStream();
        await using (var stream = await clientes.OpenAsync())
        {
            await stream.CopyToAsync(content);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));

        var lines = Text(zip.GetEntry("datos/contenido.csv")!).TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["archivo,filas,sha256", $"datos/clientes.csv,3,{digest}", $"datos/vacia.csv,0,{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("﻿id\r\n")))}", "cdr/,1,", "xml/,2,"],
            lines);
    }
}
