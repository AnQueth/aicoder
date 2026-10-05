using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPersistence(builder.Configuration);
using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<CollaboratorDb>();
Console.WriteLine("Phase 1/1: applying PostgreSQL migrations.");
var started = System.Diagnostics.Stopwatch.StartNew();
await db.Database.MigrateAsync();
Console.WriteLine($"Migration summary: processed=1 succeeded=1 failed=0 skipped=0 elapsed={started.Elapsed}.");
