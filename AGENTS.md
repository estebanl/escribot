# Notes for coding agents

Escribot is one ASP.NET Core 11 app with a React browser app, deployed to Azure Container Apps. See the README for the full picture.

- `client/`: the browser app (Vite, React 19, React Router, Tailwind 4). Check with `npm run lint && npm run typecheck && npm run build` in `client/`.
- `server/`: the API (`src/Escribot.Api`), the Aspire AppHost that runs it locally with the Cosmos DB emulator and Azurite (`src/Escribot.AppHost`), and the xUnit tests. Check with `dotnet build` and `dotnet test` in `server/`; tests in the `Integration` category need a container runtime.
- `docs/architecture.md`: how the parts fit together. Update it with any change to the components, the request pipeline in `Program.cs` or the Azure topology.
- `server/docs/`: how sign-in, the data API, the AI layer, billing and the Cosmos DB data model work. Update them with any change to endpoints, sign-in, AI routing or quotas, or stored documents.
- `infra/` and `.github/workflows/deploy.yml`: the Azure deployment.
- `scripts/test-isolation.mjs`: the end-to-end tenant-isolation test.

Rules that hold everywhere:

- Every user-owned document is read and written through the repositories with the owner's user id. Another user's data answers 404, never 403; signed-out API calls answer 401.
- No secret has a value in the repository, in code, settings or tests. Secrets reach the API as environment variables (user secrets locally, Key Vault in Azure).
- The JSON the browser app receives is shaped by `server/src/Escribot.Api/Api/Wire.cs`; change it and `client/src/lib/types.ts` together.
