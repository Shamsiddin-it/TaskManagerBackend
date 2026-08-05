using System.Reflection;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// Enforces the Clean Architecture dependency direction declared in the implementation plan.
/// </summary>
/// <remarks>
/// <c>TEST-SEC-023</c>. These rules are cheap to state and expensive to restore once violated:
/// a single <c>using Microsoft.EntityFrameworkCore</c> in Application is enough to make persistence
/// concerns leak into business code and to make the isolation checks of TZ section 9 untestable
/// without a database.
/// </remarks>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(BaseEntity).Assembly;
    private static readonly Assembly Contracts = typeof(ErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(IClock).Assembly;
    private static readonly Assembly Infrastructure = typeof(MentorTaskFlowDbContext).Assembly;

    private const string DomainNamespace = "MentorTaskFlow.Domain";
    private const string ContractsNamespace = "MentorTaskFlow.Contracts";
    private const string ApplicationNamespace = "MentorTaskFlow.Application";
    private const string InfrastructureNamespace = "MentorTaskFlow.Infrastructure";
    private const string ApiNamespace = "MentorTaskFlow.Api";

    [Fact]
    public void Domain_depends_on_nothing_in_the_solution()
    {
        Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(ContractsNamespace, ApplicationNamespace, InfrastructureNamespace, ApiNamespace)
            .GetResult()
            .ShouldPass("Domain must not reference any other project.");
    }

    [Fact]
    public void Domain_does_not_know_about_persistence_or_the_web()
    {
        Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql")
            .GetResult()
            .ShouldPass("Domain must not reference EF Core, ASP.NET Core or Npgsql.");
    }

    [Fact]
    public void Contracts_depend_on_nothing_in_the_solution()
    {
        Types.InAssembly(Contracts)
            .ShouldNot()
            .HaveDependencyOnAny(DomainNamespace, ApplicationNamespace, InfrastructureNamespace, ApiNamespace)
            .GetResult()
            .ShouldPass("Contracts must not reference any other project.");
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, ApiNamespace)
            .GetResult()
            .ShouldPass("Application must depend only on Domain and Contracts.");
    }

    /// <summary>
    /// Application talks to persistence only through abstractions. Once entities exist (Phase 1),
    /// this is what forces every query through a tenant-scoped repository or specification
    /// (<c>SEC-031</c>) instead of a bare <c>DbSet&lt;T&gt;</c>.
    /// </summary>
    [Fact]
    public void Application_does_not_reference_ef_core_or_npgsql()
    {
        Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql")
            .GetResult()
            .ShouldPass("Application must not reference EF Core or Npgsql directly.");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api()
    {
        Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOn(ApiNamespace)
            .GetResult()
            .ShouldPass("Infrastructure must not reference the API layer.");
    }
}
