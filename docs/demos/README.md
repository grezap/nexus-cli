# nexus-cli demo catalog

Each `*.json` file in this directory is a demo spec consumed by
`nexus demo {list, run, record}`. The shape is:

```json
{
  "id": "DEMO-NN-short-name",
  "title": "Human-readable title",
  "description": "One-paragraph blurb shown in the catalog listing.",
  "steps": [
    {
      "command": "nexus cluster-status",
      "waitAfterSeconds": 3
    }
  ]
}
```

## Fields

- `id` — unique identifier; convention `DEMO-NN-<kebab>`. Used both as the
  filename stem (`DEMO-01-cluster-status.json`) and as the runtime
  argument to `nexus demo run`/`record`.
- `title` — short human-readable name shown in `nexus demo list`.
- `description` — paragraph blurb (only emitted in `--json` output).
- `steps[]` — sequential shell commands. Each step:
  - `command` — passed verbatim to `cmd.exe /c` on Windows or
    `/bin/sh -c` on Linux. You can use redirects, pipes, `&&`, etc.
  - `waitAfterSeconds` — pause after the step completes (lets viewers
    see the output before the next step runs). For `demo record`, this
    becomes a `Sleep Ns` directive in the generated VHS `.tape`.

## Discovery

`JsonDemoCatalog` searches in this order:
1. `NEXUS_DEMOS_PATH` env var
2. `./docs/demos/` relative to the current working directory
3. `../docs/demos/` (useful when running from `artifacts/<rid>/`)

## Running

```pwsh
# List
nexus demo list

# Run a single demo (steps execute in your shell)
nexus demo run DEMO-01-cluster-status

# Generate a VHS .tape + render to GIF (vhs binary required on PATH)
nexus demo record DEMO-01-cluster-status --output-dir ./out
```

## VHS install (optional, only needed for `demo record`)

- Windows: `winget install charmbracelet.vhs`
- macOS: `brew install vhs`
- Linux: `scoop install vhs` or `go install github.com/charmbracelet/vhs@latest`

If `vhs` isn't on PATH, `demo record` still writes the `.tape` file
to disk and reports `VhsAvailable=false` with the install hint, so
you can render it manually later.
