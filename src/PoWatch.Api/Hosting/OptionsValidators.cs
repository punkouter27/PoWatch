using Microsoft.Extensions.Options;
using PoWatch.Application.Options;

namespace PoWatch.Api.Hosting;

// The startup-critical options' data annotations, checked by generated code instead of the
// reflection that ValidateDataAnnotations() uses. Registered in Program.cs with ValidateOnStart().
[OptionsValidator]
internal sealed partial class AzureStorageOptionsValidator : IValidateOptions<AzureStorageOptions>;

[OptionsValidator]
internal sealed partial class AzureOpenAiOptionsValidator : IValidateOptions<AzureOpenAiOptions>;
