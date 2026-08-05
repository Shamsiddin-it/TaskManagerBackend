using MentorTaskFlow.Application.Common.Abstractions;

namespace MentorTaskFlow.Infrastructure.Common;

/// <summary>The single place in the codebase allowed to read the machine clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
