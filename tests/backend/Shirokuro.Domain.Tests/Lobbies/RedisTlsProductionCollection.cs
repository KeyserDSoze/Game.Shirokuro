using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// The TLS-only fixture is shared by these tests. The outage test deliberately
/// pauses the Redis container, so all Production Redis TLS test classes must
/// execute serially rather than racing a host startup or a restore drill.
/// </summary>
[CollectionDefinition("Production Redis TLS integration", DisableParallelization = true)]
public sealed class RedisTlsProductionCollection;
