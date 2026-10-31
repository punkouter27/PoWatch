// ES module imported by .NET (JSHost.ImportAsync, DayReplay.razor) so a replay's motion grids cross
// from C# to replay.js as raw bytes ([JSImport] with a MemoryView) instead of a base64 string in JSON.
// The view points into .NET memory and is only valid during the call, so it is copied out first.
export function stage(day, grids) {
    globalThis.powatchReplay?.stage(day, grids.slice());
}
