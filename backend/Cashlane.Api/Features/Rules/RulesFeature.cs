using System.Net;
using System.Text.Json;
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

namespace Cashlane.Api.Features.Rules;

public sealed record RuleConditionDto(string Field, string Operator, string Value);
public sealed record RuleActionDto(string Type, string Value);
public sealed record RuleDto(Guid Id, Guid? AccountId, bool IsActive, int Priority, RuleConditionDto Condition, RuleActionDto Action);
public sealed record SaveRuleRequest(Guid? AccountId, bool IsActive, int Priority, RuleConditionDto Condition, RuleActionDto Action);
public sealed record RuleSimulationRequest(Guid? AccountId, Guid? CategoryId, decimal Amount, string Merchant, string PaymentMethod, string[]? Tags);
public sealed record RuleSimulationResponse(Guid? CategoryId, string[] Tags, IReadOnlyList<string> Alerts, IReadOnlyList<Guid> AppliedRuleIds);
public sealed record RuleEvaluationInput(Guid UserId, Guid AccountId, Guid? CategoryId, decimal Amount, string Merchant, string PaymentMethod, string[] Tags);
public sealed record RuleEvaluationResult(Guid? CategoryId, string[] Tags, IReadOnlyList<string> Alerts, IReadOnlyList<Guid> AppliedRuleIds);

public interface IRuleService
{
    Task<IReadOnlyList<RuleDto>> GetRulesAsync(Guid? accountId, CancellationToken cancellationToken = default);
    Task<RuleDto> CreateRuleAsync(SaveRuleRequest request, CancellationToken cancellationToken = default);
    Task<RuleDto> UpdateRuleAsync(Guid id, SaveRuleRequest request, CancellationToken cancellationToken = default);
    Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RuleSimulationResponse> SimulateAsync(RuleSimulationRequest request, CancellationToken cancellationToken = default);
    Task<RuleEvaluationResult> EvaluateAsync(RuleEvaluationInput input, CancellationToken cancellationToken = default);
}

