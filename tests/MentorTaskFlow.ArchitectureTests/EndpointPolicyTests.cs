using System.Reflection;
using MentorTaskFlow.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// <c>TEST-SEC-001</c>: every endpoint carries an explicitly assigned authorization policy.
/// </summary>
/// <remarks>
/// <c>SEC-001</c> refuses merge to an endpoint without one. Enforcing it as a test rather than by
/// review is the point: an action added in a hurry defaults to <i>reachable by anyone</i>, and that
/// failure is invisible until somebody exploits it.
/// </remarks>
public sealed class EndpointPolicyTests
{
    private static readonly IReadOnlySet<string> KnownPolicies = new HashSet<string>(StringComparer.Ordinal)
    {
        MtfPolicies.Authenticated,
        MtfPolicies.OrganizationAdmin,
        MtfPolicies.BranchAdmin,
        MtfPolicies.AnyAdmin,
        MtfPolicies.Lead,
        MtfPolicies.Mentor,
        MtfPolicies.LeadOrAdmin,
    };

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        typeof(Program).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialAssemblyName())
                .Select(m => (Controller: t, Action: m)));

    [Fact]
    public void Every_action_is_either_explicitly_anonymous_or_carries_a_policy()
    {
        var offenders = new List<string>();

        foreach (var (controller, action) in Actions())
        {
            // AllowAnonymous is an explicit decision, and the four endpoints that use it are exactly
            // the ones that cannot require a principal: login, refresh, the password flows and the
            // Telegram webhook, which authenticates by a shared secret instead.
            if (action.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            {
                continue;
            }

            var policy = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy
                ?? controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

            if (policy is null)
            {
                offenders.Add($"{controller.Name}.{action.Name} has neither [AllowAnonymous] nor a policy.");
                continue;
            }

            if (!KnownPolicies.Contains(policy))
            {
                offenders.Add($"{controller.Name}.{action.Name} uses unregistered policy '{policy}'.");
            }
        }

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// A bare <c>[Authorize]</c> only proves the caller signed in; it says nothing about role or
    /// administrative contour, which is precisely what <c>TEN-035</c> forbids leaving implicit.
    /// </summary>
    [Fact]
    public void No_action_relies_on_a_policyless_authorize_attribute()
    {
        var offenders = Actions()
            .Where(x =>
                x.Action.GetCustomAttribute<AllowAnonymousAttribute>() is null
                && x.Action.GetCustomAttribute<AuthorizeAttribute>() is { Policy: null, Roles: null }
                && x.Controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy is null)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToArray();

        offenders.ShouldBeEmpty();
    }
}

file static class MethodInfoExtensions
{
    /// <summary>Filters out property accessors and compiler-generated members.</summary>
    public static bool IsSpecialAssemblyName(this MethodInfo method) =>
        method.IsSpecialName || method.DeclaringType == typeof(object);
}
