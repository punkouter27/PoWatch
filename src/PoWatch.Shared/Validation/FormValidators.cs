using FluentValidation;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Shared.Validation;

/// <summary>
/// The rules every PoWatch form shares with the endpoint it posts to: the browser shows them inline
/// next to the field (Client/Layout/FluentValidationValidator), and the API answers a request that
/// breaks them with a 400 ValidationProblem.
/// </summary>
public sealed class RuleDraftRequestValidator : AbstractValidator<RuleDraftRequestDto>
{
    public RuleDraftRequestValidator()
    {
        RuleFor(r => r.Text)
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Describe the rule first, e.g. a dog at night.")
            .Must(t => t is null || t.Trim().Length <= RuleDraftRequestDto.MaxLength)
            .WithMessage($"Keep it under {RuleDraftRequestDto.MaxLength} characters.");
    }
}

public sealed class WatchRuleInputValidator : AbstractValidator<WatchRuleInputDto>
{
    public WatchRuleInputValidator()
    {
        RuleFor(r => r.Class)
            .Must(c => c == WatchRuleInputDto.Any || (c is not null && DetectorLabels.Coco.Contains(c)))
            .WithMessage("Pick what to watch for.");
        RuleFor(r => r.FromHour).InclusiveBetween(0, 23).WithMessage("Use an hour from 0 to 23.");
        RuleFor(r => r.ToHour).InclusiveBetween(0, 23).WithMessage("Use an hour from 0 to 23.");
    }
}
