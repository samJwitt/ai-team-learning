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
