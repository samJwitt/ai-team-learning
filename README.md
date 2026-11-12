# Homelab Inventory

Homelab Inventory is a web app for keeping track of the hardware and services
running in your home lab. It is a .NET 10 Blazor Web App using interactive
server rendering.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Run the app

```sh
dotnet run --project src/HomelabInventory.Web
```

## Run the tests

```sh
dotnet test
```

## Tracked apps

The inventory lives at the `/apps` route: list, add, view, edit, and delete
tracked applications. Records are stored in SQLite; the database file lives at
`App_Data/homelab.db` under the app's content root by default, configurable via
the `Database:Path` setting in `appsettings.json`. The file is gitignored and
data survives app restarts.

Schema changes are applied with real EF Core migrations on startup
(`db.Database.Migrate()`) so existing installs gain new tables without data
loss. Migration tooling (dotnet-ef 10.0.x) is pinned in
`.config/dotnet-tools.json`; run `dotnet tool restore` before adding a
migration.

## Run update

Each tracked app's detail page has a **Run update** action that drives a
three-stage pipeline and records every attempt as an `UpdateRun` (status,
timestamps, image tag, full log) shown as history under the button:

1. **Fetch** — clone/fetch the app's `RepoUrl` at `Branch` into the work dir
   and resolve the latest commit SHA.
2. **Build** — build the container image tagged `{ContainerImage}:{short-sha}`
   from the repo checkout (the repo must contain a `Dockerfile`).
3. **Deploy** — stream the image to the host in `DeployedOn` over SSH
   (`docker save | ssh <host> docker load`) and restart its container. No
   registry and no credentials are stored in the app; SSH uses the invoking
   user's existing key-based auth.

Runs are synchronous in this first cut — one update at a time; a concurrent
request is refused. An app missing `RepoUrl`, `ContainerImage`, or
`DeployedOn` is refused before the build starts. The git clone/build work dir
defaults to `App_Data/update-work`, configurable via `Update:WorkDir`.

### Container runtime: docker (not podman)

The environment dictates **docker** (the host daemon and the LAN VM both run
docker), so `DockerCliImageBuilder` and `SshDockerDeployer` shell out to the
`docker` CLI. The choice is isolated behind the `IImageBuilder` / `IDeployer`
interfaces in `Services/Update/PipelineInterfaces.cs`: to switch to podman,
implement those two interfaces against `podman build` /
`podman save | ssh <host> podman load` (or `podman build --remote`) and
swap the registrations in `Program.cs` — nothing else changes. `IGitSource`
(`GitCliSource`) is runtime-independent.
