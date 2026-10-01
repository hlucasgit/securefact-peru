using SecureFact.Customers.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Products.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class MasterDataEndpoints
{
    public static void MapMasterDataEndpoints(this IEndpointRouteBuilder app)
    {
        var customers = app.MapGroup("/api/v1/customers").WithTags("Customers");

        customers.MapGet(string.Empty, async (string? search, bool? includeInactive, int? skip, int? take, ICustomerAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(search, includeInactive ?? false, skip ?? 0, take ?? 50, ct))).RequireAuthorization(Permissions.CustomersRead);

        customers.MapGet("/{id:guid}", async (Guid id, ICustomerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CustomersRead);

        customers.MapPost(string.Empty, async (CustomerDetails body, ICustomerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/customers/{dto.Id}", dto))).RequireAuthorization(Permissions.CustomersManage);

        customers.MapPut("/{id:guid}", async (Guid id, CustomerDetails body, ICustomerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UpdateAsync(id, body, ct)).ToHttp(http)).RequireAuthorization(Permissions.CustomersManage);

        customers.MapPost("/{id:guid}/deactivate", async (Guid id, ICustomerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.CustomersManage);

        var products = app.MapGroup("/api/v1/products").WithTags("Products");

        products.MapGet(string.Empty, async (string? search, bool? includeInactive, int? skip, int? take, IProductAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(search, includeInactive ?? false, skip ?? 0, take ?? 50, ct))).RequireAuthorization(Permissions.ProductsRead);

        products.MapGet("/{id:guid}", async (Guid id, IProductAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.ProductsRead);

        products.MapPost(string.Empty, async (ProductDetails body, IProductAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/products/{dto.Id}", dto))).RequireAuthorization(Permissions.ProductsManage);

        products.MapPut("/{id:guid}", async (Guid id, ProductDetails body, IProductAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UpdateAsync(id, body, ct)).ToHttp(http)).RequireAuthorization(Permissions.ProductsManage);

        products.MapPost("/{id:guid}/deactivate", async (Guid id, IProductAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.ProductsManage);
    }
}
