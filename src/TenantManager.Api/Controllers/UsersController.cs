using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Accounts;
using TenantManager.Application.Common;

namespace TenantManager.Api.Controllers;

/// <summary>Staff user management. Admin only; admins cannot see tenant or property data.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
[Authorize(Policy = Policies.Admin)]
public sealed class UsersController(IUserAccountService accounts, IValidator<SetRoleRequest> roleValidator)
    : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<UserDto>> List([FromQuery] UserListQuery query, CancellationToken ct) =>
        accounts.ListAsync(query, ct);

    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var user = await accounts.CreateAsync(request, ct);
        return Created($"/api/v1/users/{user.Id}", user);
    }

    [HttpPut("{id:guid}/role")]
    public async Task<UserDto> SetRole(Guid id, SetRoleRequest request, CancellationToken ct)
    {
        await roleValidator.ValidateAndThrowAsync(request, ct);
        return await accounts.SetRoleAsync(id, request.Role, ct);
    }

    [HttpPut("{id:guid}/lock")]
    public Task<UserDto> SetLock(Guid id, SetLockRequest request, CancellationToken ct) =>
        accounts.SetLockedAsync(id, request.Locked, ct);
}
