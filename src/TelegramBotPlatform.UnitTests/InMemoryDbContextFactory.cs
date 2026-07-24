using Microsoft.EntityFrameworkCore;
using TelegramBotPlatform.Persistence;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// Creates a <see cref="PlatformDbContext"/> backed by the EF Core in-memory provider,
/// so repository tests stay pure (no network, no filesystem, no real database).
/// </summary>
internal static class InMemoryDbContextFactory
{
    public static PlatformDbContext Create() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}