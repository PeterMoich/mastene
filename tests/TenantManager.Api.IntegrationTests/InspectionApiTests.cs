using System.Net;
using Shouldly;
using TenantManager.Application.Inspections;
using TenantManager.Application.Leases;
using TenantManager.Domain.Inspections;

namespace TenantManager.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class InspectionApiTests(ApiFactory api)
{
    private async Task<(LeaseScenario S, LeaseDto Lease)> RunningLeaseAsync()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        return (s, await s.ActiveAsync(LeaseScenario.Today.AddMonths(-1)));
    }

    private static async Task<InspectionDto> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return await response.ReadAsync<InspectionDto>();
    }

    private static async Task<InspectionDto> StartAsync(LeaseScenario s, Guid leaseId, InspectionKind kind)
    {
        var response = await s.Landlord.PostJsonAsync($"/api/v1/leases/{leaseId}/inspections", new StartInspectionRequest(kind));
        await response.ShouldBeAsync(HttpStatusCode.Created);
        return await response.ReadAsync<InspectionDto>();
    }

    private static async Task<InspectionDto> CompleteAllGoodAsync(LeaseScenario s, InspectionDto inspection)
    {
        foreach (var item in inspection.Items.Where(i => i.Condition is null))
            await OkAsync(s.Landlord.PutJsonAsync($"/api/v1/inspections/{inspection.Id}/items/{item.Id}",
                new InspectionItemRequest(ItemCondition.Good, null)));
        return await OkAsync(s.Landlord.PostAsync($"/api/v1/inspections/{inspection.Id}/complete", null));
    }

    [Fact]
    public async Task Move_in_is_filled_in_completed_and_acknowledged_by_the_tenant()
    {
        var (s, lease) = await RunningLeaseAsync();
        var tenant = await s.TenantClientAsync();

        var draft = await StartAsync(s, lease.Id, InspectionKind.MoveIn);
        draft.Status.ShouldBe(InspectionStatus.Draft);
        draft.Items.Count.ShouldBe(Inspection.DefaultAreas.Count);
        draft.AllowedActions.ShouldBe(["edit", "complete"]);

        // The tenant doesn't see a draft.
        (await tenant.GetAsync($"/api/v1/inspections/{draft.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await (await s.Landlord.PostAsync($"/api/v1/inspections/{draft.Id}/complete", null))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "inspection.items_unchecked");
        var withGarage = await OkAsync(s.Landlord.PostJsonAsync($"/api/v1/inspections/{draft.Id}/items",
            new InspectionAreaRequest("Garage")));
        var garage = withGarage.Items.Single(i => i.Area == "Garage");
        await (await s.Landlord.UploadPhotoAsync($"/api/v1/inspections/{draft.Id}/items/{garage.Id}/photos",
            IssueHelpers.UniquePng())).ShouldBeAsync(HttpStatusCode.OK);

        var completed = await CompleteAllGoodAsync(s, withGarage);
        completed.Status.ShouldBe(InspectionStatus.Completed);
        await (await s.Landlord.PutJsonAsync($"/api/v1/inspections/{draft.Id}/items/{garage.Id}",
                new InspectionItemRequest(ItemCondition.Poor, "Changed my mind")))
            .ShouldBeAsync(HttpStatusCode.Conflict, "inspection.completed");

        var seen = await (await tenant.GetAsync($"/api/v1/inspections/{draft.Id}")).ReadAsync<InspectionDto>();
        seen.AllowedActions.ShouldBe(["acknowledge"]);
        var photoId = seen.Items.Single(i => i.Area == "Garage").Photos.Single().Id;
        (await tenant.GetAsync($"/api/v1/inspections/{draft.Id}/photos/{photoId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var acknowledged = await OkAsync(tenant.PostJsonAsync($"/api/v1/inspections/{draft.Id}/acknowledge",
            new AcknowledgeInspectionRequest("Garage door was already dented")));
        acknowledged.TenantAcknowledgedAt.ShouldNotBeNull();
        acknowledged.TenantComment.ShouldBe("Garage door was already dented");
        await (await tenant.PostJsonAsync($"/api/v1/inspections/{draft.Id}/acknowledge", new AcknowledgeInspectionRequest(null)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "inspection.already_acknowledged");
    }

    [Fact]
    public async Task Move_out_uses_the_move_in_areas_and_shows_what_each_was_like()
    {
        var (s, lease) = await RunningLeaseAsync();
        var moveIn = await StartAsync(s, lease.Id, InspectionKind.MoveIn);
        var lounge = moveIn.Items.Single(i => i.Area == "Lounge");
        moveIn = await OkAsync(s.Landlord.PutJsonAsync($"/api/v1/inspections/{moveIn.Id}/items/{lounge.Id}",
            new InspectionItemRequest(ItemCondition.Fair, "Small scuff by the door")));
        moveIn = await CompleteAllGoodAsync(s, moveIn);

        var moveOut = await StartAsync(s, lease.Id, InspectionKind.MoveOut);

        moveOut.Kind.ShouldBe(InspectionKind.MoveOut);
        moveOut.MoveInInspectionId.ShouldBe(moveIn.Id);
        moveOut.Items.Select(i => i.Area).ShouldBe(moveIn.Items.Select(i => i.Area));
        var loungeOut = moveOut.Items.Single(i => i.Area == "Lounge");
        loungeOut.MoveInCondition.ShouldBe(ItemCondition.Fair);
        loungeOut.MoveInNotes.ShouldBe("Small scuff by the door");
        loungeOut.Condition.ShouldBeNull();

        await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{lease.Id}/inspections", new StartInspectionRequest(InspectionKind.MoveOut)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "inspection.exists");

        var list = await (await s.Landlord.GetAsync($"/api/v1/leases/{lease.Id}/inspections")).ReadAsync<LeaseInspectionsDto>();
        list.Items.Count.ShouldBe(2);
        list.AllowedActions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_landlord_fills_in_inspections_and_tenants_only_see_their_own()
    {
        var (s, lease) = await RunningLeaseAsync();
        var tenant = await s.TenantClientAsync();
        (await tenant.PostJsonAsync($"/api/v1/leases/{lease.Id}/inspections", new StartInspectionRequest(InspectionKind.MoveIn)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var completed = await CompleteAllGoodAsync(s, await StartAsync(s, lease.Id, InspectionKind.MoveIn));
        var other = await api.ClientForAsync(Seeded.OtherTenantWithLogin);
        (await other.GetAsync($"/api/v1/inspections/{completed.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/v1/leases/{lease.Id}/inspections")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_draft_lease_has_no_inspection()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var draft = await s.DraftAsync(LeaseScenario.Today.AddMonths(1));

        await (await s.Landlord.PostJsonAsync($"/api/v1/leases/{draft.Id}/inspections", new StartInspectionRequest(InspectionKind.MoveIn)))
            .ShouldBeAsync(HttpStatusCode.Conflict, "inspection.lease_not_in_force");
    }
}
