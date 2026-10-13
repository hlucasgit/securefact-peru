using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace SecureFact.Platform.Persistence;

public static class QueryBatches
{
    /// <summary>
    /// Reads an ordered query a batch at a time, so a table of any size is walked without holding a connection while a slow reader takes the rows, or all the rows in memory. The order must be total
    /// (end it with the key): with a tie, a row could come twice or never.
    /// </summary>
    public static async IAsyncEnumerable<T> InBatchesAsync<T>(this IQueryable<T> source, int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        for (var skip = 0; ; skip += batchSize)
        {
            var batch = await source.Skip(skip).Take(batchSize).ToListAsync(cancellationToken);
            foreach (var item in batch)
            {
                yield return item;
            }

            if (batch.Count < batchSize)
            {
                yield break;
            }
        }
    }
}
