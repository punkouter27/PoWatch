using System.Text.Json;
using System.Text.Json.Serialization;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Application.Services;

/// <summary>
/// Source-generated JSON for the chat client's structured replies (web defaults: camelCase, case-insensitive),
/// so the schema the model is given and the reply it is read back with need no reflection.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RecapRewrite))]
[JsonSerializable(typeof(AssistService.RuleReply))]
[JsonSerializable(typeof(AssistService.HeadlineReply))]
internal sealed partial class AiReplyJson : JsonSerializerContext;

/// <summary>A shared recap's frozen copy, in the default (PascalCase) shape existing share rows were written in.</summary>
[JsonSerializable(typeof(RecapDto))]
internal sealed partial class ShareJson : JsonSerializerContext;
