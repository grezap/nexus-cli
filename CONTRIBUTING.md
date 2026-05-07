# Contributing to nexus-cli

Thanks for the interest. This is a portfolio project authored solely by
Grigoris Zapantis (`@grezap`); the commit author/owner of every change is
expected to be Greg. PRs that fix bugs, add tests, or sharpen documentation
are welcome — please open an issue first if the change is non-trivial.

## Ground rules

- **Single-author convention.** Commits do **not** carry `Co-Authored-By:`
  trailers, generation-marker tags, or any "assisted by …" footer. If a PR
  needs significant rewrite to fit, expect that to happen on this side.
- **Conventional Commits.** Subject line in the form
  `<type>(<scope>): <imperative summary>`. Examples:
  - `feat(cluster-status): add --json output`
  - `fix(adapters): handle 503 from portainer api`
  - `chore(ci): bump setup-dotnet to v4`
- **No force-pushes** to `main`. Force-pushes inside an open feature branch
  are fine.
- **No GPG-bypass / no `--no-verify`.** Hooks fail = fix the underlying
  issue.

## Local devloop

```pwsh
git clone https://github.com/grezap/nexus-cli
cd nexus-cli

# everything below uses the pwsh-native operator wrapper. There is no Makefile.
pwsh -File scripts\cli.ps1 build
pwsh -File scripts\cli.ps1 test
pwsh -File scripts\cli.ps1 lint
pwsh -File scripts\cli.ps1 publish -Rid win-x64
pwsh -File scripts\cli.ps1 size-check -Rid win-x64
```

Or roll-up: `pwsh -File scripts\cli.ps1 publish -Rid all` builds both RIDs and
asserts the 25 MB exit gate on each.

## What CI gates on

- `dotnet build -c Release -warnaserror` (zero warnings; trim-analyzer
  warnings are errors via `.editorconfig`)
- `dotnet test` (xUnit; NetArchTest layer rules included)
- `dotnet publish -c Release -r linux-x64` AND `-r win-x64` (Native AOT)
- Published binary size ≤ 25 MB per RID

## What I won't merge

- Reflection-heavy code in the publish project — kills AOT.
- Hand-rolled JSON parsing for shapes already covered by
  `Nexus.Cli.Adapters/Json/NexusJsonContext.cs` source-gen.
- New external secrets paths that aren't routed through the Vault token
  resolver in `Nexus.Cli.Adapters/Vault/VaultTokenResolver.cs`.
- Features without tests, ADRs without context, or commands without a help
  example.

## Reporting issues

Use the GitHub issue tracker. For lab-specific reproductions, paste the
sanitized output of `nexus cluster-status --json --verbose` (it never logs
tokens — verbose only enriches HTTP timing).
