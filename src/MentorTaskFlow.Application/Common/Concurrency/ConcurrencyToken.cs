using System.Buffers.Text;
using System.Text;
using MentorTaskFlow.Application.Common.Exceptions;

namespace MentorTaskFlow.Application.Common.Concurrency;

/// <summary>
/// Converts PostgreSQL's <c>xmin</c> into the opaque string clients echo back (<c>API-020</c>).
/// </summary>
/// <remarks>
/// <para>
/// The wire format is Base64Url of the decimal <c>xmin</c>. It is <b>an implementation detail</b>: a
/// client has no right to parse, compare or order it, and encoding it makes that obvious. Handing the
/// raw number over would invite a client to "helpfully" increment it, which turns optimistic
/// concurrency into no concurrency control at all.
/// </para>
/// <para>
/// A physical version column is not used. SQL Server's <c>rowversion</c> of version 2.0 has no
/// PostgreSQL equivalent, and a hand-maintained counter would put the burden of incrementing it on
/// every write path (<c>DEPLOY-006</c>).
/// </para>
/// </remarks>
public static class ConcurrencyToken
{
    public static string Encode(uint xmin) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(xmin.ToString()));

    /// <summary>
    /// Decodes a token supplied by a client.
    /// </summary>
    /// <remarks>
    /// A malformed value is <b>not</b> a concurrency conflict but a bad request: the client sent
    /// something that was never issued by this server, and reporting 409 would tell it to reload and
    /// retry a request that will fail identically forever.
    /// </remarks>
    public static uint Decode(string? token, string fieldName = "concurrencyToken")
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            // API-020: absence, where the endpoint requires it, is 400 VALIDATION_FAILED.
            throw new ValidationAppException(fieldName, "Поле concurrencyToken обязательно для этой операции.");
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));

            return uint.TryParse(decoded, out var xmin)
                ? xmin
                : throw new ValidationAppException(fieldName, "Некорректное значение concurrencyToken.");
        }
        catch (FormatException)
        {
            throw new ValidationAppException(fieldName, "Некорректное значение concurrencyToken.");
        }
    }
}
