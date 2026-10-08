using SecureFact.SharedKernel.Import;

namespace SecureFact.Unit.Tests.SharedKernel;

public class CsvParserTests
{
    private static CsvTable Read(string text, int maxRows = ImportLimits.MaxRows)
    {
        var result = CsvParser.Parse(text, maxRows);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        return result.Value;
    }

    [Fact]
    public void A_plain_file_gives_headers_rows_and_the_line_where_each_row_is()
    {
        var table = Read("a,b,c\n1,2,3\n4,5,6\n");

        Assert.Equal(["a", "b", "c"], table.Headers);
        Assert.Equal([2, 3], table.Rows.Select(r => r.Line));
        Assert.Equal(["4", "5", "6"], table.Rows[1].Cells);
        Assert.Equal(',', table.Delimiter);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("solo\nuno", ',')]
    public void The_delimiter_is_the_commonest_one_of_the_header_line(string text, char expected) =>
        Assert.Equal(expected, Read(text).Delimiter);

    [Fact]
    public void A_semicolon_inside_quotes_does_not_decide_the_delimiter()
    {
        var table = Read("\"a;b;c;d\",e,f\n1,2,3");

        Assert.Equal(',', table.Delimiter);
        Assert.Equal(["a;b;c;d", "e", "f"], table.Headers);
    }

    [Fact]
    public void Quotes_keep_commas_and_line_breaks_and_a_doubled_quote_is_one_quote()
    {
        var table = Read("name,note\r\n\"Perez, Ana\",\"dice \"\"hola\"\"\"\r\n\"Dos\nlíneas\",x\r\ny,z\r\n");

        Assert.Equal("Perez, Ana", table.Rows[0].Cells[0]);
        Assert.Equal("dice \"hola\"", table.Rows[0].Cells[1]);
        Assert.Equal("Dos\nlíneas", table.Rows[1].Cells[0]);
        Assert.Equal([2, 3, 5], table.Rows.Select(r => r.Line)); // the row after a break inside quotes is counted on its own line
    }

    [Fact]
    public void A_byte_order_mark_blank_lines_and_rows_of_empty_cells_are_ignored()
    {
        var table = Read("﻿a,b\n\n1,2\n , \n,,\n3,4\n");

        Assert.Equal(["a", "b"], table.Headers);
        Assert.Equal(["1", "3"], table.Rows.Select(r => r.Cells[0]));
    }

    [Fact]
    public void A_short_row_is_padded_and_a_long_one_is_flagged_only_when_the_extra_cells_hold_something()
    {
        var table = Read("a,b,c\n1\n1,2,3,4\n1,2,3,\n");

        Assert.Equal(["1", "", ""], table.Rows[0].Cells);
        Assert.False(table.Rows[0].HasExtraCells);
        Assert.True(table.Rows[1].HasExtraCells);
        Assert.False(table.Rows[2].HasExtraCells); // a trailing delimiter is not a shifted column
    }

    [Fact]
    public void A_header_only_file_has_no_rows()
    {
        Assert.Empty(Read("a,b\n").Rows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n \r\n")]
    public void An_empty_file_is_refused(string? text)
    {
        var result = CsvParser.Parse(text);

        Assert.False(result.IsSuccess);
        Assert.Equal("SF-IMP-001", result.Error.Code);
    }

    [Fact]
    public void An_open_quote_is_refused_with_the_line_where_it_opened()
    {
        var result = CsvParser.Parse("a,b\n1,\"sin cerrar\n2,3");

        Assert.False(result.IsSuccess);
        Assert.Contains("línea 2", result.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_rows_and_too_big_a_file_are_refused()
    {
        Assert.False(CsvParser.Parse("a\n1\n2\n3\n", maxRows: 2).IsSuccess);
        Assert.True(CsvParser.Parse("a\n1\n2\n", maxRows: 2).IsSuccess);
        Assert.False(CsvParser.Parse(new string('x', ImportLimits.MaxCharacters + 1)).IsSuccess);
    }

    [Theory]
    [InlineData("Razón Social", "razon_social")]
    [InlineData("  RAZON-SOCIAL  ", "razon_social")]
    [InlineData("Valor unitario (S/)", "valor_unitario_s")]
    [InlineData("N° Documento", "n_documento")]
    [InlineData("Teléfono", "telefono")]
    [InlineData("___", "")]
    public void Header_names_are_compared_without_accents_case_or_punctuation(string name, string expected) =>
        Assert.Equal(expected, CsvTable.Normalize(name));

    [Fact]
    public void A_column_is_found_by_any_of_its_aliases_and_the_first_one_wins()
    {
        var table = Read("Código,Razón Social,razon_social\n1,a,b\n");

        Assert.Equal(0, table.IndexOf("codigo"));
        Assert.Equal(1, table.IndexOf("nombre", "razon_social"));
        Assert.Equal(-1, table.IndexOf("telefono"));
    }
}
