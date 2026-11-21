/**
 * Longest text, in characters, the API accepts in each field. The server checks the same
 * limits (`server/src/Escribot.Api/Api/AiEndpoints.cs` and `WritingEndpoints.cs`) and answers
 * 400 past them; these keep a field from taking more than that in the first place.
 */
export const TEXT_LIMITS = {
  essay: 20_000,
  scratchNotes: 10_000,
  outlineField: 5_000,
  promptText: 20_000,
  shortText: 300,
  drillResponse: 20_000,
  vaultText: 20_000,
} as const;