public sealed class RuleService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAccountAccessService accountAccessService,
    IAuditLogService auditLogService) : UserScopedService(currentUserService), IRuleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RuleDto>> GetRulesAsync(Guid? accountId, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await EnsureManageScopeAsync(accountId, cancellationToken);

        var query = dbContext.Rules
            .AsNoTracking()
            .Where(x => x.AccountId == accountId);

        if (accountId is null)
        {
            query = query.Where(x => x.UserId == userId);
        }

        return await query
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAtUtc)
            .Select(x => x.ToDto())
            .ToListAsync(cancellationToken);
    }

    public async Task<RuleDto> CreateRuleAsync(SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await ValidateRequestAsync(request, userId, cancellationToken);

        var rule = new Rule
        {
            UserId = userId,
            AccountId = request.AccountId,
            IsActive = request.IsActive,
            Priority = Math.Clamp(request.Priority, 1, 9999),
            ConditionJson = JsonSerializer.Serialize(request.Condition, JsonOptions),
            ActionJson = JsonSerializer.Serialize(request.Action, JsonOptions)
        };

        dbContext.Rules.Add(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            "rule.created",
            nameof(Rule),
            rule.Id,
            new { rule.AccountId, rule.Priority, request.Condition.Field, request.Action.Type },
            cancellationToken,
            request.AccountId);

        return rule.ToDto();
    }

    public async Task<RuleDto> UpdateRuleAsync(Guid id, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await ValidateRequestAsync(request, userId, cancellationToken);

        var rule = await dbContext.Rules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Rule not found", "The requested rule does not exist.");

        await EnsureManageScopeAsync(rule.AccountId, cancellationToken);

        rule.AccountId = request.AccountId;
        rule.IsActive = request.IsActive;
        rule.Priority = Math.Clamp(request.Priority, 1, 9999);
        rule.ConditionJson = JsonSerializer.Serialize(request.Condition, JsonOptions);
        rule.ActionJson = JsonSerializer.Serialize(request.Action, JsonOptions);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            "rule.updated",
            nameof(Rule),
            rule.Id,
            new { rule.AccountId, rule.Priority, request.Condition.Field, request.Action.Type },
            cancellationToken,
            request.AccountId);

        return rule.ToDto();
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await dbContext.Rules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Rule not found", "The requested rule does not exist.");

        await EnsureManageScopeAsync(rule.AccountId, cancellationToken);

        dbContext.Rules.Remove(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            "rule.deleted",
            nameof(Rule),
            rule.Id,
            new { rule.AccountId, rule.Priority },
            cancellationToken,
            rule.AccountId);
    }

    public async Task<RuleSimulationResponse> SimulateAsync(RuleSimulationRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await EnsureSimulationScopeAsync(request.AccountId, cancellationToken);

        var evaluation = await EvaluateAsync(
            new RuleEvaluationInput(
                userId,
                request.AccountId ?? Guid.Empty,
                request.CategoryId,
                request.Amount,
                request.Merchant,
                request.PaymentMethod,
                request.Tags ?? Array.Empty<string>()),
            cancellationToken);

        return new RuleSimulationResponse(evaluation.CategoryId, evaluation.Tags, evaluation.Alerts, evaluation.AppliedRuleIds);
    }

    public async Task<RuleEvaluationResult> EvaluateAsync(RuleEvaluationInput input, CancellationToken cancellationToken = default)
    {
        var rules = await LoadScopeRulesAsync(input.UserId, input.AccountId, cancellationToken);
        var categoryId = input.CategoryId;
        var tags = input.Tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var alerts = new List<string>();
        var appliedRuleIds = new List<Guid>();
        var categoryAlreadySet = false;

        foreach (var rule in rules)
        {
            var condition = DeserializeCondition(rule.ConditionJson);
            if (!ConditionMatches(condition, input, categoryId))
            {
                continue;
            }

            appliedRuleIds.Add(rule.Id);
            var action = DeserializeAction(rule.ActionJson);
            switch (action.Type.Trim().ToLowerInvariant())
            {
                case "set_category":
                    if (!categoryAlreadySet && Guid.TryParse(action.Value, out var nextCategoryId))
                    {
                        categoryId = nextCategoryId;
                        categoryAlreadySet = true;
                    }
                    break;
                case "add_tag":
                    if (!string.IsNullOrWhiteSpace(action.Value) && !tags.Contains(action.Value, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(action.Value.Trim());
                    }
                    break;
                case "trigger_alert":
                    if (!string.IsNullOrWhiteSpace(action.Value))
                    {
                        alerts.Add(action.Value.Trim());
                    }
                    break;
            }
        }

        return new RuleEvaluationResult(categoryId, tags.ToArray(), alerts, appliedRuleIds);
    }

    private async Task<IReadOnlyList<Rule>> LoadScopeRulesAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var useAccountScope = accountId != Guid.Empty && (
            await dbContext.AccountMembers.AnyAsync(x => x.AccountId == accountId, cancellationToken) ||
            await dbContext.Rules.AnyAsync(x => x.AccountId == accountId && x.IsActive, cancellationToken));
        var query = dbContext.Rules
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAtUtc);

        if (useAccountScope)
        {
            return await query.Where(x => x.AccountId == accountId).ToListAsync(cancellationToken);
        }

        return await query.Where(x => x.UserId == userId && x.AccountId == null).ToListAsync(cancellationToken);
    }

    private async Task EnsureManageScopeAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return;
        }

        await accountAccessService.EnsureAccessAsync(accountId.Value, AccountRole.Owner, cancellationToken);
    }

    private async Task EnsureSimulationScopeAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return;
        }

        await accountAccessService.EnsureAccessAsync(accountId.Value, AccountRole.Viewer, cancellationToken);
    }

    private async Task ValidateRequestAsync(SaveRuleRequest request, Guid userId, CancellationToken cancellationToken)
    {
        await EnsureManageScopeAsync(request.AccountId, cancellationToken);
        ValidateCondition(request.Condition);
        ValidateAction(request.Action);

        if (request.Action.Type.Equals("set_category", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(request.Action.Value, out var categoryId))
            {
                throw new AppException(HttpStatusCode.BadRequest, "Invalid rule", "Category action requires a valid category id.");
            }

            var exists = await dbContext.Categories.AnyAsync(
                x =>
                    x.Id == categoryId &&
                    !x.IsArchived &&
                    (request.AccountId == null
                        ? x.UserId == userId && x.AccountId == null
                        : x.AccountId == request.AccountId),
                cancellationToken);

            if (!exists)
            {
                throw new AppException(HttpStatusCode.BadRequest, "Invalid rule", "Rule category does not exist in the selected scope.");
            }
        }
    }

    private static void ValidateCondition(RuleConditionDto condition)
    {
        var field = condition.Field.Trim().ToLowerInvariant();
        var operation = condition.Operator.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(condition.Value))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid rule", "Rule condition value is required.");
        }

        var valid = field switch
        {
            "merchant" or "paymentmethod" => operation is "equals" or "contains",
            "amount" => operation is "equals" or "greaterthan" or "lessthan",
            "categoryid" => operation is "equals",
            _ => false
        };

        if (!valid)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid rule", "Unsupported rule condition.");
        }
    }

    private static void ValidateAction(RuleActionDto action)
    {
        var type = action.Type.Trim().ToLowerInvariant();
        if (type is not ("set_category" or "add_tag" or "trigger_alert"))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid rule", "Unsupported rule action.");
        }

        if (string.IsNullOrWhiteSpace(action.Value))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid rule", "Rule action value is required.");
        }
    }

    private static RuleConditionDto DeserializeCondition(string json)
        => JsonSerializer.Deserialize<RuleConditionDto>(json, JsonOptions)
           ?? throw new AppException(HttpStatusCode.InternalServerError, "Rule error", "Unable to read rule condition.");

    private static RuleActionDto DeserializeAction(string json)
        => JsonSerializer.Deserialize<RuleActionDto>(json, JsonOptions)
           ?? throw new AppException(HttpStatusCode.InternalServerError, "Rule error", "Unable to read rule action.");

    private static bool ConditionMatches(RuleConditionDto condition, RuleEvaluationInput input, Guid? currentCategoryId)
    {
        var op = condition.Operator.Trim().ToLowerInvariant();
        return condition.Field.Trim().ToLowerInvariant() switch
        {
            "merchant" => StringMatches(input.Merchant, condition.Value, op),
            "paymentmethod" => StringMatches(input.PaymentMethod, condition.Value, op),
            "amount" => DecimalMatches(input.Amount, condition.Value, op),
            "categoryid" => Guid.TryParse(condition.Value, out var categoryId) && currentCategoryId == categoryId,
            _ => false
        };
    }

    private static bool StringMatches(string actual, string expected, string operation)
    {
        actual ??= string.Empty;
        expected ??= string.Empty;

        return operation switch
        {
            "equals" => string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool DecimalMatches(decimal actual, string expected, string operation)
    {
        if (!decimal.TryParse(expected, out var threshold))
        {
            return false;
        }

        return operation switch
        {
            "equals" => actual == threshold,
            "greaterthan" => actual > threshold,
            "lessthan" => actual < threshold,
            _ => false
        };
    }
}

[ApiController]
[Authorize]
[Route("api/rules")]
public sealed class RulesController(IRuleService ruleService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<RuleDto>> GetRules([FromQuery] Guid? accountId, CancellationToken cancellationToken)
        => ruleService.GetRulesAsync(accountId, cancellationToken);

    [HttpPost]
    public Task<RuleDto> CreateRule([FromBody] SaveRuleRequest request, CancellationToken cancellationToken)
        => ruleService.CreateRuleAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<RuleDto> UpdateRule(Guid id, [FromBody] SaveRuleRequest request, CancellationToken cancellationToken)
        => ruleService.UpdateRuleAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<SimpleMessageResponse>> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        await ruleService.DeleteRuleAsync(id, cancellationToken);
        return Ok(new SimpleMessageResponse("Rule deleted."));
    }

    [HttpPost("simulate")]
    public Task<RuleSimulationResponse> Simulate([FromBody] RuleSimulationRequest request, CancellationToken cancellationToken)
        => ruleService.SimulateAsync(request, cancellationToken);
}

internal static class RuleMappings
{
    public static RuleDto ToDto(this Rule rule)
        => new(
            rule.Id,
            rule.AccountId,
            rule.IsActive,
            rule.Priority,
            JsonSerializer.Deserialize<RuleConditionDto>(rule.ConditionJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new RuleConditionDto("merchant", "equals", string.Empty),
            JsonSerializer.Deserialize<RuleActionDto>(rule.ActionJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new RuleActionDto("add_tag", string.Empty));
}
