using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The import of customers and products from a CSV (ADR-053): preview that writes nothing, a commit that is atomic and idempotent, rows explained one by one, and tenants kept apart.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ImportApiTests(ApiFixture api)
{
    private sealed record Row(int Line, string Status, string? Key, string? Message);

    private sealed record Result(bool Committed, int Total, int Ready, int Created, int Existing, int Invalid, List<Row> Rows);

    private sealed record Listed(string DocumentNumber, string Name);

    private sealed record ListedProduct(string InternalCode, string Description, string Kind, string UnitCode, decimal UnitValue, string IgvAffectationCode);

    private async Task<(Guid TenantId, HttpClient Owner)> NewTenantAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..28]);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)));
    }

    private async Task<HttpClient> NewOwnerAsync(string name) => (await NewTenantAsync(name)).Owner;

    /// <summary>A RUC of 11 digits that passes the check digit of SUNAT, from a seed.</summary>
    private static string Ruc(int seed)
    {
        var body = $"20{seed:D8}";
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((digit, index) => (digit - '0') * weights[index]).Sum();
        var check = (11 - (sum % 11)) % 10;
        return body + check;
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>The explanation of a problem, decoded (the JSON of the body escapes the accents).</summary>
    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("detail").GetString()!;
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string path, string? csv, bool commit) =>
        client.PostAsJsonAsync($"/api/v1/{path}/import", new { csv, commit });

    private static async Task<Result> ResultOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Result>(ApiFixture.JsonOptions))!;
    }

    private static async Task<int> CountAsync(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<List<JsonElement>>($"/api/v1/{path}?take=200", ApiFixture.JsonOptions))!.Count;

    // ---------- customers ----------

    [Fact]
    public async Task The_preview_writes_nothing_and_the_commit_creates_the_valid_rows_once()
    {
        var owner = await NewOwnerAsync("Import Clientes");
        var csv = $"tipo_documento,numero_documento,nombre,direccion,correo,telefono\n6,{Ruc(1)},Distribuidora Uno SAC,Av. Uno 100,uno@cliente.pe,999111222\n1,45678912,Ana Pérez,,,\n";

        var preview = await ResultOfAsync(await ImportAsync(owner, "customers", csv, commit: false));
        Assert.False(preview.Committed);
        Assert.Equal((2, 2, 0, 0, 0), (preview.Total, preview.Ready, preview.Created, preview.Existing, preview.Invalid));
        Assert.All(preview.Rows, row => Assert.Equal("Ready", row.Status));
        Assert.Equal(0, await CountAsync(owner, "customers"));

        var committed = await ResultOfAsync(await ImportAsync(owner, "customers", csv, commit: true));
        Assert.True(committed.Committed);
        Assert.Equal((2, 0, 2, 0, 0), (committed.Total, committed.Ready, committed.Created, committed.Existing, committed.Invalid));
        var listed = (await owner.GetFromJsonAsync<List<Listed>>("/api/v1/customers?take=200", ApiFixture.JsonOptions))!;
        Assert.Equal(["Ana Pérez", "Distribuidora Uno SAC"], listed.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());

        // The same file again creates nothing: the customers exist, and they are not overwritten.
        var again = await ResultOfAsync(await ImportAsync(owner, "customers", csv.Replace("Distribuidora Uno SAC", "Otro nombre", StringComparison.Ordinal), commit: true));
        Assert.Equal((2, 0, 0, 2, 0), (again.Total, again.Ready, again.Created, again.Existing, again.Invalid));
        Assert.Contains(listed, c => c.Name == "Distribuidora Uno SAC");
        Assert.Equal(2, await CountAsync(owner, "customers"));
    }

    [Fact]
    public async Task Every_row_with_a_problem_is_explained_with_its_line_and_the_rest_is_imported()
    {
        var owner = await NewOwnerAsync("Import Errores");
        var csv = string.Join('\n',
            "tipo_documento;numero_documento;nombre;correo",
            $"RUC;{Ruc(2)};Buena SAC;compras@buena.pe", //                 line 2: valid, with «;» as Excel in Spanish writes it
            "RUC;20123456789;RUC con dígito malo;", //                      line 3: the check digit fails
            "DNI;1234;Dni corto;", //                                       line 4: not 8 digits
            $"RUC;{Ruc(2)};Repetida;", //                                   line 5: the same document as line 2
            $"pasaporte;P1234567;;", //                                     line 6: no name
            $"RUC;{Ruc(3)};Correo malo;no-es-un-correo", //                 line 7: bad e-mail
            "XYZ;1;Tipo raro;", //                                          line 8: unknown type
            $"RUC;{Ruc(4)};Otra buena SAC;", //                             line 9: valid
            $"RUC;{Ruc(5)};Con coma, sin comillas;x;y"); //                line 10: one cell too many

        var result = await ResultOfAsync(await ImportAsync(owner, "customers", csv, commit: true));

        Assert.Equal((9, 0, 2, 0, 7), (result.Total, result.Ready, result.Created, result.Existing, result.Invalid));
        var byLine = result.Rows.ToDictionary(r => r.Line);
        Assert.Equal(["Created", "Invalid", "Invalid", "Invalid", "Invalid", "Invalid", "Invalid", "Created", "Invalid"], result.Rows.Select(r => r.Status).ToArray());
        Assert.Contains("RUC", byLine[3].Message, StringComparison.Ordinal);
        Assert.Contains("8 dígitos", byLine[4].Message, StringComparison.Ordinal);
        Assert.Contains("línea 2", byLine[5].Message, StringComparison.Ordinal);
        Assert.Contains("nombre", byLine[6].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correo", byLine[7].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo de documento", byLine[8].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comillas", byLine[10].Message, StringComparison.Ordinal);
        Assert.Equal(2, await CountAsync(owner, "customers"));
    }

    [Fact]
    public async Task Without_a_type_column_the_length_of_the_number_decides_only_for_a_ruc_and_a_dni()
    {
        var owner = await NewOwnerAsync("Import Deduce");
        var csv = $"documento,razon social\n{Ruc(6)},Con RUC SAC\n87654321,Con DNI\nAB123,Ni una cosa ni otra\n";

        var result = await ResultOfAsync(await ImportAsync(owner, "customers", csv, commit: true));

        Assert.Equal((3, 2, 1), (result.Total, result.Created, result.Invalid));
        Assert.Contains("deducir", result.Rows.Single(r => r.Status == "Invalid").Message, StringComparison.Ordinal);
        var listed = (await owner.GetFromJsonAsync<List<Listed>>("/api/v1/customers?take=200", ApiFixture.JsonOptions))!;
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_refused_whole_with_its_reason()
    {
        var owner = await NewOwnerAsync("Import Archivo");

        foreach (var (csv, fragment) in new (string?, string)[]
        {
            (null, "vacío"),
            ("   \n  ", "vacío"),
            ("nombre,telefono\nAna,1", "Faltan columnas"),
            ("numero_documento,nombre\n\"sin cerrar,1", "comilla"),
            (new string('x', 1_000_001), "demasiado grande"),
        })
        {
            var response = await ImportAsync(owner, "customers", csv, commit: true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("SF-IMP-001", await CodeAsync(response));
            Assert.Contains(fragment, await DetailAsync(response), StringComparison.Ordinal);
        }

        var tooMany = "numero_documento,nombre\n" + string.Concat(Enumerable.Range(0, 2001).Select(i => $"{i},x\n"));
        Assert.Equal("SF-IMP-001", await CodeAsync(await ImportAsync(owner, "customers", tooMany, commit: true)));
        Assert.Equal(0, await CountAsync(owner, "customers"));
    }

    [Fact]
    public async Task A_customer_of_another_tenant_is_not_an_existing_one_and_nobody_sees_the_import_of_another()
    {
        var first = await NewOwnerAsync("Import Aislado A");
        var second = await NewOwnerAsync("Import Aislado B");
        var csv = $"tipo_documento,numero_documento,nombre\n6,{Ruc(7)},Compartido SAC\n";

        Assert.Equal(1, (await ResultOfAsync(await ImportAsync(first, "customers", csv, commit: true))).Created);
        var other = await ResultOfAsync(await ImportAsync(second, "customers", csv, commit: false));

        Assert.Equal((1, 0), (other.Ready, other.Existing)); // the first tenant's customer does not count for the second
        Assert.Equal(1, (await ResultOfAsync(await ImportAsync(second, "customers", csv, commit: true))).Created);
        Assert.Equal(1, await CountAsync(first, "customers"));
        Assert.Equal(1, await CountAsync(second, "customers"));
    }

    [Fact]
    public async Task Only_who_manages_customers_imports_them_and_the_audit_records_the_import_without_the_file()
    {
        var (tenantId, owner) = await NewTenantAsync("Import Permisos");
        using var admin = await api.AdminClientAsync();
        var viewer = await ApiFixture.CreateUserAsync(admin, Roles.Accountant, tenantId);
        using var reader = api.ClientFor(await api.LoginOkAsync(viewer.Email, viewer.Password));
        var csv = $"tipo_documento,numero_documento,nombre\n6,{Ruc(8)},Auditada SAC\n";

        Assert.Equal(HttpStatusCode.Forbidden, (await ImportAsync(reader, "customers", csv, commit: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ImportAsync(reader, "products", "codigo,descripcion,valor_unitario,afectacion_igv\nA,B,1,10\n", commit: false)).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ImportAsync(anonymous, "customers", csv, commit: false)).StatusCode);

        await ResultOfAsync(await ImportAsync(owner, "customers", csv, commit: true));
        var events = await owner.GetStringAsync("/api/v1/audit?action=customers.customer.imported");
        Assert.Contains("customers.customer.imported", events, StringComparison.Ordinal);
        Assert.Contains("fileSha256", events, StringComparison.Ordinal);
        Assert.DoesNotContain("Auditada SAC", events, StringComparison.Ordinal);
    }

    // ---------- products ----------

    [Fact]
    public async Task Products_are_previewed_then_created_with_the_usual_unit_of_their_kind()
    {
        var owner = await NewOwnerAsync("Import Productos");
        var csv = string.Join('\n',
            "codigo,descripcion,tipo,unidad,valor_unitario,afectacion_igv,categoria",
            "SKU-1,Cuaderno A4,bien,,12.50,10,Útiles",
            "SKU-2,Instalación,servicio,,300,10,",
            "SKU-3,Arroz por kilo,bien,KGM,4.2,20,Abarrotes");

        var preview = await ResultOfAsync(await ImportAsync(owner, "products", csv, commit: false));
        Assert.Equal((3, 3), (preview.Total, preview.Ready));
        Assert.Equal(0, await CountAsync(owner, "products"));

        Assert.Equal(3, (await ResultOfAsync(await ImportAsync(owner, "products", csv, commit: true))).Created);
        var listed = (await owner.GetFromJsonAsync<List<ListedProduct>>("/api/v1/products?take=200", ApiFixture.JsonOptions))!.ToDictionary(p => p.InternalCode);
        Assert.Equal(("NIU", 12.50m, "10"), (listed["SKU-1"].UnitCode, listed["SKU-1"].UnitValue, listed["SKU-1"].IgvAffectationCode));
        Assert.Equal(("ZZ", "Service"), (listed["SKU-2"].UnitCode, listed["SKU-2"].Kind));
        Assert.Equal(("KGM", 4.2m, "20"), (listed["SKU-3"].UnitCode, listed["SKU-3"].UnitValue, listed["SKU-3"].IgvAffectationCode));

        var again = await ResultOfAsync(await ImportAsync(owner, "products", csv, commit: true));
        Assert.Equal((0, 3), (again.Created, again.Existing));
    }

    [Fact]
    public async Task The_tax_treatment_is_never_assumed_and_each_bad_product_row_says_why()
    {
        var owner = await NewOwnerAsync("Import Prod Errores");

        var withoutAffectation = await ImportAsync(owner, "products", "codigo,descripcion,valor_unitario\nA,B,1\n", commit: true);
        Assert.Equal("SF-IMP-001", await CodeAsync(withoutAffectation)); // no default tax treatment: the column is required

        var csv = string.Join('\n',
            "codigo;descripcion;tipo;valor_unitario;afectacion_igv",
            "OK-1;Bueno;bien;10,5;10", //            line 2: valid, with the decimal comma of Excel in Spanish
            "OK-1;Repetido;bien;10;10", //           line 3: repeated code
            "BAD 2;Código con espacio;bien;10;10", //  line 4: invalid code
            "OK-3;Valor negativo;bien;-1;10", //     line 5: negative
            "OK-4;Valor de texto;bien;diez;10", //   line 6: not a number
            "OK-5;Afectación inexistente;bien;1;99", // line 7: not in catalogue 07
            "OK-6;Tipo raro;cosa;1;10", //           line 8: unknown kind
            "OK-7;;bien;1;10"); //                    line 9: no description

        var result = await ResultOfAsync(await ImportAsync(owner, "products", csv, commit: true));

        Assert.Equal((8, 1, 7), (result.Total, result.Created, result.Invalid));
        var byLine = result.Rows.ToDictionary(r => r.Line);
        Assert.Equal("Created", byLine[2].Status);
        Assert.Contains("línea 2", byLine[3].Message, StringComparison.Ordinal);
        Assert.Contains("código", byLine[4].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valor", byLine[5].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("número", byLine[6].Message, StringComparison.Ordinal);
        Assert.Contains("afectación", byLine[7].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bien", byLine[8].Message, StringComparison.Ordinal);
        Assert.Contains("descripción", byLine[9].Message, StringComparison.OrdinalIgnoreCase);
        var saved = (await owner.GetFromJsonAsync<List<ListedProduct>>("/api/v1/products?take=200", ApiFixture.JsonOptions))!.Single();
        Assert.Equal(10.5m, saved.UnitValue);
    }

    [Fact]
    public async Task Quoted_cells_with_commas_and_line_breaks_arrive_whole_and_a_byte_order_mark_is_ignored()
    {
        var owner = await NewOwnerAsync("Import Comillas");
        var csv = "﻿codigo,descripcion,valor_unitario,afectacion_igv\r\nQ-1,\"Caja de 12, con \"\"asa\"\"\",5,10\r\nQ-2,\"Dos\nlíneas\",6,10\r\n";

        var result = await ResultOfAsync(await ImportAsync(owner, "products", csv, commit: true));

        Assert.Equal(2, result.Created);
        var saved = (await owner.GetFromJsonAsync<List<ListedProduct>>("/api/v1/products?take=200", ApiFixture.JsonOptions))!.ToDictionary(p => p.InternalCode);
        Assert.Equal("Caja de 12, con \"asa\"", saved["Q-1"].Description);
        Assert.Equal("Dos\nlíneas", saved["Q-2"].Description);
    }
}
