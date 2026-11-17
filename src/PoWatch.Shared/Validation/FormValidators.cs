using FluentValidation;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Shared.Validation;

/// <summary>
/// The rules every PoWatch form shares with the endpoint it posts to: the browser shows them inline
/// next to the field (Client/Layout/FluentValidationValidator), and the API answers a request that
/// breaks them with a 400 ValidationProblem.
/// </summary>
public sealed class AskRequestValidator : AbstractValidator<AskRequestDto>
{
    public AskRequestValidator()
    {
        RuleFor(r => r.Question)
            .Must(q => !string.IsNullOrWhiteSpace(q)).WithMessage("Type a question first.")
            .Must(q => q is null || q.Trim().Length <= AskRequestDto.MaxLength)
            .WithMessage($"Keep the question under {AskRequestDto.MaxLength} characters.");
    }
}

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

/// <summary>A regular's new name. Blank is allowed (it makes them anonymous again) unless <paramref name="requireName"/>.</summary>
public sealed class RenameRegularRequestValidator : AbstractValidator<RenameRegularRequestDto>
{
    public RenameRegularRequestValidator() : this(requireName: false) { }

    public RenameRegularRequestValidator(bool requireName)
    {
        if (requireName)
            RuleFor(r => r.Name).Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Type a name, or press Not now.");
        RuleFor(r => r.Name)
            .Must(n => n is null || n.Trim().Length <= RenameRegularRequestDto.MaxLength)
            .WithMessage($"Names are at most {RenameRegularRequestDto.MaxLength} characters.")
            .Must(n => string.IsNullOrWhiteSpace(n) || n.Any(char.IsLetterOrDigit))
            .WithMessage("Use at least one letter or number.");
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
