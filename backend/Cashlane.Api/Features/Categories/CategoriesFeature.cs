using System.Net;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Categories;

public sealed record CategoryDto(Guid Id, string Name, CategoryType Type, string Color, string Icon, bool IsArchived, Guid? AccountId, bool IsShared);
public sealed record SaveCategoryRequest(string Name, CategoryType Type, string Color, string Icon, bool IsArchived, Guid? AccountId);

public interface ICategoryService
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(Guid? accountId, CancellationToken cancellationToken = default);
    Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken cancellationToken = default);
    Task<CategoryDto> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken = default);
    Task ArchiveCategoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class CategoryService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    IAccountAccessService accountAccessService) : UserScopedService(currentUserService), ICategoryService
{
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(Guid? accountId, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (accountId is not null)
        {
            await accountAccessService.EnsureAccessAsync(accountId.Value, AccountRole.Viewer, cancellationToken);
        }

        return await dbContext.Categories
            .Where(x =>
                accountId == null
                    ? x.UserId == userId && x.AccountId == null
                    : x.AccountId == accountId)
            .OrderBy(x => x.Type)
            .ThenBy(x => x.Name)
            .Select(x => new CategoryDto(x.Id, x.Name, x.Type, x.Color, x.Icon, x.IsArchived, x.AccountId, x.AccountId != null))
            .ToListAsync(cancellationToken);
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid category", "Category name is required.");
        }

        if (request.AccountId is not null)
        {
            await accountAccessService.EnsureAccessAsync(request.AccountId.Value, AccountRole.Owner, cancellationToken);
        }

        var category = new Category
        {
            UserId = userId,
            AccountId = request.AccountId,
            Name = request.Name.Trim(),
            Type = request.Type,
            Color = string.IsNullOrWhiteSpace(request.Color) ? "#1F9D74" : request.Color,
            Icon = string.IsNullOrWhiteSpace(request.Icon) ? "circle" : request.Icon,
            IsArchived = request.IsArchived
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.created", nameof(Category), category.Id, new { category.Name }, cancellationToken, category.AccountId);

        return category.ToDto();
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        await EnsureScopeAccessAsync(category, userId, cancellationToken);
        if (request.AccountId != category.AccountId)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid category", "Category scope cannot be changed.");
        }

        category.Name = request.Name.Trim();
        category.Type = request.Type;
        category.Color = request.Color;
        category.Icon = request.Icon;
        category.IsArchived = request.IsArchived;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.updated", nameof(Category), category.Id, new { category.Name }, cancellationToken, category.AccountId);

        return category.ToDto();
    }

    public async Task ArchiveCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        await EnsureScopeAccessAsync(category, userId, cancellationToken);

        category.IsArchived = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.archived", nameof(Category), category.Id, new { category.Name }, cancellationToken, category.AccountId);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        await EnsureScopeAccessAsync(category, userId, cancellationToken);
        if (!category.IsArchived)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Category not archived", "Archive the category before deleting it permanently.");
        }

        await using var dbTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var clearedTransactions = await dbContext.Transactions
            .Where(x => x.CategoryId == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CategoryId, x => (Guid?)null), cancellationToken);

        var clearedRecurringItems = await dbContext.RecurringTransactions
            .Where(x => x.CategoryId == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CategoryId, x => (Guid?)null), cancellationToken);

        var deletedBudgets = await dbContext.Budgets
            .Where(x => x.CategoryId == id)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            "category.deleted",
            nameof(Category),
            category.Id,
            new { category.Name, deletedBudgets, clearedTransactions, clearedRecurringItems },
            cancellationToken,
            category.AccountId);
        await dbTransaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureScopeAccessAsync(Category category, Guid userId, CancellationToken cancellationToken)
    {
        if (category.AccountId is null)
        {
            if (category.UserId != userId)
            {
                throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");
            }

            return;
        }

        await accountAccessService.EnsureAccessAsync(category.AccountId.Value, AccountRole.Owner, cancellationToken);
    }
}

[ApiController]
[Authorize]
[Route("api/categories")]
public sealed class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<CategoryDto>> GetCategories([FromQuery] Guid? accountId, CancellationToken cancellationToken)
        => categoryService.GetCategoriesAsync(accountId, cancellationToken);

    [HttpPost]
    public Task<CategoryDto> CreateCategory([FromBody] SaveCategoryRequest request, CancellationToken cancellationToken)
        => categoryService.CreateCategoryAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<CategoryDto> UpdateCategory(Guid id, [FromBody] SaveCategoryRequest request, CancellationToken cancellationToken)
        => categoryService.UpdateCategoryAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<SimpleMessageResponse>> ArchiveCategory(Guid id, CancellationToken cancellationToken)
    {
        await categoryService.ArchiveCategoryAsync(id, cancellationToken);
        return Ok(new SimpleMessageResponse("Category archived."));
    }

    [HttpDelete("{id:guid}/permanent")]
    public async Task<ActionResult<SimpleMessageResponse>> DeleteCategory(Guid id, CancellationToken cancellationToken)
    {
        await categoryService.DeleteCategoryAsync(id, cancellationToken);
        return Ok(new SimpleMessageResponse("Category deleted."));
    }
}

internal static class CategoryMappings
{
    public static CategoryDto ToDto(this Category category)
        => new(category.Id, category.Name, category.Type, category.Color, category.Icon, category.IsArchived, category.AccountId, category.AccountId != null);
}
