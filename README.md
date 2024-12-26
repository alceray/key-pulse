# KeyPulse
Introducing the keyboard and mice monitoring application that is tailored for enthusiasts but can be enjoyed by anyone. It allows you to monitor the connection status and usage of USB devices on your Windows computer.

## Development Notes
In Developer PowerShell, these commands can be ran for database migration:
- `dotnet ef migrations add <MigrationName>`: Creates a new migration file in `/Migrations` to reflect the current model state.
- `dotnet ef migratiosn remove <MigrationName>`: Removes the last unapplied migration file in `/Migrations`.
- `dotnet ef database update`: Applies any pending migrations to the database, updating the schema without losing existing data.
- `dotnet ef database update <MigrationName>`: Updates database to the given migration, reverting the later migrations.