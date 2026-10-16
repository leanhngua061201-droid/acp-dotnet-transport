# Contributing

Small, focused fixes and reproducible bug reports are welcome. This project is
an experimental transport component; protocol methods and host policy belong
to the embedding application. Contributions are made available under the
project's MIT license.

## Verify a change

Install the .NET 8 SDK and run the commands in [README.md](README.md). On Windows,
`powershell -NoProfile -ExecutionPolicy Bypass -File ./verify.ps1` runs the same
builds, console checks and local example. The console suite is run with
`dotnet run`, not `dotnet test`.

For a transport bug, add a focused controlled-stream regression that fails before
the fix. Include the platform, .NET version, reproduction steps and expected
behavior. Never include credentials, private configuration or raw conversations.

## Keep the boundary small

- Keep the library free of third-party package dependencies.
- Preserve caller ownership of streams and document changes to cancellation,
  message ordering, limits or shutdown behavior.
- Keep real-agent and model-service checks opt-in; normal verification and CI
  must work without credentials or network calls to a model service.
- Describe measured results precisely. A text round trip does not establish
  every ACP method or agent capability.

Please keep pull requests focused and describe the behavior change and the
checks you ran. Larger API changes are best discussed in an issue first.
