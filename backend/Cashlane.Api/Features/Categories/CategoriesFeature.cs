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

public sealed record CategoryDto(Guid Id, string Name, CategoryType Type, string Color, string Icon, bool IsArchived);
public sealed record SaveCategoryRequest(string Name, CategoryType Type, string Color, string Icon, bool IsArchived);

public interface ICategoryService
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken cancellationToken = default);
    Task<CategoryDto> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken = default);
    Task ArchiveCategoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class CategoryService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService) : UserScopedService(currentUserService), ICategoryService
{
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        return await dbContext.Categories
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Type)
            .ThenBy(x => x.Name)
            .Select(x => new CategoryDto(x.Id, x.Name, x.Type, x.Color, x.Icon, x.IsArchived))
            .ToListAsync(cancellationToken);
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid category", "Category name is required.");
        }

        var category = new Category
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Type = request.Type,
            Color = string.IsNullOrWhiteSpace(request.Color) ? "#1F9D74" : request.Color,
            Icon = string.IsNullOrWhiteSpace(request.Icon) ? "circle" : request.Icon,
            IsArchived = request.IsArchived
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.created", nameof(Category), category.Id, new { category.Name }, cancellationToken);

        return new CategoryDto(category.Id, category.Name, category.Type, category.Color, category.Icon, category.IsArchived);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        category.Name = request.Name.Trim();
        category.Type = request.Type;
        category.Color = request.Color;
        category.Icon = request.Icon;
        category.IsArchived = request.IsArchived;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.updated", nameof(Category), category.Id, new { category.Name }, cancellationToken);

        return new CategoryDto(category.Id, category.Name, category.Type, category.Color, category.Icon, category.IsArchived);
    }

    public async Task ArchiveCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        category.IsArchived = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.archived", nameof(Category), category.Id, new { category.Name }, cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        if (!category.IsArchived)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Category not archived", "Archive the category before deleting it permanently.");
        }

        await using var dbTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var clearedTransactions = await dbContext.Transactions
            .Where(x => x.UserId == userId && x.CategoryId == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CategoryId, x => (Guid?)null), cancellationToken);

        var clearedRecurringItems = await dbContext.RecurringTransactions
            .Where(x => x.UserId == userId && x.CategoryId == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CategoryId, x => (Guid?)null), cancellationToken);

        var deletedBudgets = await dbContext.Budgets
            .Where(x => x.UserId == userId && x.CategoryId == id)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("category.deleted", nameof(Category), category.Id, new
        {
            category.Name,
            deletedBudgets,
            clearedTransactions,
            clearedRecurringItems
        }, cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);
    }
}

[ApiController]
[Authorize]
[Route("api/categories")]
public sealed class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<CategoryDto>> GetCategories(CancellationToken cancellationToken)
        => categoryService.GetCategoriesAsync(cancellationToken);

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
