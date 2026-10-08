using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Gre.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The roads of the guide that the main tests do not walk: lists, closed series, answers of the ticket that cannot be used, and missing credentials.</summary>
public sealed partial class GreApiTests
{
    private async Task<GreDto> MakePendingAsync(Setup setup, GreDto guide, string reference = "T001-1")
    {
        await api.Postgres.ExecuteAsOwnerAsync(
            $"UPDATE gre.guide SET state = 'Pending', ticket = 'SBX.' || translate(encode(convert_to('{setup.Company.Ruc}/{reference}/A', 'UTF8'), 'base64'), '+/=', '-_'), next_attempt_at = now() - interval '1 hour' WHERE id = '{guide.Id}'");
        return await ReadAsync(setup.Owner, guide.Id);
    }

    private static async Task<GreDto> RefreshAsync(Setup setup, Guid id)
    {
        var response = await setup.Owner.PostAsync($"/api/v1/gre/guides/{id}/refresh", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
    }

    [Fact]
    public async Task The_list_filters_by_company_and_by_state()
    {
        var setup = await NewTenantAsync("gre-list");
        var accepted = await CreateOkAsync(setup);
        await setup.Owner.PostAsync($"/api/v1/gre/guides/{accepted.Id}/submit", null);
        var prepared = await CreateOkAsync(setup);

        var all = await setup.Owner.GetFromJsonAsync<GreDto[]>($"/api/v1/gre/guides?companyId={setup.Company.Id}", ApiFixture.JsonOptions);
        var onlyPrepared = await setup.Owner.GetFromJsonAsync<GreDto[]>($"/api/v1/gre/guides?companyId={setup.Company.Id}&state=Prepared", ApiFixture.JsonOptions);
        var elsewhere = await setup.Owner.GetFromJsonAsync<GreDto[]>($"/api/v1/gre/guides?companyId={Guid.NewGuid()}", ApiFixture.JsonOptions);

        Assert.Equal(2, all!.Length);
        Assert.Equal(prepared.Id, Assert.Single(onlyPrepared!).Id);
        Assert.Empty(elsewhere!);
    }

    [Fact]
    public async Task A_deactivated_series_cannot_issue_and_an_unknown_one_is_not_found()
    {
        var setup = await NewTenantAsync("gre-deactivate");

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/gre/series/{setup.Series.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.PostAsync($"/api/v1/gre/series/{Guid.NewGuid()}/deactivate", null)).StatusCode);

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Guide(setup));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("SF-GRE-006", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task A_guide_needs_a_company_that_exists_and_an_active_certificate()
    {
        var setup = await NewTenantAsync("gre-no-cert", certificate: false);

        var noCertificate = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Guide(setup));
        Assert.False(noCertificate.IsSuccessStatusCode);

        var missingCompany = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", new
        {
            companyId = Guid.NewGuid(),
            seriesId = setup.Series.Id,
            motiveCode = "01",
            modalityCode = "02",
            recipient = new { documentTypeCode = "6", documentNumber = "20100070970", name = "X" },
            origin = new { ubigeoCode = "150101", address = "Av. Argentina 123" },
            destination = new { ubigeoCode = "150122", address = "Calle Los Pinos 456" },
            goods = new[] { new { description = "Caja", unitCode = "NIU", quantity = 1m } },
        });
        Assert.Equal(HttpStatusCode.NotFound, missingCompany.StatusCode);
    }

    [Fact]
    public async Task A_series_that_reached_its_last_number_refuses_the_next_guide()
    {
        var setup = await NewTenantAsync("gre-exhausted");
        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.series SET last_number = 99999999 WHERE id = '{setup.Series.Id}'");

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Guide(setup));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-GRE-010", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task A_guide_that_SUNAT_already_has_is_not_sent_twice_and_a_final_one_is_not_asked_again()
    {
        var setup = await NewTenantAsync("gre-idempotent");
        var guide = await MakePendingAsync(setup, await CreateOkAsync(setup));
        Assert.Equal(GreState.Pending, guide.State);

        var again = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty(api.Gre.Submissions);

        var closed = await RefreshAsync(setup, guide.Id);
        Assert.Equal(GreState.Accepted, closed.State);
        var queries = api.Gre.Queries.Count;
        Assert.Equal(GreState.Accepted, (await RefreshAsync(setup, guide.Id)).State);
        Assert.Equal(queries, api.Gre.Queries.Count);
    }

    [Fact]
    public async Task Asking_for_a_guide_that_was_not_sent_or_does_not_exist_is_refused()
    {
        var setup = await NewTenantAsync("gre-refresh-state");
        var prepared = await CreateOkAsync(setup);

        var notSent = await setup.Owner.PostAsync($"/api/v1/gre/guides/{prepared.Id}/refresh", null);
        Assert.Equal(HttpStatusCode.Conflict, notSent.StatusCode);
        Assert.Equal("SF-GRE-004", await ProblemCodeAsync(notSent));
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.PostAsync($"/api/v1/gre/guides/{Guid.NewGuid()}/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.GetAsync($"/api/v1/gre/guides/{prepared.Id}/cdr")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.GetAsync($"/api/v1/gre/guides/{Guid.NewGuid()}/cdr")).StatusCode);
    }

    [Fact]
    public async Task The_answer_of_a_ticket_that_is_not_ready_or_not_usable_keeps_the_guide_waiting()
    {
        var setup = await NewTenantAsync("gre-waiting");
        var guide = await MakePendingAsync(setup, await CreateOkAsync(setup));

        api.Gre.EnqueueQuery(new GreTicketOutcome(GreTicketStatus.InProcess, null, null, null));
        var inProcess = await RefreshAsync(setup, guide.Id);
        api.Gre.EnqueueQuery(new GreTicketOutcome(GreTicketStatus.Done, null, null, null));
        var withoutCdr = await RefreshAsync(setup, guide.Id);
        api.Gre.EnqueueQuery(new GreTicketOutcome(GreTicketStatus.Done, [1, 2, 3], null, null));
        var garbage = await RefreshAsync(setup, guide.Id);

        Assert.All([inProcess, withoutCdr, garbage], g => Assert.Equal(GreState.Pending, g.State));
        Assert.Equal([1, 2, 3], [inProcess.Attempts, withoutCdr.Attempts, garbage.Attempts]);
    }

    [Fact]
    public async Task A_CDR_for_another_guide_is_not_applied()
    {
        var setup = await NewTenantAsync("gre-wrong-cdr");
        var guide = await MakePendingAsync(setup, await CreateOkAsync(setup), reference: "T001-99");

        var result = await RefreshAsync(setup, guide.Id);

        Assert.Equal(GreState.Pending, result.State);
        Assert.Equal("SF-GRE-CDR", result.ErrorCode);
        Assert.Null(result.CdrResponseCode);
    }

    [Fact]
    public async Task A_ticket_that_ends_with_an_error_and_no_CDR_fails_the_guide()
    {
        var setup = await NewTenantAsync("gre-ticket-error");
        var guide = await MakePendingAsync(setup, await CreateOkAsync(setup));
        api.Gre.EnqueueQuery(new GreTicketOutcome(GreTicketStatus.Error, null, "2800", "Rechazada por SUNAT."));

        var result = await RefreshAsync(setup, guide.Id);

        Assert.Equal(GreState.Failed, result.State);
        Assert.Equal("2800", result.ErrorCode);
    }

    [Fact]
    public async Task Without_credentials_the_status_of_a_ticket_cannot_be_asked()
    {
        var withoutApi = await NewTenantAsync("gre-refresh-noapi", apiCredentials: false);
        var guide = await MakePendingAsync(withoutApi, await CreateOkAsync(withoutApi));
        var noApi = await withoutApi.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/refresh", null);
        Assert.Equal(HttpStatusCode.Conflict, noApi.StatusCode);
        Assert.Equal("SF-GRE-008", await ProblemCodeAsync(noApi));

        var withoutSol = await NewTenantAsync("gre-refresh-nosol", solCredentials: false);
        var other = await MakePendingAsync(withoutSol, await CreateOkAsync(withoutSol));
        var noSol = await withoutSol.Owner.PostAsync($"/api/v1/gre/guides/{other.Id}/refresh", null);
        Assert.False(noSol.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_worker_leaves_a_guide_it_cannot_move_and_goes_on()
    {
        var setup = await NewTenantAsync("gre-worker-skip", apiCredentials: false);
        var guide = await MakePendingAsync(setup, await CreateOkAsync(setup));

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var changed = await scope.ServiceProvider.GetRequiredService<IGreWorkProcessor>().RunOnceAsync(CancellationToken.None);
            Assert.True(changed >= 0);
        }

        Assert.Equal(GreState.Pending, (await ReadAsync(setup.Owner, guide.Id)).State);
    }

    [Fact]
    public async Task A_company_that_is_inactive_takes_no_new_series()
    {
        var setup = await NewTenantAsync("gre-inactive-company");
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/companies/{setup.Company.Id}/deactivate", null)).StatusCode);

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/series", new { companyId = setup.Company.Id, code = "T002" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-GRE-005", await ProblemCodeAsync(response));
    }
}
